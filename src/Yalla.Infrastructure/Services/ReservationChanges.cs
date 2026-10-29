using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner moves their booking - in the app, or with its manage link: the replay and the gates, the target
/// table's lock, and everything that follows the booking to its new slot.
/// </summary>
internal sealed class ReservationChanges(
    YallaDbContext db,
    IClock clock,
    ReservationWriter reservations,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationSlotRules slots,
    ReservationViews views)
{
    // ---------------------------------------------------------------- the diner moves their booking

    /// <summary>What a change of booking is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string RescheduleCommandType = "reservation.reschedule";

    /// <summary>The status a change answers with, recorded beside it as every processed command's is.</summary>
    internal const int RescheduleStatusCode = 200;

    public async Task<ReservationView> RescheduleAsync(
        RescheduleReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = lookups.RequireDiner("Change a booking");

        // As for booking: the moved booking's reminder and no-show record land on the number, so it
        // must be one the diner has proved.
        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Changing a booking", cancellationToken);

        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == command.ReservationId, cancellationToken);

        // Somebody else's booking answers exactly as one that does not exist. Cancelling says "not
        // yours"; a change would go on to judge a slot on somebody else's booking, and a stranger
        // guessing ids should not even learn that the id is one.
        if (reservation is null || reservation.DinerUserId != dinerUserId)
        {
            throw new KeyNotFoundException($"Reservation {command.ReservationId} was not found.");
        }

        return await MoveAsync(reservation, command, dinerUserId, viaManageLink: false, cancellationToken);
    }

    /// <summary>
    /// Moves a booking with its manage token: the diner's move, with the link as the proof.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One move, two ways in</b>, as cancelling is: the token finds the booking exactly as the
    /// manage link's read and cancel find it - unknown, expired and gone answering identically - and
    /// from there it is <see cref="MoveAsync"/>, the diner's move. The token is the credential and
    /// names one booking; there is no id in the command to point it at another.
    /// </para>
    /// <para>
    /// <b>The phone gate is the booking's account's.</b> It was never about who is asking: the moved
    /// booking's reminder and no-show record land on the account's number, so that number must still
    /// be a proved one, whoever holds the link. A booking whose account was deleted has no account to
    /// land on - the deletion cut the link to it - and is refused the same way, as the gate refuses an
    /// account row that is not there. Such a booking can still be read and cancelled from its link.
    /// </para>
    /// <para>
    /// <b>The change is recorded as the diner's</b> - the booking's account is the actor in
    /// <c>ProcessedCommands</c> - so a retry is recognised whichever way it arrives, and an id that
    /// belongs to another booking, or to a different change of this one, is the same
    /// <c>client-command-id-in-use</c> it is for the app.
    /// </para>
    /// <para>
    /// <b>The one answer that is narrower than the diner's</b> is a clash with another of the diner's
    /// bookings: <see cref="ManageLinkOverlapsOwnException"/> instead of
    /// <see cref="ReservationOverlapsOwnException"/>, the same 409 and code with nothing about the other
    /// booking - no time, table or code. The code still tells that such a booking exists: the writer
    /// checks the diner's own bookings before the table's, for the app as for the link, and reordering
    /// that shared check for one caller is not worth what it would hide from somebody already holding
    /// the diner's link to the same venue.
    /// </para>
    /// <para>
    /// <b>The account's liveness is the gate's answer here too.</b> The account's lock inside the move
    /// refuses an account deactivated or deleted since the gate as a session ending (401); there is no
    /// session on a link, so it is <see cref="ManageLinkAccountUnavailableException"/>, the gate's 403.
    /// </para>
    /// <para>
    /// <b>The booking's account is told</b> when the move is not a retry: a push and a feed entry in the
    /// same save (see <c>TellDinerDoneFromLink</c>), because whoever holds the link may not be the diner,
    /// and a booking moved without their knowing becomes their no-show.
    /// </para>
    /// </remarks>
    public async Task<ReservationView> RescheduleByManageTokenAsync(
        string manageToken,
        RescheduleByManageTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await lookups.FindByManageTokenAsync(manageToken, cancellationToken);

        // The account the move would land on: none left (deleted), not live, or without a proved number -
        // the app's 403, in words the link holder can act on (they have no account to verify).
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            throw new ManageLinkAccountUnavailableException();
        }

        try
        {
            await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Changing a booking", cancellationToken);
        }
        catch (Domain.Identity.PhoneNotVerifiedException)
        {
            throw new ManageLinkAccountUnavailableException();
        }

        var move = new RescheduleReservationCommand(
            reservation.Id,
            command.TableId,
            command.LocalDate,
            command.LocalTime,
            command.PartySize,
            command.ClientCommandId,
            command.AcceptsFreeByUtc);

        ReservationView moved;

        try
        {
            moved = await MoveAsync(reservation, move, dinerUserId, viaManageLink: true, cancellationToken);
        }
        catch (ReservationOverlapsOwnException)
        {
            throw new ManageLinkOverlapsOwnException();
        }
        catch (Domain.Identity.AuthenticationFailedException)
        {
            // The account stopped being live between the gate and its lock inside the move. On the app that
            // is the diner's session ending; an anonymous link has no session, so it is the gate's answer.
            throw new ManageLinkAccountUnavailableException();
        }

        if (!moved.WasReplay)
        {
            // Which way in, for the venue answering "I never moved it" - the move's own line says where to.
            logger.LogInformation(
                "Booking {Code} was changed from its manage link, not the app; its account was told.", moved.Code);
        }

        return moved;
    }

    /// <summary>
    /// The move itself, for a caller already proved entitled to move <paramref name="reservation"/> and
    /// whose phone gate has passed - the diner's own booking, or the booking behind a manage link.
    /// </summary>
    /// <param name="reservation">The booking, tracked.</param>
    /// <param name="command">The change.</param>
    /// <param name="dinerUserId">The booking's account: the actor recorded, and whose bookings and history count.</param>
    /// <param name="viaManageLink">
    /// Whether whoever holds the booking's link made the change, rather than the diner in the app: the
    /// account holder is then told, in the same save - see <see cref="ReservationNotices.TellDinerDoneFromLink"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<ReservationView> MoveAsync(
        Reservation reservation,
        RescheduleReservationCommand command,
        Guid dinerUserId,
        bool viaManageLink,
        CancellationToken cancellationToken)
    {
        // The branch's zone first, because a replay is judged by where the change asked to go: the
        // start a retry names is a local time, and the record holds the instant it became.
        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);
        var zone = BranchZone.For(branch.TimeZoneId);

        // Cheap path first, as for creation: a retry that arrives after the original committed is
        // answered with the booking as it stands and never reaches the lock.
        if (await IsRescheduleReplayAsync(command, dinerUserId, zone, cancellationToken))
        {
            return await views.ReplayViewAsync(reservation.Id, cancellationToken);
        }

        // A guest blocked for no-shows moves nothing - on the app or by the link. Again under the lock, in the writer.
        await GuestRecords.RequireNotBlockedAsync(
            db, dinerUserId, reservation.BookerPhoneE164, clock.UtcNow, cancellationToken);

        var nowUtc = clock.UtcNow;

        // Before any rule about the new slot. A booking that is cancelled, seated or already under way
        // is not going to move, and answering "that table is taken" about it would send the diner
        // looking for another table for nothing.
        reservation.RequireChangeableAt(nowUtc);

        // And a short-notice request nobody answered in time is the sweep's to decline, not the diner's
        // to restart: a move now would hand the floor a fresh fifteen minutes for a question it let
        // run out. 409 reservation-approval-expired.
        reservation.RequireAnswerWindowOpenAt(nowUtc);

        // Every gate creation applies: a move is a new promise about a slot, so a venue that stopped
        // trading - or stopped taking bookings from where this one came from - does not make it.
        VenueGate.RequireOpenForBusiness(branch);
        ReservationSlotRules.RequireChannelAccepted(reservation.Channel, branch, viaManageLink);

        // A booking made by time only is moved by cancelling it and booking the new time (2026-10-29):
        // its move would be judged against the branch's guests at once, not a table's evening.
        if (reservation.BookedWithoutTable)
        {
            throw new Yalla.Domain.DomainStateException(
                $"Booking {reservation.Code} was made by time only and cannot be moved; cancel it and book the new time.");
        }

        // At the booking's own branch: a move changes the slot, never the venue.
        var table = await lookups.LoadTableAsync(reservation.BranchId, command.TableId, cancellationToken);
        var policy = branch.ReservationPolicy;

        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var proposed = new BookedInterval(startUtc, startUtc.AddMinutes(policy.TurnTimeMinutes));
        var pinned = await slots.IsPinnedOnItsRoomAsync(table, cancellationToken);
        var hours = await lookups.HoursAroundAsync(branch.Id, command.LocalDate, cancellationToken);

        ReservationSlotRules.Validate(
            command.LocalDate, command.LocalTime, command.PartySize, table, pinned, hours, policy, zone, proposed, nowUtc);

        // Decided again, by creation's rules - with one thing creation never has: a party somebody
        // already confirmed. Twelve people the venue confirmed are still twelve people it has said
        // yes to at nine instead of seven, so the size threshold alone does not send them back; a
        // party that grew past what was confirmed does, and so do a venue that approves every
        // booking and a no-show history, which are about the diner and the venue, not the size.
        // A party that shrank under the threshold no longer needs anybody.
        var approvedPartySize = reservation.Status == ReservationStatus.Confirmed
            ? reservation.PartySize
            : (int?)null;

        // And one thing no rule needs asking about: a confirmed booking that keeps its table and its
        // start and does not grow. Nothing the venue said yes to got any harder to keep - the same
        // table at the same time, for as many people or fewer - so it stays confirmed, tonight
        // included. Anything else is a new request about a table, and a move into tonight goes to the
        // floor with a fresh fifteen minutes, as a new booking would.
        var keepsItsConfirmation = reservation.Status == ReservationStatus.Confirmed
                                   && table.Id == reservation.DiningTableId
                                   && startUtc == reservation.StartUtc
                                   && command.PartySize <= reservation.PartySize;

        var status = keepsItsConfirmation
            ? new ReservationSlotRules.StatusDecision(ReservationStatus.Confirmed, null, null)
            : await slots.DecideStatusAsync(
                command.PartySize,
                approvedPartySize,
                policy,
                dinerUserId,
                startUtc,
                ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc)),
                nowUtc,
                cancellationToken);

        var shortNoticeMove = ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc));
        ReservationSlotRules.StatusDecision decided = status;

        var outcome = await MoveUnderTheTableLockAsync(
            reservation,
            table,
            startUtc,
            policy,
            new BookerKey(dinerUserId, reservation.BookerPhoneE164),
            command.ClientCommandId,
            command.AcceptsFreeByUtc,
            keepsItsConfirmation,
            async (verdict, ct) =>
            {
                // A confirmed booking keeping its table and start is not asked about again: nothing the
                // venue said yes to got harder to keep, whoever has the table before it.
                var (decision, ask) = keepsItsConfirmation
                    ? (status, (ReservationSlotRules.ShareAsk?)null)
                    : await slots.DecideWithTheTableAsync(verdict, status, shortNoticeMove, startUtc, nowUtc, ct);

                // A waiting booking that moves never gets a later deadline than it had: moving the request
                // around does not restart a clock - fifteen minutes or two hours - nor turn one into the
                // other. Never past the new start either, which the new deadline already respects.
                if (reservation is { Status: ReservationStatus.PendingApproval, ApprovalExpiresAtUtc: { } had }
                    && decision.ApprovalExpiresAtUtc is { } fresh
                    && had < fresh)
                {
                    decision = decision with { ApprovalExpiresAtUtc = had };
                }

                // Every question has a clock - in the app, the floor's tonight, the floor's another day -
                // and it runs out when the booking's wait does.
                if (ask is { } timed && decision.ApprovalExpiresAtUtc is { } deadline)
                {
                    ask = timed with { ExpiresAtUtc = deadline };
                }

                decided = await StageMoveAsync(
                    reservation, command, table, branch, startUtc, decision, ask, verdict, dinerUserId, viaManageLink, nowUtc, ct);
            },
            cancellationToken);

        if (outcome.WasReplay)
        {
            // A retry of the same change committed while this one waited. Asked again out here
            // because the record the lock found may be somebody else's command, which is refused.
            await IsRescheduleReplayAsync(command, dinerUserId, zone, cancellationToken);

            return await views.ReplayViewAsync(reservation.Id, cancellationToken);
        }

        // Excluding this booking from the snapshot, as from the clash check: the floor the diner is
        // shown afterwards has their own table free, which is how they were looking at it.
        var snapshot = new AvailabilityRequest(
            reservation.BranchId, command.PartySize, command.LocalDate, command.LocalTime, reservation.Id);

        if (outcome.Occupied is { } sitting)
        {
            throw await slots.OccupiedAsync(snapshot, table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            throw await slots.ConflictAsync(snapshot, table, proposed, conflict, cancellationToken);
        }

        if (outcome.FreeBy is { } freeBy)
        {
            throw ReservationSlotRules.FreeByNotAccepted(table, freeBy, zone, command.AcceptsFreeByUtc);
        }

        logger.LogInformation(
            "Booking {Code} moved to table {TableLabel} at {Start:o} for {PartySize} ({Status}).",
            reservation.Code, table.Label, reservation.StartUtc, reservation.PartySize, reservation.Status);

        return await views.ToViewAsync(reservation, branch, table, wasReplay: false, decided.Trigger, cancellationToken);
    }

    /// <summary>
    /// The move itself, staged inside the target table's lock once every re-check has passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The reminder and the late nudge are replaced, not edited.</b> Both name the old time - in
    /// their payload and in when they fire - so the unsent ones are cancelled exactly as a
    /// cancellation cancels them, the reminder's feed entry with them, and new ones are written for
    /// the new start. The new keys carry the start they are for (<c>reminder:202610171930</c>),
    /// because a reminder already <i>sent</i> keeps its row and its key: moving tomorrow's booking to
    /// next week must not collide with the reminder that went out an hour ago.
    /// </para>
    /// <para>
    /// The manage link travels with the booking. Its plaintext exists only in the response that
    /// created the booking and in the messages written then, so a web booking's new reminder carries
    /// the link its old one did.
    /// </para>
    /// <para>
    /// The command is recorded in the same unit of work, so a move and the record that it happened
    /// commit together - a retry finds one exactly when there is the other.
    /// </para>
    /// </remarks>
    private async Task<ReservationSlotRules.StatusDecision> StageMoveAsync(
        Reservation reservation,
        RescheduleReservationCommand command,
        DiningTable table,
        Branch branch,
        DateTime startUtc,
        ReservationSlotRules.StatusDecision status,
        ReservationSlotRules.ShareAsk? ask,
        TableSequencingVerdict verdict,
        Guid dinerUserId,
        bool viaManageLink,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // The question this booking was waiting on. Still the same guest, asked the same way: the same
        // question at the new time - no second push to them, and its deadline never later than it was.
        // Otherwise it was about the slot this booking is leaving, and goes at once, inside the lock's
        // transaction, so a new one asked below finds it closed.
        var waitingOn = await TableShares.PendingForLaterAsync(db, reservation.Id, cancellationToken);

        if (waitingOn is not null
            && ask is { } same
            && waitingOn.EarlierReservationId == same.EarlierReservationId
            && waitingOn.AsksEarlierGuest == same.AsksEarlierGuest)
        {
            waitingOn.Retime(startUtc, same.FreeByUtc, same.ExpiresAtUtc, same.FallbackTrigger);

            if (waitingOn.AsksEarlierGuest)
            {
                status = status with { ApprovalExpiresAtUtc = waitingOn.ExpiresAtUtc };
            }

            ask = null;
        }
        else if (waitingOn is not null)
        {
            db.Entry(waitingOn).State = EntityState.Detached;
            await TableShares.WithdrawForLaterNowAsync(db, reservation.Id, nowUtc, cancellationToken);
        }

        // A promise this booking made for the slot it keeps stands; otherwise the new slot's own.
        var promise = verdict.MustFreeByUtc
                      ?? (reservation.StartUtc == startUtc
                          && reservation.PromisedFreeByUtc is { } kept
                          && kept < startUtc.AddMinutes(branch.ReservationPolicy.TurnTimeMinutes)
                          && kept > startUtc
                              ? kept
                              : null);

        // Throws if the booking stopped being changeable since it was read - though a change of state
        // since then has moved its row version, and the save would refuse it anyway.
        reservation.Reschedule(
            table.Id,
            startUtc,
            command.LocalDate,
            command.LocalTime,
            command.PartySize,
            branch.ReservationPolicy,
            status.Status,
            nowUtc,
            status.Trigger,
            status.ApprovalExpiresAtUtc,
            promise,
            viaManageLink);

        if (ask is { } question)
        {
            TableShares.Ask(
                db, reservation, question.EarlierReservationId, question.FreeByUtc, question.ExpiresAtUtc,
                question.AsksEarlierGuest, question.FallbackTrigger, nowUtc);
        }

        // And a guest who was asked to free their table for this one - or whose table this one was
        // asked for - is settled by where it now stands.
        await TableShares.EarlierChangedAsync(db, reservation, branch, nowUtc, cancellationToken);

        // The pre-order moves with the booking - judged against the new start wherever it is read - unless the kitchen
        // already has it: food cooked for seven cannot be moved to tomorrow (409 pre-order-already-sent). A waiter's
        // "not coming" was about the old time and goes. The row is saved even when nothing on it changed, so its row
        // version settles this move against a send racing it under the old table's lock.
        if (await db.PreOrders.FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken) is { } preOrder)
        {
            preOrder.FollowMove(nowUtc);
            db.Entry(preOrder).State = EntityState.Modified;
        }

        // The History's line, the reminders replaced for the new slot (the account is live: the writer took its lock,
        // inside this transaction, before calling here), the account told of a move from the link, the change stream
        // and - for a move back to waiting - the floor's push are the move's handlers'.
        db.ProcessedCommands.Add(new ProcessedCommand(
            command.ClientCommandId,
            RescheduleCommandType,
            ActorType.Diner,
            dinerUserId,
            JsonSerializer.Serialize(
                new RescheduleRecord(
                    reservation.Id, reservation.DiningTableId, reservation.StartUtc, reservation.PartySize, reservation.Status),
                RecordJson),
            RescheduleStatusCode,
            nowUtc));

        return status;
    }

    /// <summary>
    /// Whether this change was already applied - or throws when the id belongs to some other command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A replay is this diner sending this change of this booking again. The command id is unique
    /// across every command anybody sends, so the record holding it may be another diner's change,
    /// a change of another booking, or a waiter's table command - and answering any of those as a
    /// replay would hand the caller a booking that is not theirs. Those are a collision, refused as
    /// <c>client-command-id-in-use</c>.
    /// </para>
    /// <para>
    /// <b>So is a different change under the same id.</b> A phone that reuses the id it sent for
    /// "eight o'clock at table 4" to ask for "nine at table 6" is not retrying, and answering
    /// <c>wasReplay</c> with the booking still at eight would tell it the nine o'clock move was done.
    /// The record's table, start and party size must be the command's; anything else is the same
    /// 409, and the client mints a fresh id for the new change.
    /// </para>
    /// </remarks>
    private async Task<bool> IsRescheduleReplayAsync(
        RescheduleReservationCommand command,
        Guid dinerUserId,
        BranchZone zone,
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

        if (processed.CommandType != RescheduleCommandType
            || processed.ActorId != dinerUserId
            || JsonSerializer.Deserialize<RescheduleRecord>(processed.ResponseJson, RecordJson) is not { } record
            || !record.Records(command, zone))
        {
            logger.LogWarning(
                "Change command {ClientCommandId} reuses an id already held by another command or another change.",
                command.ClientCommandId);

            throw new ClientCommandIdAlreadyUsedException(
                command.ClientCommandId,
                "That clientCommandId already belongs to another command. Generate a fresh one per change, "
                + "and reuse it only when retrying that same change.");
        }

        logger.LogInformation(
            "Change command {ClientCommandId} was already applied; returning the booking as it stands.",
            command.ClientCommandId);

        return true;
    }

    /// <summary>
    /// The writer's locked move, with a lock timeout re-labelled as the booking-specific one - as
    /// <see cref="ReservationCreation.InsertUnderTheTableLockAsync"/> does for a new booking.
    /// </summary>
    private async Task<MoveOutcome> MoveUnderTheTableLockAsync(
        Reservation reservation,
        DiningTable table,
        DateTime startUtc,
        ReservationPolicy policy,
        BookerKey booker,
        Guid clientCommandId,
        DateTime? acceptsFreeByUtc,
        bool keepsItsSlot,
        Func<TableSequencingVerdict, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.MoveUnderTableLockAsync(
                reservation, table, startUtc, policy, booker, clientCommandId, acceptsFreeByUtc, keepsItsSlot,
                applyAsync, cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

    /// <summary>
    /// What a change of booking records in <c>ProcessedCommands</c>: which booking, and where it went.
    /// </summary>
    /// <remarks>
    /// Not the response. A replay answers with the booking as it stands, and a stored view would carry
    /// the guest's name and number into one more table.
    /// </remarks>
    private sealed record RescheduleRecord(
        Guid ReservationId,
        Guid? TableId,
        DateTime StartUtc,
        int PartySize,
        ReservationStatus Status)
    {
        /// <summary>
        /// Whether this is the record of <paramref name="command"/>: the same booking, moved to the
        /// same table, start and party size.
        /// </summary>
        /// <remarks>
        /// The start is compared as the instant the command's local date and time are in the branch's
        /// zone, which is how the move computed it. A local time the zone skips names no instant, so
        /// it cannot be the change that was recorded.
        /// </remarks>
        public bool Records(RescheduleReservationCommand command, BranchZone zone) =>
            ReservationId == command.ReservationId
            && TableId == command.TableId
            && PartySize == command.PartySize
            && zone.TryToUtc(command.LocalDate, command.LocalTime, out var startUtc)
            && StartUtc == startUtc;
    }

    internal static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web);
}
