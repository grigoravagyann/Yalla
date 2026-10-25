using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The earlier guest answers, in the app, a later guest's request for their table: yes holds them to the
/// time and confirms the later booking; no declines it.
/// </summary>
internal sealed class ReservationAnswers(
    YallaDbContext db,
    IClock clock,
    ReservationWriter reservations,
    IOutbox outbox,
    IActivityLog activity,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationViews views)
{
    // ---------------------------------------------------------------- the earlier guest answers

    /// <summary>What an answer to a request for a table is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string AnswerTableRequestCommandType = "reservation.table-request.answer";

    public async Task<ReservationView> AnswerTableRequestAsync(
        AnswerTableRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = lookups.RequireDiner("Answer a request for your table");

        var earlier = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == command.ReservationId, cancellationToken);

        // Somebody else's booking answers exactly as one that does not exist, as for a change.
        if (earlier is null || earlier.DinerUserId != dinerUserId)
        {
            throw new KeyNotFoundException($"Reservation {command.ReservationId} was not found.");
        }

        // A retry of this answer: the booking as it stands now. The same id on anything else is refused.
        if (await IsAnswerReplayAsync(command, dinerUserId, cancellationToken))
        {
            return await views.ReplayViewAsync(earlier.Id, cancellationToken);
        }

        // Only a request put to this guest in the app is theirs to answer; one the floor is deciding
        // was never shown to them.
        var request = await db.TableShareRequests
            .FirstOrDefaultAsync(
                r => r.Id == command.RequestId && r.EarlierReservationId == earlier.Id && r.AsksEarlierGuest,
                cancellationToken)
            ?? throw new KeyNotFoundException($"Request {command.RequestId} was not found on this booking.");

        var nowUtc = clock.UtcNow;
        var branch = await lookups.LoadBranchAsync(earlier.BranchId, cancellationToken);

        if (!request.IsOpenAt(nowUtc))
        {
            // The same answer again - a second tap, the notification's action and then the card - is
            // answered with the booking, not refused. Anything else is too late.
            var sameAnswer = request.Status == (command.Accept ? TableShareRequestStatus.Accepted : TableShareRequestStatus.Declined);

            if (sameAnswer)
            {
                return await views.ToViewAsync(earlier, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
            }

            throw new TableRequestNotPendingException(earlier.Id, request.Id, request.Status, request.ExpiresAtUtc);
        }

        var later = await db.Reservations.FirstOrDefaultAsync(r => r.Id == request.LaterReservationId, cancellationToken);

        if (later is not { Status: ReservationStatus.PendingApproval } || later.ApprovalHasLapsedAt(nowUtc))
        {
            throw new TableRequestNotPendingException(earlier.Id, request.Id, request.Status, request.ExpiresAtUtc);
        }

        db.ProcessedCommands.Add(new ProcessedCommand(
            command.ClientCommandId,
            AnswerTableRequestCommandType,
            ActorType.Diner,
            dinerUserId,
            JsonSerializer.Serialize(new AnswerRecord(earlier.Id, request.Id, command.Accept), ReservationChanges.RecordJson),
            ReservationChanges.RescheduleStatusCode,
            nowUtc));

        try
        {
            if (command.Accept)
            {
                // Yes: the later booking is confirmed, this one held to the time - its end cut to it -
                // and both diners told.
                later.Confirm(nowUtc);

                await TableShares.ApplyYesAsync(db, outbox, later.Id, branch, nowUtc, cancellationToken);

                await BookingActivity.RecordAsync(
                    activity, db, later, ActivityActions.BookingGuestAnswered, cancellationToken,
                    extra: new { answer = "table-request", accepted = true, earlierReservationId = earlier.Id },
                    byGuest: true);

                var key = await ReservationNotices.DecisionKeyAsync(db, later.Id, "approved", nowUtc, cancellationToken);

                ReservationNotices.TellDinerDecided(db, outbox, later, branch, approved: true, expired: false, key, nowUtc);

                await reservations.SaveAsync(cancellationToken);
            }
            else
            {
                // No: the later booking is declined - or, for a change of a confirmed one, goes back to the
                // slot it had - and its diner told. Saved inside, with this guest's own record of it.
                await TableShares.TellEarlierDecidedAsync(
                    db, outbox, earlier, request, branch, accepted: false, nowUtc, cancellationToken);

                await PendingDecline.DeclineAsync(
                    db,
                    outbox,
                    reservations,
                    later,
                    branch,
                    DeclineReason.EarlierGuestDeclined,
                    reason: null,
                    quietIfAlreadyStarted: false,
                    nowUtc,
                    cancellationToken,
                    ct => BookingActivity.RecordAsync(
                        activity, db, later, ActivityActions.BookingGuestAnswered, ct,
                        extra: new { answer = "table-request", accepted = false, earlierReservationId = earlier.Id },
                        byGuest: true));
            }
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException
                                   || (ex is DbUpdateException update && UniqueViolation.IsOn(update, DatabaseIndexNames.OutboxIdempotency)))
        {
            // The later booking or the request changed under this answer - the sweep expired it, the other
            // diner moved or cancelled, the floor decided, or this guest's other tap answered first (whose
            // pushes this one's collided with). Whatever they did stands.
            db.ChangeTracker.Clear();

            var current = await db.TableShareRequests
                .AsNoTracking()
                .Where(r => r.Id == request.Id)
                .Select(r => new { r.Status, r.ExpiresAtUtc })
                .FirstAsync(cancellationToken);

            // Two quick taps with two ids, the same answer: the first took effect, and the second is
            // answered with the booking as the first left it. Only the other answer is too late.
            if (current.Status == (command.Accept ? TableShareRequestStatus.Accepted : TableShareRequestStatus.Declined))
            {
                return await views.ReplayViewAsync(earlier.Id, cancellationToken);
            }

            throw new TableRequestNotPendingException(earlier.Id, request.Id, current.Status, current.ExpiresAtUtc);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            // The same id was recorded by a racing request. Answered outside as a replay - or refused,
            // when it was another command's.
            db.ChangeTracker.Clear();

            if (await IsAnswerReplayAsync(command, dinerUserId, cancellationToken))
            {
                return await views.ReplayViewAsync(command.ReservationId, cancellationToken);
            }

            throw;
        }

        logger.LogInformation(
            "Booking {Code} answered the request for its table from booking {LaterCode}: {Answer}.",
            earlier.Code, later.Code, command.Accept ? "yes" : "no");

        var fresh = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == earlier.Id, cancellationToken);

        return await views.ToViewAsync(fresh, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
    }

    /// <summary>
    /// Whether this answer was already given under this id - or throws when the id belongs to another
    /// command, another booking's request, or the other answer.
    /// </summary>
    private async Task<bool> IsAnswerReplayAsync(
        AnswerTableRequestCommand command,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .Where(c => c.ClientCommandId == command.ClientCommandId)
            .Select(c => new { c.CommandType, c.ActorId, c.ResponseJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (processed is null)
        {
            return false;
        }

        if (processed.CommandType != AnswerTableRequestCommandType
            || processed.ActorId != dinerUserId
            || JsonSerializer.Deserialize<AnswerRecord>(processed.ResponseJson, ReservationChanges.RecordJson) is not { } record
            || record.ReservationId != command.ReservationId
            || record.RequestId != command.RequestId
            || record.Accept != command.Accept)
        {
            throw new ClientCommandIdAlreadyUsedException(
                command.ClientCommandId,
                "That clientCommandId already belongs to another command. Generate a fresh one per answer, "
                + "and reuse it only when retrying that same answer.");
        }

        return true;
    }

    /// <summary>What an answer records: which booking, which request, and which way.</summary>
    private sealed record AnswerRecord(Guid ReservationId, Guid RequestId, bool Accept);
}
