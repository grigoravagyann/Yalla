using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Messaging;
using Yalla.Domain.Tabs;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Creating, cancelling and deciding bookings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this locks and the table state machine does not.</b> Two waiters seating a walk-in both
/// update the <i>same</i> <c>DiningTables</c> row, so its <c>RowVersion</c> catches the race with
/// no locks at all. Two diners booking the same table insert <i>different</i> <c>Reservations</c>
/// rows: nothing they touch collides, so optimistic concurrency sees no conflict and both commit.
/// The table is then double-booked and the system does not know it.
/// </para>
/// <para>
/// Touching the parent table row to force a version clash would fix that and break something
/// worse: two bookings for the same table at <i>different</i> times would also collide, so the
/// most popular tables in the room would start refusing perfectly good bookings, at the busiest
/// moment, for no reason a diner could understand.
/// </para>
/// <para>
/// So bookers are serialised per table instead. Take an update lock on the table row, re-check for
/// overlaps <b>inside</b> the lock, insert, commit. A second booker waits, re-reads, and then
/// either succeeds - different slot - or is told the table went. Both are the right answer.
/// </para>
/// <para>
/// The locked span is deliberately tiny: one indexed range read and one insert. No availability
/// snapshot, no push notification, no email happens inside it. The 409's availability payload is
/// built after the transaction has already rolled back.
/// </para>
/// <para>
/// <b>Which commands take the table's write lock.</b> A list rather than a rule, because the rule
/// has been stated twice and been wrong twice.
/// </para>
/// <list type="table">
/// <listheader><term>Command</term><description>Locks, and why</description></listheader>
/// <item>
/// <term>Create a booking</term>
/// <description><b>Yes.</b> Two bookers insert different rows and collide on nothing, so optimistic
/// concurrency sees no conflict and both commit.</description>
/// </item>
/// <item>
/// <term>Move a booking (reschedule)</term>
/// <description><b>Yes, the target table.</b> The moved booking updates its own row and the booking it
/// now overlaps is a different one, so no row version sees the clash. Leaving the old table only frees
/// it, which narrows nothing.</description>
/// </item>
/// <item>
/// <term>Seat (walk-in, QR, reservation, held party)</term>
/// <description><b>Yes.</b> Booking's re-check reads <c>TableSessions</c>; a seating that skipped the
/// lock could commit between that read and the booking's commit, and both would succeed.</description>
/// </item>
/// <item>
/// <term>Mark out of service</term>
/// <description><b>Yes.</b> A booking validated while the table was <c>Free</c> and committing after
/// this leaves a confirmed reservation on a broken table. Prompt 7 grouped this with the commands
/// below and that was wrong: it is not a narrower answer, it is a wrong one. Rare enough that the
/// throughput argument does not apply.</description>
/// </item>
/// <item>
/// <term>Free, hold, release a hold, return to service</term>
/// <description><b>No.</b> These only ever <i>narrow</i> what a booking finds - a booking that saw a
/// sitting about to close refuses a slot that would have been fine, which is a worse answer and never
/// a wrong one. They are also the floor's whole write traffic, and putting that through one queue per
/// table buys nothing.</description>
/// </item>
/// </list>
/// </remarks>
internal sealed class ReservationService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAvailabilityQuery availabilityQuery,
    IAuthorizationQueries authorization,
    IStaffBranchGuard branchGuard,
    NoShowPolicy noShowPolicy,
    ReservationWriter reservations,
    ITableStateService tableState,
    IOutbox outbox,
    IOptions<PublicWebOptions> publicWeb,
    ILogger<ReservationService> logger) : IReservationService
{
    public async Task<ReservationView> CreateAsync(
        CreateReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Book a table");

        // The note is part of the body, so it is refused before anything is looked up: a 422 naming
        // `note` is the answer whatever else would have gone on to be refused (K9).
        var note = Reservation.NormaliseNote(command.Note);

        // Before the replay, too: an account that cannot book has no original to be handed back,
        // and a booking's reminders and no-show record land on the number, so it must be a real one.
        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Booking a table", cancellationToken);

        // Cheap path first: a retry that arrives after the original committed never reaches the
        // lock at all. The unique index below is what makes the racing case safe.
        if (await reservations.FindReplayAsync(command.ClientCommandId, dinerUserId, cancellationToken)
            is { } replay)
        {
            logger.LogInformation(
                "Booking command {ClientCommandId} was already applied as {Code}; returning the original.",
                command.ClientCommandId, replay.Code);

            var replayBranch = await LoadBranchAsync(replay.BranchId, cancellationToken);

            return await ToViewAsync(
                replay, replayBranch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        var branch = await LoadBranchAsync(command.BranchId, cancellationToken);

        // A venue that stopped paying, or one that was deleted, takes no new bookings - a diner
        // whose app cached the branch id must not get past the fact that it vanished from search.
        // Only creation and moving a booking are gated: cancelling and seating one still work.
        VenueGate.RequireOpenForBusiness(branch);

        RequireChannelAccepted(command.Channel, branch);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        var policy = branch.ReservationPolicy;
        var zone = BranchZone.For(branch.TimeZoneId);
        var nowUtc = clock.UtcNow;

        // Throws LocalTimeDoesNotExistException for an hour a spring-forward skips, rather than
        // letting TimeZoneInfo's ArgumentException escape as a 500.
        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var endUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);
        var proposed = new BookedInterval(startUtc, endUtc);

        var pinned = await IsPinnedOnItsRoomAsync(table, cancellationToken);

        Validate(command.LocalDate, command.LocalTime, command.PartySize, table, pinned, branch, policy, zone, proposed, nowUtc);

        // Every booking here is a diner's - the app's or the public page's, whatever channel the
        // client reports, because the channel is self-reported and a rule it could switch off would be
        // no rule. Tonight's are the floor's to decide.
        var shortNotice = ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc));

        var status = await DecideStatusAsync(
            command.PartySize, approvedPartySize: null, policy, dinerUserId, startUtc, shortNotice, nowUtc, cancellationToken);

        // Minted here and returned exactly once, below. Only the hash is stored, so this plaintext
        // exists in this method and in the response and nowhere else - which is what makes it safe
        // to put in a URL somebody will paste into WhatsApp.
        var manageToken = Secrets.NewOpaqueToken();
        var manageTokenHash = Secrets.Hash(manageToken);
        StatusDecision decided = status;

        // The one way a booking row is created. Everything above this line is validation and
        // everything below it is presentation; the concurrency-critical part is all in there - and
        // what the booking becomes, which the table's evening decides, is built inside it.
        var outcome = await InsertUnderTheTableLockAsync(
            command.ClientCommandId,
            table,
            startUtc,
            policy,
            dinerUserId,
            command.AcceptsFreeByUtc,
            async (verdict, ct) =>
            {
                var withTable = await DecideWithTheTableAsync(verdict, status, shortNotice, startUtc, nowUtc, ct);
                decided = withTable.Status;

                var booking = Reservation.Create(
                    branchId: branch.Id,
                    diningTableId: table.Id,
                    startUtc: startUtc,
                    localDate: command.LocalDate,
                    localStartTime: command.LocalTime,
                    partySize: command.PartySize,
                    guestName: command.GuestName,
                    guestPhone: command.GuestPhone,
                    code: ReservationCode.Generate(),
                    policy: policy,
                    initialStatus: withTable.Status.Status,
                    dinerUserId: dinerUserId,
                    stayHint: command.StayHint,
                    clientCommandId: command.ClientCommandId,
                    channel: command.Channel,
                    note: note,
                    approvalTrigger: withTable.Status.Trigger,
                    approvalExpiresAtUtc: withTable.Status.ApprovalExpiresAtUtc,
                    promisedFreeByUtc: verdict.MustFreeByUtc);

                booking.AttachManageToken(manageTokenHash);

                if (withTable.Ask is { } ask)
                {
                    await TableShares.AskAsync(
                        db, outbox, branch, table.Label, booking, ask.EarlierReservationId, ask.FreeByUtc,
                        ask.ExpiresAtUtc, ask.AsksEarlierGuest, nowUtc, ct);
                }

                return booking;
            },
            cancellationToken);

        if (outcome.Replay is { } winner)
        {
            return await ToViewAsync(winner, branch, table, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.Occupied is { } sitting)
        {
            // Outside the lock, like the booking conflict below: assembling the floor snapshot is
            // a wide read and holding the table's lock through it would queue every other booker
            // behind a response body.
            throw await OccupiedAsync(SnapshotFor(command), table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            // Outside the lock, deliberately. The floor snapshot is a wide read and holding a
            // table's lock while assembling it would queue every other booker behind a response
            // body.
            throw await ConflictAsync(SnapshotFor(command), table, proposed, conflict, cancellationToken);
        }

        if (outcome.FreeBy is { } freeBy)
        {
            // A full sitting runs into the next booking, and the diner has not agreed to leave in time.
            throw FreeByNotAccepted(table, freeBy, zone, command.AcceptsFreeByUtc);
        }

        var reservation = outcome.Booking!;

        // The reminder and the nudge, written in the same unit of work as the booking. A booking
        // that exists without its reminder is the failure mode an outbox is for, and the diner finds
        // out about it by not being reminded.
        await ScheduleRemindersAsync(reservation, branch, table.Label, manageToken, cancellationToken);

        logger.LogInformation(
            "Booked table {TableLabel} at branch {BranchId} for {PartySize} as {Code} ({Status}).",
            table.Label, branch.Id, command.PartySize, reservation.Code, reservation.Status);

        return await ToViewAsync(
            reservation, branch, table, wasReplay: false, decided.Trigger, cancellationToken, manageToken);
    }

    /// <summary>
    /// Delegates to the writer and re-labels a lock timeout as the booking-specific one.
    /// </summary>
    /// <remarks>
    /// <see cref="TableLockTimeoutException"/> is shared with the table state machine, but a diner
    /// booking on their phone and a waiter seating a walk-in need different words and different
    /// error codes for the same underlying wait. The retryable-503 shape is identical either way.
    /// </remarks>
    private async Task<InsertOutcome> InsertUnderTheTableLockAsync(
        Guid clientCommandId,
        DiningTable table,
        DateTime startUtc,
        ReservationPolicy policy,
        Guid dinerUserId,
        DateTime? acceptsFreeByUtc,
        Func<TableSequencingVerdict, CancellationToken, Task<Reservation>> stageAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.InsertUnderTableLockAsync(
                clientCommandId, table, startUtc, policy, dinerUserId, acceptsFreeByUtc, stageAsync, cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

    /// <summary>
    /// What a booking becomes once the table's evening is known: the status creation's rules gave it,
    /// or - when the guest before it may still be at the table - a wait for their yes, and the question
    /// to ask them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tonight</b> (short notice) the floor decides, as for any short-notice booking: the same
    /// fifteen minutes, with the guest before named on the awaiting card and asked by the floor - a
    /// request the floor answers is opened beside it, so approving it holds the guest before to the time.
    /// </para>
    /// <para>
    /// <b>Another day</b> the booking waits <c>EarlierGuestConsent</c>, for two hours at most and never
    /// past the earlier booking's start. The earlier guest is asked in the app when they booked from it,
    /// have a phone to push to, and nothing else about this booking needs a human; otherwise the floor
    /// asks them - by telephone - from the awaiting list.
    /// </para>
    /// </remarks>
    private async Task<(StatusDecision Status, ShareAsk? Ask)> DecideWithTheTableAsync(
        TableSequencingVerdict verdict,
        StatusDecision status,
        bool shortNotice,
        DateTime startUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!verdict.NeedsEarlierGuest)
        {
            return (status, null);
        }

        var earlier = verdict.Earlier!.Value;
        var freeBy = verdict.EarlierGuestFreeByUtc!.Value;

        if (shortNotice && status is { Trigger: ApprovalTrigger.ShortNotice, ApprovalExpiresAtUtc: { } deadline })
        {
            return (status, new ShareAsk(earlier.ReservationId, freeBy, deadline, AsksEarlierGuest: false));
        }

        var expiresAtUtc = TableSequencing.AnswerDeadline(
            nowUtc, TableShareRequest.AnswerWindowMinutes, earlier.StartUtc, startUtc);

        // Something else about this booking already needs a human - a venue that approves every booking,
        // a large party, a no-show history - so the floor decides it with the guest before on the card.
        var needsAHumanAnyway = status.Status == ReservationStatus.PendingApproval;

        var inApp = !needsAHumanAnyway
                    && earlier.StartUtc > nowUtc
                    && await TableShares.CanAskInAppAsync(db, earlier.ReservationId, cancellationToken);

        return (
            new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.EarlierGuestConsent, expiresAtUtc),
            new ShareAsk(earlier.ReservationId, freeBy, expiresAtUtc, inApp));
    }

    /// <summary>The refusal for a time to leave by the booking did not accept, in the branch's wall clock.</summary>
    private static FreeByNotAcceptedException FreeByNotAccepted(
        DiningTable table,
        FreeByRequirement requirement,
        BranchZone zone,
        DateTime? accepted) =>
        new(
            table.Id,
            table.Label,
            requirement.FreeByUtc,
            zone.LocalTimeAt(requirement.FreeByUtc),
            requirement.NextStartUtc,
            accepted);

    // ---------------------------------------------------------------- the diner moves their booking

    /// <summary>What a change of booking is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string RescheduleCommandType = "reservation.reschedule";

    /// <summary>The status a change answers with, recorded beside it as every processed command's is.</summary>
    private const int RescheduleStatusCode = 200;

    public async Task<ReservationView> RescheduleAsync(
        RescheduleReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Change a booking");

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

        // The branch's zone first, because a replay is judged by where the change asked to go: the
        // start a retry names is a local time, and the record holds the instant it became.
        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var zone = BranchZone.For(branch.TimeZoneId);

        // Cheap path first, as for creation: a retry that arrives after the original committed is
        // answered with the booking as it stands and never reaches the lock.
        if (await IsRescheduleReplayAsync(command, dinerUserId, zone, cancellationToken))
        {
            return await ReplayViewAsync(reservation.Id, cancellationToken);
        }

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
        RequireChannelAccepted(reservation.Channel, branch);

        // At the booking's own branch: a move changes the slot, never the venue.
        var table = await LoadTableAsync(reservation.BranchId, command.TableId, cancellationToken);
        var policy = branch.ReservationPolicy;

        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var proposed = new BookedInterval(startUtc, startUtc.AddMinutes(policy.TurnTimeMinutes));
        var pinned = await IsPinnedOnItsRoomAsync(table, cancellationToken);

        Validate(
            command.LocalDate, command.LocalTime, command.PartySize, table, pinned, branch, policy, zone, proposed, nowUtc);

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
            ? new StatusDecision(ReservationStatus.Confirmed, null, null)
            : await DecideStatusAsync(
                command.PartySize,
                approvedPartySize,
                policy,
                dinerUserId,
                startUtc,
                ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc)),
                nowUtc,
                cancellationToken);

        // A booking already waiting on a short-notice answer keeps its deadline through a move: the
        // diner moving the request around does not restart the floor's fifteen minutes. Never past the
        // new start, which may be sooner.
        if (status.Trigger == ApprovalTrigger.ShortNotice
            && reservation.Status == ReservationStatus.PendingApproval
            && reservation.ApprovalExpiresAtUtc is { } runningDeadline)
        {
            status = status with
            {
                ApprovalExpiresAtUtc = runningDeadline < startUtc ? runningDeadline : startUtc,
            };
        }

        var shortNoticeMove = ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc));
        StatusDecision decided = status;

        var outcome = await MoveUnderTheTableLockAsync(
            reservation,
            table,
            startUtc,
            policy,
            dinerUserId,
            command.ClientCommandId,
            command.AcceptsFreeByUtc,
            async (verdict, ct) =>
            {
                // A confirmed booking keeping its table and start is not asked about again: nothing the
                // venue said yes to got harder to keep, whoever has the table before it.
                var withTable = keepsItsConfirmation
                    ? (status, (ShareAsk?)null)
                    : await DecideWithTheTableAsync(verdict, status, shortNoticeMove, startUtc, nowUtc, ct);

                decided = withTable.Item1;

                await StageMoveAsync(
                    reservation, command, table, branch, startUtc, withTable.Item1, withTable.Item2, verdict,
                    dinerUserId, nowUtc, ct);
            },
            cancellationToken);

        if (outcome.WasReplay)
        {
            // A retry of the same change committed while this one waited. Asked again out here
            // because the record the lock found may be somebody else's command, which is refused.
            await IsRescheduleReplayAsync(command, dinerUserId, zone, cancellationToken);

            return await ReplayViewAsync(reservation.Id, cancellationToken);
        }

        // Excluding this booking from the snapshot, as from the clash check: the floor the diner is
        // shown afterwards has their own table free, which is how they were looking at it.
        var snapshot = new AvailabilityRequest(
            reservation.BranchId, command.PartySize, command.LocalDate, command.LocalTime, reservation.Id);

        if (outcome.Occupied is { } sitting)
        {
            throw await OccupiedAsync(snapshot, table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            throw await ConflictAsync(snapshot, table, proposed, conflict, cancellationToken);
        }

        if (outcome.FreeBy is { } freeBy)
        {
            throw FreeByNotAccepted(table, freeBy, zone, command.AcceptsFreeByUtc);
        }

        logger.LogInformation(
            "Booking {Code} moved to table {TableLabel} at {Start:o} for {PartySize} ({Status}).",
            reservation.Code, table.Label, reservation.StartUtc, reservation.PartySize, reservation.Status);

        return await ToViewAsync(reservation, branch, table, wasReplay: false, decided.Trigger, cancellationToken);
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
    private async Task StageMoveAsync(
        Reservation reservation,
        RescheduleReservationCommand command,
        DiningTable table,
        Branch branch,
        DateTime startUtc,
        StatusDecision status,
        ShareAsk? ask,
        TableSequencingVerdict verdict,
        Guid dinerUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // The question this booking was waiting on is for the slot it is leaving; at once, inside the
        // lock's transaction, so a new one asked of the same guest below finds it closed.
        await TableShares.WithdrawForLaterNowAsync(db, reservation.Id, nowUtc, cancellationToken);

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
            promise);

        if (ask is { } question)
        {
            await TableShares.AskAsync(
                db, outbox, branch, table.Label, reservation, question.EarlierReservationId, question.FreeByUtc,
                question.ExpiresAtUtc, question.AsksEarlierGuest, nowUtc, cancellationToken);
        }

        // And a guest who was asked to free their table for this one - or whose table this one was
        // asked for - is settled by where it now stands.
        await TableShares.EarlierChangedAsync(db, outbox, reservation, branch, nowUtc, cancellationToken);

        // The account is live: the writer took its lock, inside this transaction, before calling here.
        await ReplaceRemindersAsync(db, outbox, reservation, branch, table.Label, nowUtc, cancellationToken);

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
    }

    /// <summary>
    /// Replaces a booking's unsent reminder, late nudge and waiting feed entry with ones for the slot
    /// it now has, in the caller's unit of work - after a move, and after a declined change sent it
    /// back to the slot it had.
    /// </summary>
    /// <remarks>
    /// The new keys carry the start they are for (<c>reminder:202610171930</c>), because a message
    /// already sent keeps its row and its key; one whose key was sent already is not written again. A
    /// web booking's new messages carry the manage link its old ones did - the token's plaintext exists
    /// only in the create response and the messages written then. The caller is responsible for the
    /// diner's account being live (its lock, or a booking whose account is gone has no diner and so no
    /// feed to write).
    /// </remarks>
    internal static async Task ReplaceRemindersAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        string tableLabel,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservation.Id);

        var messages = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix))
            .Select(m => new { m.Type, m.IdempotencyKey, m.PayloadJson, Sent = m.SentAtUtc != null })
            .ToListAsync(cancellationToken);

        var manageUrl = reservation.Channel == ReservationChannel.Web
            ? messages
                .Where(m => m.Type is OutboxMessageTypes.ReservationReminder or OutboxMessageTypes.ReservationLateNudge)
                .Select(m => ManageUrlIn(m.PayloadJson))
                .FirstOrDefault(url => url is not null)
            : null;

        var delivered = messages.Where(m => m.Sent).Select(m => m.IdempotencyKey).ToHashSet(StringComparer.Ordinal);

        await outbox.CancelAsync(prefix, cancellationToken);
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        EnqueueReminders(
            db,
            outbox,
            reservation,
            branch,
            tableLabel,
            manageUrl,
            keyVersion: reservation.StartUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
            writeFeed: reservation.DinerUserId is not null,
            delivered,
            nowUtc);
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
    /// A replayed change's answer: the booking as it stands now, read fresh.
    /// </summary>
    /// <remarks>
    /// The booking as it is, rather than a stored copy of the first answer, because a booking's view
    /// carries the guest's name and number and a stored copy would be one more place to scrub them
    /// from. The retry asked to be told where the booking is, and this tells it.
    /// </remarks>
    private async Task<ReservationView> ReplayViewAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var current = await db.Reservations
            .AsNoTracking()
            .FirstAsync(r => r.Id == reservationId, cancellationToken);

        var branch = await LoadBranchAsync(current.BranchId, cancellationToken);

        return await ToViewAsync(
            current,
            branch,
            table: null,
            wasReplay: true,
            PendingTriggerFor(current, branch.ReservationPolicy),
            cancellationToken);
    }

    /// <summary>The manage link a reminder was written with, or null.</summary>
    private static string? ManageUrlIn(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);

        return payload.RootElement.TryGetProperty("manageUrl", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;
    }

    /// <summary>
    /// The writer's locked move, with a lock timeout re-labelled as the booking-specific one - as
    /// <see cref="InsertUnderTheTableLockAsync"/> does for a new booking.
    /// </summary>
    private async Task<MoveOutcome> MoveUnderTheTableLockAsync(
        Reservation reservation,
        DiningTable table,
        DateTime startUtc,
        ReservationPolicy policy,
        Guid dinerUserId,
        Guid clientCommandId,
        DateTime? acceptsFreeByUtc,
        Func<TableSequencingVerdict, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.MoveUnderTableLockAsync(
                reservation, table, startUtc, policy, dinerUserId, clientCommandId, acceptsFreeByUtc, applyAsync,
                cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

    // ---------------------------------------------------------------- the earlier guest answers

    /// <summary>What an answer to a request for a table is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string AnswerTableRequestCommandType = "reservation.table-request.answer";

    public async Task<ReservationView> AnswerTableRequestAsync(
        AnswerTableRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Answer a request for your table");

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
            return await ReplayViewAsync(earlier.Id, cancellationToken);
        }

        // Only a request put to this guest in the app is theirs to answer; one the floor is deciding
        // was never shown to them.
        var request = await db.TableShareRequests
            .FirstOrDefaultAsync(
                r => r.Id == command.RequestId && r.EarlierReservationId == earlier.Id && r.AsksEarlierGuest,
                cancellationToken)
            ?? throw new KeyNotFoundException($"Request {command.RequestId} was not found on this booking.");

        var nowUtc = clock.UtcNow;
        var branch = await LoadBranchAsync(earlier.BranchId, cancellationToken);

        if (!request.IsOpenAt(nowUtc))
        {
            // The same answer again - a second tap, the notification's action and then the card - is
            // answered with the booking, not refused. Anything else is too late.
            var sameAnswer = request.Status == (command.Accept ? TableShareRequestStatus.Accepted : TableShareRequestStatus.Declined);

            if (sameAnswer)
            {
                return await ToViewAsync(earlier, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
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
            JsonSerializer.Serialize(new AnswerRecord(earlier.Id, request.Id, command.Accept), RecordJson),
            RescheduleStatusCode,
            nowUtc));

        try
        {
            if (command.Accept)
            {
                // Yes: the later booking is confirmed, this one held to the time - its end cut to it -
                // and both diners told.
                later.Confirm(nowUtc);

                await TableShares.ApplyYesAsync(db, outbox, later.Id, branch, nowUtc, cancellationToken);

                var key = await DecisionKeyAsync(db, later.Id, "approved", nowUtc, cancellationToken);

                TellDinerDecided(db, outbox, later, branch, approved: true, expired: false, key, nowUtc);

                await db.SaveChangesAsync(cancellationToken);
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
                    cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // The later booking or the request changed under this answer: the sweep expired it, the other
            // diner moved or cancelled, the floor decided. Whatever they did stands.
            db.ChangeTracker.Clear();

            var current = await db.TableShareRequests
                .AsNoTracking()
                .Where(r => r.Id == request.Id)
                .Select(r => new { r.Status, r.ExpiresAtUtc })
                .FirstAsync(cancellationToken);

            throw new TableRequestNotPendingException(earlier.Id, request.Id, current.Status, current.ExpiresAtUtc);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            // The same id was recorded by a racing request. Answered outside as a replay - or refused,
            // when it was another command's.
            db.ChangeTracker.Clear();

            if (await IsAnswerReplayAsync(command, dinerUserId, cancellationToken))
            {
                return await ReplayViewAsync(command.ReservationId, cancellationToken);
            }

            throw;
        }

        logger.LogInformation(
            "Booking {Code} answered the request for its table from booking {LaterCode}: {Answer}.",
            earlier.Code, later.Code, command.Accept ? "yes" : "no");

        var fresh = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == earlier.Id, cancellationToken);

        return await ToViewAsync(fresh, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
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
            || JsonSerializer.Deserialize<AnswerRecord>(processed.ResponseJson, RecordJson) is not { } record
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

    public async Task<ReservationView> CancelAsync(
        CancelReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Cancel a booking");
        var reservation = await LoadReservationAsync(command.ReservationId, cancellationToken);

        if (reservation.DinerUserId != dinerUserId)
        {
            // Deliberately the same answer as a booking that does not exist would give a stranger
            // guessing ids, except that the caller is told plainly they do not own this one.
            throw new UnauthorizedAccessException("A diner may only cancel their own bookings.");
        }

        return await CancelCoreAsync(reservation, command.Reason, cancellationToken);
    }

    /// <summary>
    /// Cancels a booking with the manage token from its link, for a caller with no account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>only</b> thing this does not share with <see cref="CancelAsync"/> is how the caller
    /// proved they may: a signed-in diner is checked against <c>DinerUserId</c>, a link holder by
    /// holding an unguessable token. Everything after that - the deadline rule, the lateness
    /// record, cancelling the outbox messages, the save - is <see cref="CancelCoreAsync"/>, once.
    /// A second cancellation path is how a web cancel would eventually stop cancelling the reminder.
    /// </para>
    /// <para>
    /// A booking that is already cancelled or finished is returned untouched rather than refused;
    /// the caller renders its state. Only an unusable token fails, and every one of those fails
    /// identically - see <see cref="ManageBookingFailure"/>.
    /// </para>
    /// </remarks>
    public async Task<ReservationView> CancelByManageTokenAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var reservation = await FindByManageTokenAsync(manageToken, cancellationToken);

        // Already finished with. Cancelling a cancelled booking must not move CancelledAtUtc, and
        // cancelling a completed one is meaningless - so the state is reported, not rewritten.
        if (!CanStillCancel(reservation.Status))
        {
            var settled = await LoadBranchAsync(reservation.BranchId, cancellationToken);

            return await ToViewAsync(
                reservation, settled, table: null, wasReplay: false, trigger: null, cancellationToken);
        }

        return await CancelCoreAsync(reservation, reason, cancellationToken);
    }

    /// <summary>Whether cancelling would still do anything.</summary>
    internal static bool CanStillCancel(ReservationStatus status) =>
        status is ReservationStatus.Confirmed or ReservationStatus.PendingApproval;

    /// <summary>
    /// Resolves a manage token to its booking, or throws the one refusal every failure shares.
    /// </summary>
    /// <remarks>
    /// Unknown, expired and purged are three facts here and one answer on the wire. The expiry
    /// check runs after the row is found and raises the same exception as not finding one, and
    /// nothing branches on the token's contents before the lookup.
    /// </remarks>
    internal async Task<Reservation> FindByManageTokenAsync(
        string manageToken,
        CancellationToken cancellationToken)
    {
        // A blank token is answered like any other bad one rather than as a different error: the
        // caller learns nothing either way, which is the whole rule.
        if (string.IsNullOrWhiteSpace(manageToken))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(manageToken);

        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.ManageTokenHash == hash, cancellationToken);

        // The link outlives the booking by ManageTokenGraceDays so somebody can still read what
        // happened, then stops working - a bearer capability sitting in a WhatsApp thread should
        // not be live forever.
        if (reservation is null || !reservation.ManageTokenIsLiveAt(clock.UtcNow))
        {
            throw ManageBookingFailure.Raise();
        }

        return reservation;
    }

    /// <summary>
    /// Refuses a booking - or a move of one - from a channel the branch is not taking bookings from.
    /// </summary>
    private static void RequireChannelAccepted(ReservationChannel channel, Branch branch)
    {
        // A branch that never agreed to take bookings from its public page does not take them.
        // Checked here rather than at the endpoint because the public page books through this same
        // service - there is no separate public booking route to guard - and because a rule that
        // lives in the service holds for any caller, not only the one that remembered.
        RequireWebBookingsAccepted(channel, branch);

        // The app channel's gate (K9): bookings switched on AND a reservation policy somebody at the
        // venue has saved. A branch ships with default rules nobody chose, and an app that lists
        // every branch in the city would otherwise book against those guesses from the day it is
        // created. Web keeps its own rule above, unchanged; Unknown and Staff are not gated.
        if (channel == ReservationChannel.App
            && !(branch.AcceptsWebBookings && branch.ReservationPolicyReviewedAtUtc is not null))
        {
            throw new AppBookingsNotAcceptedException(branch.Id, branch.Name);
        }
    }

    /// <summary>
    /// Refuses a booking from the public page at a branch that has not switched web bookings on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReservationChannel"/> is self-reported, which is worth being plain about: this
    /// stops the public page offering a booking the venue never agreed to; it does not stop a
    /// hand-written client claiming to be the app. That is the right trade for what the flag is - a
    /// venue's stated preference about its own page, not an access control - and making it one
    /// would mean authenticating the channel, which a page reachable by anybody with a URL cannot
    /// do.
    /// </para>
    /// <para>
    /// The public page reads <c>acceptsWebBookings</c> and hides its booking UI, so in practice
    /// this fires for a stale page whose branch was switched off while somebody had it open.
    /// </para>
    /// </remarks>
    private static void RequireWebBookingsAccepted(ReservationChannel channel, Branch branch)
    {
        if (channel == ReservationChannel.Web && !branch.AcceptsWebBookings)
        {
            throw new WebBookingsNotAcceptedException(branch.Id, branch.Name);
        }
    }

    /// <summary>
    /// The one cancellation path. Both entry points arrive here having already settled that the
    /// caller is entitled to cancel this booking.
    /// </summary>
    private async Task<ReservationView> CancelCoreAsync(
        Reservation reservation,
        string? reason,
        CancellationToken cancellationToken)
    {
        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var late = ReservationRules.IsLateCancellation(reservation.StartUtc, nowUtc, branch.ReservationPolicy);

        // Late is recorded, never refused. A diner who cannot cancel simply does not turn up, and
        // a no-show costs the venue the same table plus the chance to resell it.
        reservation.CancelByDiner(nowUtc, reason, late);

        // A question this booking was waiting on goes with it, and a guest who was asked to free their
        // table for it - or a booking waiting on this one's guest - is settled now the table is free.
        await TableShares.CloseForLaterAsync(db, reservation.Id, TableShareRequestStatus.Withdrawn, nowUtc, cancellationToken);
        await TableShares.EarlierChangedAsync(db, outbox, reservation, branch, nowUtc, cancellationToken);

        // Cancelling the cause cancels the message, in the same transaction. A reminder arriving for
        // a booking somebody cancelled an hour ago is worse than no reminder at all - it is the push
        // the diner remembers, and it teaches them the notifications are wrong.
        await outbox.CancelAsync(
            OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

        // And its feed entry that has not appeared yet - the reminder - with it (K12).
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        if (late)
        {
            logger.LogInformation(
                "Booking {Code} was cancelled {Minutes} minutes before it started, past the branch deadline.",
                reservation.Code, (int)(reservation.StartUtc - nowUtc).TotalMinutes);
        }

        return await ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
    }

    public Task<ReservationView> ApproveAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        DecideAsync(command, approve: true, cancellationToken);

    public Task<ReservationView> RejectAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        DecideAsync(command, approve: false, cancellationToken);

    public async Task<MyReservations> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Read my bookings");
        var nowUtc = clock.UtcNow;

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DinerUserId == dinerUserId)
            .Select(r => new MineRow
            {
                Reservation = r,
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                TableLabel = r.DiningTable.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,

                // A later guest asking for this booking's table, while the question is open and theirs
                // to answer - beside the booking, in the same statement.
                Incoming = db.TableShareRequests
                    .Where(q => q.EarlierReservationId == r.Id
                                && q.Status == TableShareRequestStatus.Pending
                                && q.AsksEarlierGuest
                                && q.ExpiresAtUtc > nowUtc)
                    .OrderBy(q => q.CreatedAtUtc)
                    .Select(q => new IncomingRow
                    {
                        RequestId = q.Id,
                        RequestedStartUtc = q.RequestedStartUtc,
                        FreeByUtc = q.FreeByUtc,
                        ExpiresAtUtc = q.ExpiresAtUtc,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Upcoming means "still going to happen": not finished, and not already called off. A
        // booking cancelled for tomorrow belongs in the history, not at the top of the screen.
        var upcoming = rows
            .Where(r => r.Reservation.EndUtc > nowUtc && ReservationOverlap.Blocks(r.Reservation.Status))
            .OrderBy(r => r.Reservation.StartUtc)
            .Select(r => ToView(r))
            .ToList();

        var past = rows
            .Where(r => r.Reservation.EndUtc <= nowUtc || !ReservationOverlap.Blocks(r.Reservation.Status))
            .OrderByDescending(r => r.Reservation.StartUtc)
            .Select(r => ToView(r))
            .ToList();

        return new MyReservations(upcoming, past);
    }

    public async Task<IReadOnlyList<ReservationView>> ListForBranchAsync(
        Guid branchId,
        ReservationStatus status,
        CancellationToken cancellationToken = default)
    {
        // The console's queue, so the manager-or-above check from the stored row. BranchScoped on the
        // route widens a manager with a home branch to their whole venue; this narrows them back, so
        // the list never shows a booking its reader would be refused permission to decide.
        await branchGuard.RequireAtBranchAsync(branchId, "List a branch's bookings", cancellationToken);

        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.ReservationPolicy)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.Status == status)
            .OrderBy(r => r.LocalDate)
            .ThenBy(r => r.LocalStartTime)
            .ThenBy(r => r.Id)
            .Take(BranchReservationList.MaxRows)
            .Select(r => new MineRow
            {
                Reservation = r,
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                TableLabel = r.DiningTable.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => BuildView(
                r.Reservation,
                r.BranchName,
                r.TimeZoneId,
                new TableFacts(r.TableLabel, r.RoomId, r.RoomName),
                policy.CancellationDeadlineMinutes,
                wasReplay: false,
                trigger: PendingTriggerFor(r.Reservation, policy)))
            .ToList();
    }

    public async Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        // The floor's read, so the floor's guard: a waiter or above at this branch, as for deciding.
        await RequireStaffForBranchAsync(branchId, "List the bookings awaiting approval", cancellationToken);

        // The guests' numbers are the venue's to call, not the platform's to read.
        var withPhones = actor.Role != StaffRole.PlatformAdmin;
        var nowUtc = clock.UtcNow;

        // One statement for the whole panel. Each request's table context - the booking before it that
        // day, the one after, the sitting there now - is a correlated subquery beside it (OUTER APPLY),
        // so ten requests cost what one does rather than thirty-one round trips.
        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && r.Status == ReservationStatus.PendingApproval
                        && r.StartUtc > nowUtc
                        && (r.ApprovalExpiresAtUtc == null || r.ApprovalExpiresAtUtc > nowUtc)

                        // Not one waiting on the earlier guest's own answer in the app: that question is
                        // theirs, not the floor's.
                        && !db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                           && q.Status == TableShareRequestStatus.Pending
                                                           && q.AsksEarlierGuest))
            .OrderBy(r => r.ApprovalExpiresAtUtc == null)
            .ThenBy(r => r.ApprovalExpiresAtUtc)
            .ThenBy(r => r.StartUtc)
            .ThenBy(r => r.Id)
            .Take(BranchReservationList.MaxRows)
            .Select(r => new AwaitingRow
            {
                Reservation = r,
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                Policy = r.Branch.ReservationPolicy,
                TableLabel = r.DiningTable.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,

                // Whoever had the table before, that day: still to come, at it now, gone, or never
                // turned up. A cancelled booking was never going to be at the table, so it is skipped.
                Previous = db.Reservations
                    .Where(p => p.DiningTableId == r.DiningTableId
                                && p.LocalDate == r.LocalDate
                                && p.StartUtc < r.StartUtc
                                && p.Id != r.Id
                                && p.Status != ReservationStatus.CancelledByDiner
                                && p.Status != ReservationStatus.CancelledByVenue)
                    .OrderByDescending(p => p.StartUtc)
                    .Select(p => new AwaitingPreviousBooking(
                        p.Id, p.StartUtc, p.LocalStartTime, p.PartySize, p.GuestName, p.GuestPhone, p.Status,
                        p.PromisedFreeByUtc))
                    .FirstOrDefault(),

                // The next promise on the table that day - the statuses that hold it.
                Next = db.Reservations
                    .Where(n => n.DiningTableId == r.DiningTableId
                                && n.LocalDate == r.LocalDate
                                && n.StartUtc > r.StartUtc
                                && n.Id != r.Id
                                && (n.Status == ReservationStatus.Confirmed
                                    || n.Status == ReservationStatus.PendingApproval
                                    || n.Status == ReservationStatus.Seated))
                    .OrderBy(n => n.StartUtc)
                    .Select(n => new AwaitingNextBooking(n.Id, n.StartUtc, n.LocalStartTime, n.PartySize, n.PromisedFreeByUtc))
                    .FirstOrDefault(),

                // The time the guest before is asked to have left by, when this booking waits on that.
                EarlierGuestFreeByUtc = db.TableShareRequests
                    .Where(q => q.LaterReservationId == r.Id && q.Status == TableShareRequestStatus.Pending)
                    .Select(q => (DateTime?)q.FreeByUtc)
                    .FirstOrDefault(),

                // Somebody at the table now. A diner's scan opens a table with a guessed party size, so
                // the size is only given when a waiter seated them or they came on a booking.
                Sitting = db.TableSessions
                    .Where(s => s.DiningTableId == r.DiningTableId && s.ClosedAtUtc == null)
                    .OrderByDescending(s => s.SeatedAtUtc)
                    .Select(s => new AwaitingCurrentSitting(
                        s.SeatedAtUtc,
                        s.SeatedByStaffId != null || s.ReservationId != null ? s.PartySize : null))
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                var view = BuildView(
                    row.Reservation,
                    row.BranchName,
                    row.TimeZoneId,
                    new TableFacts(row.TableLabel, row.RoomId, row.RoomName),
                    row.Policy.CancellationDeadlineMinutes,
                    wasReplay: false,
                    trigger: PendingTriggerFor(row.Reservation, row.Policy));

                return new AwaitingApproval
                {
                    Reservation = withPhones ? view : view with { GuestPhone = string.Empty },
                    ApprovalExpiresAtUtc = view.ApprovalExpiresAtUtc,

                    // Never null here: the booking is pending, and a pending booking always has a reason.
                    Trigger = view.AwaitingApprovalBecause ?? ApprovalTrigger.NoShowHistory,
                    PreviousBooking = withPhones || row.Previous is null
                        ? row.Previous
                        : row.Previous with { GuestPhone = null },
                    NextBooking = row.Next,
                    CurrentSitting = row.Sitting,
                    EarlierGuestFreeByUtc = row.EarlierGuestFreeByUtc,
                };
            })
            .ToList();
    }

    /// <summary>
    /// Why a pending booking is waiting: the reason stored when it went to approval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the booking, never worked out from the clock: a booking made at short notice is still
    /// waiting for that reason at any later read, and a policy edited since cannot rename it.
    /// </para>
    /// <para>
    /// <b>The fallback is for rows from before the column existed</b>, which carry none. Those replay
    /// <see cref="DecideStatusAsync"/>'s old order against the branch's current policy -
    /// approve-everything, then the party-size threshold, and otherwise the diner's no-show history -
    /// which is what every pending booking used to be told. It never names a reason for a booking
    /// that is not pending.
    /// </para>
    /// </remarks>
    private static ApprovalTrigger? PendingTriggerFor(Reservation reservation, ReservationPolicy policy)
    {
        if (reservation.Status != ReservationStatus.PendingApproval)
        {
            return null;
        }

        if (reservation.ApprovalTrigger is { } stored)
        {
            return stored;
        }

        if (!policy.AutoConfirm)
        {
            return ApprovalTrigger.BranchApprovesEveryBooking;
        }

        return ReservationRules.NeedsApproval(reservation.PartySize, policy)
            ? ApprovalTrigger.LargeParty
            : ApprovalTrigger.NoShowHistory;
    }

    /// <summary>
    /// Builds the "somebody is sitting there" refusal, with a fresh floor snapshot attached.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ConflictAsync"/> deliberately - same shape, same best-effort snapshot -
    /// because the two refusals differ in what they mean to the diner, not in how they are built.
    /// </remarks>
    private async Task<TableCurrentlyOccupiedException> OccupiedAsync(
        AvailabilityRequest snapshot,
        DiningTable table,
        BookedInterval proposed,
        TableSession sitting,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        var projected = SessionOccupancy.ProjectedInterval(sitting.SeatedAtUtc, policy.TurnTimeMinutes);

        logger.LogInformation(
            "Table {TableLabel} has been occupied since {SeatedAt:o}; the sitting runs into the {Start:o} booking.",
            table.Label, sitting.SeatedAtUtc, proposed.StartUtc);

        BranchAvailability? availability = null;

        try
        {
            availability = await availabilityQuery.GetAvailabilityAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the occupancy conflict.");
        }

        return new TableCurrentlyOccupiedException(
            snapshot.BranchId,
            table.Id,
            table.Label,
            proposed,
            sitting.SeatedAtUtc,
            projected.EndUtc,
            sitting.Id,
            availability);
    }

    private async Task<TableAlreadyBookedException> ConflictAsync(
        AvailabilityRequest snapshot,
        DiningTable table,
        BookedInterval proposed,
        BookingClash conflict,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Table {TableLabel} cannot take {Start:o}: booking {ReservationId} is in the way ({Reason}).",
            table.Label, proposed.StartUtc, conflict.ReservationId, conflict.Reason);

        BranchAvailability? availability = null;

        try
        {
            // So the app can redraw the floor and show what changed, rather than only being told
            // no. Best effort: failing to build it must not replace a 409 the client can act on
            // with a 500 it cannot.
            availability = await availabilityQuery.GetAvailabilityAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the booking conflict.");
        }

        return new TableAlreadyBookedException(
            snapshot.BranchId,
            table.Id,
            table.Label,
            proposed,
            conflict.Interval,
            conflict.ReservationId,
            availability,
            conflict.Reason);
    }

    /// <summary>The floor a refused booking is answered with: the slot it asked about.</summary>
    private static AvailabilityRequest SnapshotFor(CreateReservationCommand command) =>
        new(command.BranchId, command.PartySize, command.LocalDate, command.LocalTime);

    // ---------------------------------------------------------------- rules

    /// <summary>
    /// Whether the table is pinned on a photo of its own room - the rule availability and the rooms
    /// read apply: a pin left on another room's picture is not a table a diner could have tapped.
    /// </summary>
    private async Task<bool> IsPinnedOnItsRoomAsync(DiningTable table, CancellationToken cancellationToken) =>
        table.PhotoId is { } photoId
        && table.FloorAreaId is { } roomId
        && await db.FloorAreaPhotos.AnyAsync(p => p.FloorAreaId == roomId && p.PhotoId == photoId, cancellationToken);

    /// <summary>
    /// Every branch rule, each answering with its own named exception.
    /// </summary>
    /// <remarks>
    /// The rules themselves are pure functions in the domain and are shared with the availability
    /// read model, so a table the screen offers is a table this method accepts. All this does is
    /// turn the reason into the exception that carries the numbers behind it.
    /// </remarks>
    private static void Validate(
        DateOnly localDate,
        TimeOnly localTime,
        int partySize,
        DiningTable table,
        bool pinned,
        Branch branch,
        ReservationPolicy policy,
        BranchZone zone,
        BookedInterval proposed,
        DateTime nowUtc)
    {
        var localToday = zone.LocalDateAt(nowUtc);

        var timing = ReservationRules.CheckTiming(
            proposed.StartUtc, nowUtc, localDate, localToday, policy);

        switch (timing)
        {
            case ReservationRejectionReason.LeadTimeTooShort:
                throw new LeadTimeTooShortException(
                    proposed.StartUtc, nowUtc.AddMinutes(policy.MinLeadMinutes), policy.MinLeadMinutes);

            case ReservationRejectionReason.OutsideBookingWindow:
                throw new OutsideBookingWindowException(
                    localDate, localToday.AddDays(policy.BookingWindowDays), policy.BookingWindowDays);
        }

        var localStart = localDate.ToDateTime(localTime);
        var localEnd = zone.ToLocal(proposed.EndUtc);

        if (ReservationRules.CheckOpeningHours(branch.OpeningHours, localStart, localEnd) is not null)
        {
            throw new OutsideOpeningHoursException(localDate, localTime, TimeOnly.FromDateTime(localEnd));
        }

        // A table with no pin on one of its room's photos is not offered to diners - availability
        // leaves it out - so a booking that names one anyway is refused as not bookable, the same
        // answer a walk-in-only table gets. Every booking here is a diner's (RequireDiner above); staff seat such a table
        // through the floor, not through this.
        var tableReason = ReservationRules.CheckTable(
            table.Status, table.IsBookable && pinned, table.Seats, partySize, policy);

        switch (tableReason)
        {
            case ReservationRejectionReason.TableNotBookable:
                throw new TableNotBookableException(table.Id, table.Label);

            case ReservationRejectionReason.TableOutOfService:
                throw new TableOutOfServiceException(table.Id, table.Label);

            case ReservationRejectionReason.PartyExceedsCapacity:
                throw new PartyExceedsTableCapacityException(
                    table.Id, table.Label, partySize, table.Seats);

            case ReservationRejectionReason.SeatOverhangExceeded:
                throw new SeatOverhangExceededException(
                    table.Id, table.Label, partySize, table.Seats, policy.MaxSeatOverhang!.Value);
        }
    }

    /// <summary>
    /// Confirmed, or waiting for a human - which of the four reasons it was, and until when the venue
    /// may answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of a new booking and again of a moved one, with the party size it now has: the same
    /// four reasons, in the same order, either way, and the first that applies is the one stored.
    /// </para>
    /// <para>
    /// <b>Short notice first</b> (<see cref="ShortNoticeRule"/>), because it is the one with a
    /// deadline: tonight's booking waits for the floor for fifteen minutes at most, whatever else is
    /// true of it, and a party of twelve tonight is still a table somebody may be sitting at.
    /// </para>
    /// <para>
    /// A moved booking that was confirmed passes its confirmed size as
    /// <paramref name="approvedPartySize"/>, and a party no larger than that is not held back by the
    /// size threshold - the venue has said yes to that many people already. The other reasons are
    /// asked as for any booking.
    /// </para>
    /// </remarks>
    /// <param name="partySize">How many are coming.</param>
    /// <param name="approvedPartySize">
    /// The party size the booking was confirmed for, on a move of a confirmed booking; null on a new
    /// booking and on a move of one still waiting.
    /// </param>
    /// <param name="policy">The branch's policy.</param>
    /// <param name="dinerUserId">Whose booking, for the no-show count.</param>
    /// <param name="startUtc">When the booking starts, which caps the answer window.</param>
    /// <param name="shortNotice">Whether the booking is at short notice - see <see cref="ShortNoticeRule.Applies"/>.</param>
    /// <param name="nowUtc">Now, for the no-show window and the answer window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<StatusDecision> DecideStatusAsync(
        int partySize,
        int? approvedPartySize,
        ReservationPolicy policy,
        Guid dinerUserId,
        DateTime startUtc,
        bool shortNotice,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (shortNotice)
        {
            // Not "no", and not a guess either: somebody may have just sat down at that table, and
            // only the floor can see. They have fifteen minutes - or until the start - to say.
            return new StatusDecision(
                ReservationStatus.PendingApproval,
                ApprovalTrigger.ShortNotice,
                ShortNoticeRule.AnswerDeadline(nowUtc, startUtc));
        }

        if (!policy.AutoConfirm)
        {
            return new StatusDecision(
                ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking, null);
        }

        var alreadyConfirmedThisMany = approvedPartySize is { } approved && partySize <= approved;

        if (!alreadyConfirmedThisMany && ReservationRules.NeedsApproval(partySize, policy))
        {
            // Not a rejection. Twelve people is the most valuable booking of the night and the one
            // most likely to need tables moved, so a human looks at it.
            return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.LargeParty, null);
        }

        if (!noShowPolicy.Enabled)
        {
            return new StatusDecision(ReservationStatus.Confirmed, null, null);
        }

        var noShows = await db.Reservations
            .AsNoTracking()
            .CountAsync(
                r => r.DinerUserId == dinerUserId
                     && r.Status == ReservationStatus.NoShow
                     && r.StartUtc >= noShowPolicy.WindowStartUtc(nowUtc),
                cancellationToken);

        if (!noShowPolicy.RequiresApproval(noShows))
        {
            return new StatusDecision(ReservationStatus.Confirmed, null, null);
        }

        logger.LogInformation(
            "Diner {DinerUserId} has {NoShows} no-shows in the last {WindowDays} days; this booking needs approval.",
            dinerUserId, noShows, noShowPolicy.WindowDays);

        return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.NoShowHistory, null);
    }

    private async Task<ReservationView> DecideAsync(
        DecideReservationCommand command,
        bool approve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await LoadReservationAsync(command.ReservationId, cancellationToken);
        var operation = approve ? "Approve a booking" : "Reject a booking";

        // A waiter or above at the booking's own branch, not only a manager: a short-notice booking is
        // a question about a table the person on the floor can see and the office cannot, and one that
        // waits for somebody senior to walk past is one the clock declines.
        var staffId = await RequireStaffForBranchAsync(reservation.BranchId, operation, cancellationToken);

        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;

        // Both throw ReservationApprovalExpiredException once the answer window has closed: past it
        // the decision is the sweep's, and the diner is told nobody looked rather than that somebody
        // said no a second too late.
        try
        {
            if (approve)
            {
                reservation.Confirm(nowUtc);

                // The floor's yes for the guest before, when this booking was waiting on them: they are
                // held to the time asked - its end cut to it - and told.
                await TableShares.ApplyYesAsync(db, outbox, reservation.Id, branch, nowUtc, cancellationToken);

                // A booking the diner moved back into the queue is approved a second time, and the
                // first approval's message keeps its key once sent - see DecisionKeyAsync.
                var decidedKey = await DecisionKeyAsync(db, reservation.Id, "approved", nowUtc, cancellationToken);

                // The decision is worth telling the diner about: they are on a screen that says "waiting".
                TellDinerDecided(db, outbox, reservation, branch, approved: true, expired: false, decidedKey, nowUtc);

                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // A no - to the booking, or, for a change of a confirmed one, to the change: the diner
                // keeps the slot they had when it is still free. Saved inside.
                await PendingDecline.DeclineAsync(
                    db,
                    outbox,
                    reservations,
                    reservation,
                    branch,
                    DeclineReason.StaffDeclined,
                    command.Reason,
                    quietIfAlreadyStarted: false,
                    nowUtc,
                    cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // Something else wrote the booking between the read above and this save. When that was the
            // sweep declining it for no answer - the waiter tapped on the last second, the sweep ran on
            // it - the tablet is told exactly that, not that the record merely changed.
            var current = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == reservation.Id)
                .Select(r => new { r.Status, r.DeclineReason })
                .FirstOrDefaultAsync(cancellationToken);

            if (current is { DeclineReason: { } declined }
                && Reservation.IsForNoAnswer(declined)
                && current.Status != ReservationStatus.PendingApproval)
            {
                throw new ReservationApprovalExpiredException(
                    reservation.Id,
                    reservation.Code,
                    current.Status,
                    reservation.StartUtc,
                    approvalExpiresAtUtc: null,
                    $"Booking {reservation.Code} was declined because nobody answered in time, just before this "
                    + "decision arrived. The diner has been told.");
            }

            throw;
        }

        logger.LogInformation(
            "Booking {Code} was {Decision} by staff {StaffId}; it is now {Status}.",
            reservation.Code, approve ? "approved" : "rejected", staffId, reservation.Status);

        var view = await ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);

        // The guest's number is the venue's to call, not the platform's to read - as on the floor's list.
        return actor.Role == StaffRole.PlatformAdmin ? view with { GuestPhone = string.Empty } : view;
    }

    /// <summary>
    /// The idempotency key a decision's push is written under: <c>reservation:{id}:{what}</c>, or - when
    /// that is taken already - the same with the decision's moment after it.
    /// </summary>
    /// <remarks>
    /// One booking can be decided more than once. A move sends a confirmed booking back for approval,
    /// so it can be approved twice; and a declined change leaves the booking standing at the slot it
    /// had, so it can be declined - or run out of time - twice as well. The first message keeps its key
    /// once sent, so a later one is named for when it was made rather than colliding with it.
    /// </remarks>
    internal static async Task<string> DecisionKeyAsync(
        YallaDbContext db,
        Guid reservationId,
        string what,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var key = OutboxMessageTypes.KeyFor("reservation", reservationId, what);

        return await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == key, cancellationToken)
            ? OutboxMessageTypes.KeyFor(
                "reservation",
                reservationId,
                what + ":" + nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture))
            : key;
    }

    /// <summary>
    /// Tells the diner a booking that was waiting has been decided - the push and the feed entry
    /// beside it - in the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One copy for the two places a waiting booking is decided: staff, here, and the sweep that
    /// declines a short-notice request nobody answered (<see cref="ApprovalExpirySweep"/>). The push
    /// payload says which with <c>expired</c>, so the diner reads an apology - the venue did not look
    /// in time - rather than the venue's "no".
    /// </para>
    /// <para>
    /// A booking whose diner account is gone has nobody to tell. Nothing is written for it, rather
    /// than a push that could never be addressed and would only be retried into the dead letters.
    /// </para>
    /// </remarks>
    /// <param name="db">The caller's unit of work, for the feed entry.</param>
    /// <param name="outbox">The caller's outbox, for the push.</param>
    /// <param name="reservation">The booking as it now stands.</param>
    /// <param name="branch">Its branch, with the venue, for the names.</param>
    /// <param name="approved">Whether it was a yes.</param>
    /// <param name="expired">Whether it was the clock: nobody answered in time.</param>
    /// <param name="idempotencyKey">The push's key - see <see cref="DecisionKeyAsync"/>.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="change">
    /// For a declined change of a confirmed booking: whether the booking went back to the slot it had,
    /// and the slot that was asked for. Null for every other decision.
    /// </param>
    /// <param name="because">Why a decline happened, carried as <c>declinedBecause</c>; null for an approval.</param>
    internal static void TellDinerDecided(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        bool approved,
        bool expired,
        string idempotencyKey,
        DateTime nowUtc,
        DeclinedChange? change = null,
        DeclineReason? because = null)
    {
        if (reservation.DinerUserId is null)
        {
            return;
        }

        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationDecided,
            new
            {
                reservationId = reservation.Id,
                dinerUserId = reservation.DinerUserId,
                venueName,
                branchName = branch.Name,
                tableLabel = string.Empty,
                localStartTime = reservation.LocalStartTime,
                reservationCode = reservation.Code,
                graceExtensionMinutes = 0,
                approved,
                expired,
                wasChange = change is not null,
                originalKept = change?.OriginalKept ?? false,
                declinedBecause = approved ? null : because,
            },
            nowUtc,
            idempotencyKey);

        // The same fact into the diner's feed, beside its push and in the same save (K12).
        DinerNotices.BookingDecided(
            db, reservation, venueName, branch.Name, approved, expired, nowUtc, change, approved ? null : because);
    }

    // ---------------------------------------------------------------- what the diner gets told

    /// <summary>
    /// Queues the reminder and the late nudge alongside the booking that causes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are written in the caller's transaction and saved with it - <c>IOutbox</c> has no save of
    /// its own, precisely so this cannot drift into "book, commit, then enqueue" and leave a window
    /// where the booking exists and the reminder does not.
    /// </para>
    /// <para>
    /// The payload carries the venue and branch names, the table and the time as they are now. At
    /// send time the world has moved - the venue may have been renamed - and what the message should
    /// say is what was true when the booking was made.
    /// </para>
    /// <para>
    /// A reminder whose moment has already passed is not written at all. A booking made an hour
    /// before it starts has no three-hours-before, and queueing one in the past would only be
    /// discarded by the staleness rule with a log line implying something went wrong.
    /// </para>
    /// </remarks>
    private async Task ScheduleRemindersAsync(
        Reservation reservation,
        Branch branch,
        string tableLabel,
        string manageToken,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        // The manage link, for a booking whose diner has no app to be pushed to.
        //
        // Written now, before any channel exists that could send it. The app reminder already
        // carries a one-tap cancel; a web booking's reminder has to carry a URL instead, and
        // the token is knowable only here - it is never stored in plaintext and never returned
        // again. A dispatcher added later that had to go and mint one would find it cannot.
        //
        // Only for Web. A capability that opens somebody's booking should be written into as
        // few places as possible, and an app booking has a better cancel route already.
        var manageUrl = reservation.Channel == ReservationChannel.Web
            ? publicWeb.Value.ManageBookingUrlTemplate.Replace(
                "{token}", Uri.EscapeDataString(manageToken), StringComparison.Ordinal)
            : null;

        // The booking committed a moment ago, and the account can have been deleted since - from another
        // phone, past the token cache - which cut the booking loose and emptied the feed. The feed entry
        // is the person's, so it is written under the account's lock and only for a live account:
        // a deletion that comes after waits for this commit and removes it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var writeFeed = ReminderDueAt(reservation, branch.ReservationPolicy) > nowUtc
                        && reservation.DinerUserId is { } dinerUserId
                        && await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken);

        EnqueueReminders(
            db, outbox, reservation, branch, tableLabel, manageUrl, keyVersion: null, writeFeed, NothingDelivered, nowUtc);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>A new booking has sent nothing yet.</summary>
    private static readonly IReadOnlySet<string> NothingDelivered = new HashSet<string>();

    /// <summary>When the booking's reminder is due: the branch's reminder hours before its start.</summary>
    private static DateTime ReminderDueAt(Reservation reservation, ReservationPolicy policy) =>
        reservation.StartUtc.AddHours(-policy.ReminderHoursBefore);

    /// <summary>
    /// Writes the reminder - with its feed entry - and the late nudge for the booking as it now stands,
    /// into the caller's unit of work.
    /// </summary>
    /// <param name="db">The caller's unit of work, for the feed entry.</param>
    /// <param name="outbox">The caller's outbox, for the messages.</param>
    /// <param name="reservation">The booking, at its current start.</param>
    /// <param name="branch">Its branch, with the venue, for the names the messages carry.</param>
    /// <param name="tableLabel">The table, as the messages name it.</param>
    /// <param name="manageUrl">The manage link for a web booking, or null.</param>
    /// <param name="keyVersion">
    /// Null for a new booking, whose keys are <c>reservation:{id}:reminder</c> and
    /// <c>reservation:{id}:late-nudge</c>. A moved booking's carry the start they are for, because the
    /// messages for an earlier start keep their keys once sent.
    /// </param>
    /// <param name="writeFeed">Whether the account is live, under its lock, so the feed entry may be written.</param>
    /// <param name="delivered">Keys already sent: a message under one of them went out and is not written again.</param>
    /// <param name="nowUtc">Now.</param>
    private static void EnqueueReminders(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        string tableLabel,
        string? manageUrl,
        string? keyVersion,
        bool writeFeed,
        IReadOnlySet<string> delivered,
        DateTime nowUtc)
    {
        var policy = branch.ReservationPolicy;

        var notice = new
        {
            reservationId = reservation.Id,
            dinerUserId = reservation.DinerUserId,
            venueName = branch.Venue?.Name ?? branch.Name,
            branchName = branch.Name,
            tableLabel,
            localStartTime = reservation.LocalStartTime,
            reservationCode = reservation.Code,
            graceExtensionMinutes = policy.GraceExtensionMinutes,
            approved = false,
            manageUrl,
        };

        string KeyFor(string what) =>
            OutboxMessageTypes.KeyFor("reservation", reservation.Id, keyVersion is null ? what : $"{what}:{keyVersion}");

        var remindAt = ReminderDueAt(reservation, policy);
        var reminderKey = KeyFor("reminder");

        // A reminder whose moment has already passed is not written at all: a booking made an hour
        // before it starts has no three-hours-before, and queueing one in the past would only be
        // discarded by the staleness rule with a log line implying something went wrong.
        //
        // Nor one due while a short-notice booking is still waiting for the floor's answer. "Your table
        // is at 20:00" about a table nobody has promised yet is the wrong thing to say, and the answer
        // itself - "confirmed, they expect you at 20:00" - says the right one minutes later.
        var dueBeforeTheAnswer = reservation.ApprovalExpiresAtUtc is { } answerBy && remindAt <= answerBy;

        if (remindAt > nowUtc && !dueBeforeTheAnswer && !delivered.Contains(reminderKey))
        {
            outbox.Enqueue(OutboxMessageTypes.ReservationReminder, notice, remindAt, reminderKey);

            // Its feed entry, written now beside the push and appearing when the push is due (K12).
            // Cancelling the booking before then deletes both.
            if (writeFeed)
            {
                DinerNotices.BookingReminder(db, reservation, branch.Venue?.Name ?? branch.Name, branch.Name, remindAt);
            }
        }

        // The nudge is always in the future when the booking is made or moved, because neither can
        // start in the past, so it has no "already passed" rule. Only one already sent under the same
        // key - a move back to a start whose nudge went out - is not written twice.
        var nudgeKey = KeyFor("late-nudge");

        if (!delivered.Contains(nudgeKey))
        {
            outbox.Enqueue(
                OutboxMessageTypes.ReservationLateNudge,
                notice,
                reservation.StartUtc.AddMinutes(policy.LateNudgeAfterMinutes),
                nudgeKey);
        }
    }

    // ---------------------------------------------------------------- the diner extends their hold

    public async Task<ExtendHoldResult> ExtendHoldAsync(
        ExtendHoldCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Extend a hold");

        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Extending a hold", cancellationToken);
        var reservation = await LoadReservationAsync(command.ReservationId, cancellationToken);

        // Their own booking, read from the token rather than the body. A diner must not be able to
        // hold somebody else's table by guessing an id.
        if (reservation.DinerUserId != dinerUserId)
        {
            throw new TabPermissionException("Extending this hold", "the diner who made the booking");
        }

        // Already extended by this exact command: answer with what it did rather than refusing. The
        // notification is tappable twice and the second tap is not an error.
        if (reservation.GraceExtensionsUsed > 0
            && await db.TableStateChanges.AnyAsync(
                c => c.ClientCommandId == command.ClientCommandId, cancellationToken))
        {
            return new ExtendHoldResult(
                reservation.Id, reservation.HoldExpiresAtUtc ?? clock.UtcNow, 0, 0, WasReplay: true);
        }

        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var minutes = branch.ReservationPolicy.GraceExtensionMinutes;

        // Throws when the one extension is spent, or the booking is not confirmed.
        reservation.ExtendHold(clock.UtcNow, minutes);

        await db.SaveChangesAsync(cancellationToken);

        // Onto the branch change sequence, so the waiter watching that table sees it. Held to Held -
        // nothing about the table changed, but something happened at it.
        await tableState.RecordHoldExtendedAsync(
            new TableStateCommand(
                reservation.BranchId,
                reservation.DiningTableId,
                command.ClientCommandId,
                $"the diner extended their hold by {minutes} minutes"),
            reservation.Id,
            reservation.HoldExpiresAtUtc!.Value,
            cancellationToken);

        logger.LogInformation(
            "Booking {Code} extended its hold to {HoldExpiresAtUtc} ({Minutes} minutes).",
            reservation.Code, reservation.HoldExpiresAtUtc, minutes);

        return new ExtendHoldResult(
            reservation.Id, reservation.HoldExpiresAtUtc!.Value, minutes, 0, WasReplay: false);
    }

    // ---------------------------------------------------------------- releasing a late booking

    public async Task<ReservationReleaseResult> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await LoadReservationAsync(command.ReservationId, cancellationToken);
        var staffId = await RequireStaffForBranchAsync(reservation.BranchId, "Release a booking", cancellationToken);

        var table = await db.DiningTables
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == reservation.DiningTableId, cancellationToken)
            ?? throw new KeyNotFoundException($"Table {reservation.DiningTableId} was not found.");

        // Already let go. A tablet replaying its queue must not count a second no-show against a
        // diner, and the answer it gets is the one the first attempt got.
        if (reservation.Status is ReservationStatus.NoShow or ReservationStatus.CancelledByVenue)
        {
            logger.LogInformation(
                "Booking {Code} was already released as {Status}; returning that answer.",
                reservation.Code, reservation.Status);

            return await BuildReleaseAsync(
                reservation, OutcomeOf(reservation.Status), tableFreed: false, table.Id, table.Label,
                table.Status, wasReplay: true, cancellationToken);
        }

        var nowUtc = clock.UtcNow;
        var reason = command.Reason ?? DefaultReleaseReason(command.Outcome);

        if (command.Outcome == ReleaseOutcome.NoShow)
        {
            reservation.MarkNoShow(nowUtc);
        }
        else
        {
            reservation.CancelByVenue(nowUtc, reason);
        }

        // As for a cancellation: the table is free of this booking, so a question it asked goes, and a
        // booking waiting on this one's guest no longer has to.
        var releasedFrom = await LoadBranchAsync(reservation.BranchId, cancellationToken);

        await TableShares.CloseForLaterAsync(db, reservation.Id, TableShareRequestStatus.Withdrawn, nowUtc, cancellationToken);
        await TableShares.EarlierChangedAsync(db, outbox, reservation, releasedFrom, nowUtc, cancellationToken);

        // Whatever is left unsent for this booking goes with it. A nudge asking "still coming?"
        // after a waiter has already given the table away is the worst of both.
        await outbox.CancelAsync(
            OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        // The venue let an accepted booking go, and the diner's feed hears about it (K12). No push has
        // ever been sent for this; the entry is saved with the release. A no-show tells nobody.
        if (command.Outcome != ReleaseOutcome.NoShow && reservation.DinerUserId is not null)
        {
            var releasedAt = await LoadBranchAsync(reservation.BranchId, cancellationToken);

            DinerNotices.BookingCancelledByVenue(
                db, reservation, releasedAt.Venue?.Name ?? releasedAt.Name, releasedAt.Name, nowUtc);
        }

        await db.SaveChangesAsync(cancellationToken);

        // The table is freed only when it is being held for THIS booking. A table somebody is
        // sitting at is left exactly as it is: the booking is released either way, but a floor plan
        // that shows an occupied table as free costs more than a stale hold does.
        var freed = false;
        var tableStatus = table.Status;

        if (table.Status == TableStatus.Held
            && await HoldIsForAsync(table.Id, reservation.Id, cancellationToken))
        {
            // ReleaseHold, not FreeTable: the machine has no Held-to-Free transition, because
            // vacating is what happens when a party leaves and a hold has nobody at it. Going
            // through the machine at all is the point - the audit row is written and the branch
            // change sequence moves, which is what puts this on every other tablet in the room.
            var change = await tableState.ReleaseHoldAsync(
                new TableStateCommand(reservation.BranchId, table.Id, command.ClientCommandId, reason),
                cancellationToken);

            freed = true;
            tableStatus = change.ToStatus;
        }

        logger.LogInformation(
            "Booking {Code} released as {Outcome} by staff {StaffId}. Table {TableLabel} {TableAction}.",
            reservation.Code, command.Outcome, staffId, table.Label,
            freed ? "was freed" : $"was left {tableStatus}");

        return await BuildReleaseAsync(
            reservation, command.Outcome, freed, table.Id, table.Label, tableStatus,
            wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// Whether the table's current hold was placed for this booking.
    /// </summary>
    /// <remarks>
    /// <c>DiningTable</c> stores that it is <c>Held</c> and not who for - the hold is a physical
    /// fact and the booking is a financial one, and coupling them on the row was the wrong trade.
    /// The audit log knows: the most recent transition into <c>Held</c> names the booking it was
    /// placed for. Releasing a different booking must not free a table being held for somebody else.
    /// </remarks>
    private async Task<bool> HoldIsForAsync(Guid tableId, Guid reservationId, CancellationToken cancellationToken)
    {
        var mostRecentHold = await db.TableStateChanges
            .AsNoTracking()
            .Where(c => c.DiningTableId == tableId && c.ToStatus == TableStatus.Held)
            .OrderByDescending(c => c.Sequence)
            .Select(c => c.ReservationId)
            .FirstOrDefaultAsync(cancellationToken);

        return mostRecentHold == reservationId;
    }

    private static ReleaseOutcome OutcomeOf(ReservationStatus status) =>
        status == ReservationStatus.NoShow ? ReleaseOutcome.NoShow : ReleaseOutcome.CancelledByVenue;

    private static string DefaultReleaseReason(ReleaseOutcome outcome) =>
        outcome == ReleaseOutcome.NoShow
            ? "released: nobody arrived"
            : "released: the venue let the booking go";

    private async Task<ReservationReleaseResult> BuildReleaseAsync(
        Reservation reservation,
        ReleaseOutcome outcome,
        bool tableFreed,
        Guid tableId,
        string tableLabel,
        TableStatus tableStatus,
        bool wasReplay,
        CancellationToken cancellationToken)
    {
        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);

        var view = await ToViewAsync(
            reservation, branch, table: null, wasReplay: wasReplay, trigger: null, cancellationToken);

        return new ReservationReleaseResult(
            view,
            outcome,
            tableFreed,
            tableId,
            tableLabel,
            tableStatus,

            // Stated rather than implied. A client should be able to tell the waiter "this one goes
            // on their record" without knowing the rule.
            CountsTowardNoShowThreshold: outcome == ReleaseOutcome.NoShow,
            wasReplay);
    }

    /// <summary>
    /// A waiter or above, at this branch. The lighter cousin of the manager check below.
    /// </summary>
    /// <remarks>
    /// Releasing a late booking is floor work, not a decision about money: the person standing at
    /// the table is the one who knows nobody came. Requiring a manager would mean the table stays
    /// held until somebody senior walks past, which is how the feature ends up unused. The same goes
    /// for saying yes or no to a booking, and for the list of the ones waiting: a short-notice request
    /// is a question about a table only the floor can see, with fifteen minutes to answer it.
    /// </remarks>
    private async Task<Guid> RequireStaffForBranchAsync(
        Guid branchId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role is not { } tokenRole
            || !IsFloorStaff(tokenRole))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        // The stored role, not only the token's: a waiter moved to the kitchen keeps a waiter's token
        // until it expires, and must not keep a waiter's reach with it - the same rule the manager
        // guard applies to a demoted manager.
        if (!IsFloorStaff(staff.Role))
        {
            throw new StaffPermissionException(operation, staff.Role, StaffRole.Waiter);
        }

        if (staff.Role == StaffRole.PlatformAdmin || staff.BranchId == branchId)
        {
            return staffId;
        }

        // An owner or general manager is venue-scoped rather than branch-scoped, which is the point
        // of those accounts. A branch manager and a waiter are confined to their own branch, which
        // the line above has already admitted.
        if (StaffRoleRules.CoversWholeVenue(staff.Role)
            && staff.VenueId is { } venueId
            && await authorization.BranchBelongsToVenueAsync(branchId, venueId, cancellationToken))
        {
            return staffId;
        }

        throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
    }

    /// <summary>A waiter or above: the roles that work the floor - and decide what it can take.</summary>
    private static bool IsFloorStaff(StaffRole role) =>
        role is StaffRole.Waiter or StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner
            or StaffRole.PlatformAdmin;

    // ---------------------------------------------------------------- permissions

    /// <summary>
    /// The calling diner.
    /// </summary>
    /// <remarks>
    /// Reads identity from <see cref="ICurrentActor"/> and nowhere else, which is now the token's
    /// claims by way of <c>ClaimsCurrentActor</c>. The <c>VerifiedDiner</c> policy on the route has
    /// already refused anyone who is not a diner with an account; this is the second half of that
    /// answer - the id the booking is filed under - and it fails closed if the two ever disagree.
    /// Staff taking a booking over the phone is a separate operation and is not this one.
    /// </remarks>
    private Guid RequireDiner(string operation)
    {
        if (actor.Type != ActorType.Diner || actor.DinerUserId is not { } dinerUserId || dinerUserId == Guid.Empty)
        {
            throw new UnauthorizedAccessException($"{operation} requires a verified diner account.");
        }

        return dinerUserId;
    }

    // ---------------------------------------------------------------- loading

    private async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .Include(b => b.OpeningHours)
            .Include(b => b.Venue)
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

        return branch ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");
    }

    private async Task<DiningTable> LoadTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken)
    {
        // With its room, which the booking's view names.
        var table = await db.DiningTables
            .AsNoTracking()
            .Include(t => t.FloorArea)
            .FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken);

        if (table is null || !table.IsActive)
        {
            throw new KeyNotFoundException($"Table {tableId} was not found.");
        }

        if (table.BranchId != branchId)
        {
            // A branch mismatch is a bad request, not a missing table: answering 404 would tell a
            // caller the table does not exist when it does.
            throw new ArgumentException(
                $"Table {tableId} does not belong to branch {branchId}.", nameof(branchId));
        }

        return table;
    }

    private async Task<Reservation> LoadReservationAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        return reservation ?? throw new KeyNotFoundException($"Reservation {reservationId} was not found.");
    }

    // ---------------------------------------------------------------- views

    /// <summary>
    /// Builds the view, fetching only the table facts the caller did not already have in hand.
    /// </summary>
    /// <remarks>
    /// A <paramref name="table"/> passed in must be the booking's own and loaded with its room, as
    /// <see cref="LoadTableAsync"/> loads it.
    /// </remarks>
    private async Task<ReservationView> ToViewAsync(
        Reservation reservation,
        Branch branch,
        DiningTable? table,
        bool wasReplay,
        ApprovalTrigger? trigger,
        CancellationToken cancellationToken,
        string? manageToken = null)
    {
        var facts = table is not null
            ? new TableFacts(table.Label, table.FloorAreaId, table.FloorArea?.Name)
            : await db.DiningTables
                  .AsNoTracking()
                  .Where(t => t.Id == reservation.DiningTableId)
                  .Select(t => new TableFacts(
                      t.Label, t.FloorAreaId, t.FloorArea == null ? null : t.FloorArea.Name))
                  .FirstOrDefaultAsync(cancellationToken)
              ?? new TableFacts(string.Empty, null, null);

        // The question to this booking's guest, on their own views of it only.
        IncomingRow? incoming = null;

        if (actor.Type == ActorType.Diner
            && actor.DinerUserId is { } caller
            && reservation.DinerUserId == caller)
        {
            var nowUtc = clock.UtcNow;

            incoming = await db.TableShareRequests
                .AsNoTracking()
                .Where(q => q.EarlierReservationId == reservation.Id
                            && q.Status == TableShareRequestStatus.Pending
                            && q.AsksEarlierGuest
                            && q.ExpiresAtUtc > nowUtc)
                .OrderBy(q => q.CreatedAtUtc)
                .Select(q => new IncomingRow
                {
                    RequestId = q.Id,
                    RequestedStartUtc = q.RequestedStartUtc,
                    FreeByUtc = q.FreeByUtc,
                    ExpiresAtUtc = q.ExpiresAtUtc,
                })
                .FirstOrDefaultAsync(cancellationToken);
        }

        return BuildView(
            reservation,
            branch.Name,
            branch.TimeZoneId,
            facts,
            branch.ReservationPolicy.CancellationDeadlineMinutes,
            wasReplay,
            trigger,
            manageToken,
            incoming);
    }

    private static ReservationView ToView(MineRow row) =>
        BuildView(
            row.Reservation,
            row.BranchName,
            row.TimeZoneId,
            new TableFacts(row.TableLabel, row.RoomId, row.RoomName),
            row.CancellationDeadlineMinutes,
            wasReplay: false,
            trigger: null,
            incoming: row.Incoming);

    private static ReservationView BuildView(
        Reservation reservation,
        string branchName,
        string timeZoneId,
        TableFacts table,
        int cancellationDeadlineMinutes,
        bool wasReplay,
        ApprovalTrigger? trigger,
        string? manageToken = null,
        IncomingRow? incoming = null)
    {
        var zone = BranchZone.For(timeZoneId);

        return new ReservationView
        {
            Id = reservation.Id,
            Code = reservation.Code,
            BranchId = reservation.BranchId,
            BranchName = branchName,
            TimeZoneId = timeZoneId,
            TableId = reservation.DiningTableId,
            TableLabel = table.Label,
            RoomId = table.RoomId,
            RoomName = table.RoomName,
            PartySize = reservation.PartySize,
            StartUtc = reservation.StartUtc,
            EndUtc = reservation.EndUtc,
            LocalDate = reservation.LocalDate,
            LocalStartTime = reservation.LocalStartTime,

            // Derived for display only. The stored wall-clock start is the one the diner was
            // shown and is never recomputed.
            LocalEndTime = zone.LocalTimeAt(reservation.EndUtc),

            Status = reservation.Status,
            GuestName = reservation.GuestName,
            GuestPhone = reservation.GuestPhone,

            // What the diner asked for, on every view of the booking - the approval queue included,
            // which is where the venue reads it (K9).
            Note = reservation.Note,
            ConfirmedAtUtc = reservation.ConfirmedAtUtc,
            CancelledAtUtc = reservation.CancelledAtUtc,
            CancellationReason = reservation.CancellationReason,
            CancelledAfterDeadline = reservation.CancelledAfterDeadline,
            CancellationDeadlineUtc =
                ReservationRules.CancellationDeadline(reservation.StartUtc, cancellationDeadlineMinutes),
            ClientCommandId = reservation.ClientCommandId,
            WasReplay = wasReplay,

            // The reason stored when it went to approval; the one passed in only for a row from before
            // the column, which has none (see PendingTriggerFor).
            AwaitingApprovalBecause = reservation.Status == ReservationStatus.PendingApproval
                ? reservation.ApprovalTrigger ?? trigger
                : null,

            // Cleared on the booking the moment it stops waiting, so this is only ever a live deadline.
            ApprovalExpiresAtUtc = reservation.Status == ReservationStatus.PendingApproval
                ? reservation.ApprovalExpiresAtUtc
                : null,
            // Only on a booking that ended declined. A confirmed one carrying a reason is one whose last
            // change was declined and that went back to the slot it had - it is not declined.
            DeclinedBecause = reservation.Status == ReservationStatus.CancelledByVenue
                ? reservation.DeclineReason
                : null,

            // Null on every path but creation. The server holds only the hash, so a later read
            // could not return it even if one wanted to.
            ManageToken = manageToken,

            // The time the guest promised to free the table by, kept on the row (EndUtc is cut to it).
            PromisedFreeByUtc = reservation.PromisedFreeByUtc,

            // Only on a booking still holding its table: a cancelled one has nothing to answer about.
            IncomingTableRequest = incoming is not null && ReservationOverlap.Blocks(reservation.Status)
                ? new IncomingTableRequest(
                    incoming.RequestId,
                    incoming.RequestedStartUtc,
                    incoming.FreeByUtc,
                    incoming.ExpiresAtUtc,
                    zone.LocalTimeAt(incoming.RequestedStartUtc),
                    zone.LocalTimeAt(incoming.FreeByUtc))
                : null,
        };
    }

    /// <summary>What a booking lands as: the status, why it waits, and until when the venue may answer.</summary>
    private sealed record StatusDecision(
        ReservationStatus Status,
        ApprovalTrigger? Trigger,
        DateTime? ApprovalExpiresAtUtc);

    /// <summary>The question a booking asks the booking before it on the table, before it is staged.</summary>
    /// <param name="EarlierReservationId">The booking whose guest is asked.</param>
    /// <param name="FreeByUtc">The time to have left by.</param>
    /// <param name="ExpiresAtUtc">The deadline - the later booking's own <c>ApprovalExpiresAtUtc</c>.</param>
    /// <param name="AsksEarlierGuest">In the app; or false, the floor asks.</param>
    private sealed record ShareAsk(
        Guid EarlierReservationId,
        DateTime FreeByUtc,
        DateTime ExpiresAtUtc,
        bool AsksEarlierGuest);

    /// <summary>
    /// What a change of booking records in <c>ProcessedCommands</c>: which booking, and where it went.
    /// </summary>
    /// <remarks>
    /// Not the response. A replay answers with the booking as it stands, and a stored view would carry
    /// the guest's name and number into one more table.
    /// </remarks>
    private sealed record RescheduleRecord(
        Guid ReservationId,
        Guid TableId,
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

    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web);

    /// <summary>What a booking's view says about its table: the label, and the room it is in.</summary>
    private sealed record TableFacts(string Label, Guid? RoomId, string? RoomName);

    private sealed class MineRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public string TableLabel { get; init; } = null!;

        public Guid? RoomId { get; init; }

        public string? RoomName { get; init; }

        /// <summary>The branch's setting as it stands, which a cancellation now is judged by.</summary>
        public int CancellationDeadlineMinutes { get; init; }

        /// <summary>A later guest's open request for this booking's table, or null.</summary>
        public IncomingRow? Incoming { get; init; }
    }

    /// <summary>A later guest's open request for a booking's table, as the read finds it.</summary>
    private sealed class IncomingRow
    {
        public Guid RequestId { get; init; }

        public DateTime RequestedStartUtc { get; init; }

        public DateTime FreeByUtc { get; init; }

        public DateTime ExpiresAtUtc { get; init; }
    }

    /// <summary>One booking awaiting approval, with its table's evening, as the single query reads it.</summary>
    private sealed class AwaitingRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        /// <summary>The owned policy, for the deadline and for the reason of a row from before the column.</summary>
        public ReservationPolicy Policy { get; init; } = null!;

        public string TableLabel { get; init; } = null!;

        public Guid? RoomId { get; init; }

        public string? RoomName { get; init; }

        public AwaitingPreviousBooking? Previous { get; init; }

        public AwaitingNextBooking? Next { get; init; }

        public AwaitingCurrentSitting? Sitting { get; init; }

        public DateTime? EarlierGuestFreeByUtc { get; init; }
    }
}
