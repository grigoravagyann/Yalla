using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner books a table: the gates, the insert under the table's lock, and the reminders written beside
/// it - see <see cref="ReservationService"/> for which commands take that lock, and why.
/// </summary>
internal sealed class ReservationCreation(
    YallaDbContext db,
    IClock clock,
    ReservationWriter reservations,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationSlotRules slots,
    ReservationViews views,
    ReservationReminders reminders)
{
    public async Task<ReservationView> CreateAsync(
        CreateReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = lookups.RequireDiner("Book a table");

        // The note is part of the body, so it is refused before anything is looked up: a 422 naming
        // `note` is the answer whatever else would have gone on to be refused (K9).
        var note = Reservation.NormaliseNote(command.Note);

        // The account, read from the row. Before the replay, too: an account that cannot book has no original
        // to be handed back. A deactivated or deleted one books nothing, and neither does one whose number was
        // never proved - a booking's reminders, its record and the per-number rules land on the number, so it must
        // be a real one. (Yalla#54 let an unproved account book; the user withdrew that the same day.)
        var account = await db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == dinerUserId)
            .Select(d => new { d.PhoneE164, Proved = d.PhoneVerifiedAtUtc != null, Live = d.IsActive && d.DeletedAtUtc == null })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is not { Live: true, Proved: true })
        {
            throw new Domain.Identity.PhoneNotVerifiedException(dinerUserId, "Booking a table");
        }

        var booker = new BookerKey(dinerUserId, BookerPhone(account.PhoneE164));

        return await CreateCoreAsync(command, note, booker, cancellationToken);
    }

    /// <summary>
    /// The number an account books under: its own, in the canonical form - or as stored when that is not
    /// parseable.
    /// </summary>
    internal static string? BookerPhone(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return Domain.Common.PhoneNumber.Canonical(Domain.Common.PhoneNumber.Normalise(stored, "phoneE164"));
        }
        catch (ArgumentException)
        {
            return stored.Trim();
        }
    }

    /// <summary>A diner's booking - see <see cref="CreateAsync"/>.</summary>
    private async Task<ReservationView> CreateCoreAsync(
        CreateReservationCommand command,
        string? note,
        BookerKey booker,
        CancellationToken cancellationToken)
    {
        // Cheap path first: a retry that arrives after the original committed never reaches the
        // lock at all. The unique index below is what makes the racing case safe.
        if (await reservations.FindReplayAsync(command.ClientCommandId, booker.DinerUserId, cancellationToken)
            is { } replay)
        {
            logger.LogInformation(
                "Booking command {ClientCommandId} was already applied as {Code}; returning the original.",
                command.ClientCommandId, replay.Code);

            var replayBranch = await lookups.LoadBranchAsync(replay.BranchId, cancellationToken);

            return await views.ToViewAsync(
                replay, replayBranch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        // Blocked for no-shows: said before anything about the table - after the replay, so a retry of a booking
        // made just before the block answers as the booking (review N7). The writer asks again under the
        // account's lock, which is what makes it hold against a block being written this moment.
        await GuestRecords.RequireNotBlockedAsync(db, booker.DinerUserId, booker.PhoneE164, clock.UtcNow, cancellationToken);

        var branch = await lookups.LoadBranchAsync(command.BranchId, cancellationToken);

        // A venue that stopped paying, or one that was deleted, takes no new bookings - a diner
        // whose app cached the branch id must not get past the fact that it vanished from search.
        // Only creation and moving a booking are gated: cancelling and seating one still work.
        VenueGate.RequireOpenForBusiness(branch);

        ReservationSlotRules.RequireChannelAccepted(command.Channel, branch, viaManageLink: false);

        // And one that has not finished its required setup - no tablet to see the booking arrive,
        // say - is not taking guests at all (DinerVisibility).
        await DinerVisibility.RequireTakingGuestsAsync(db, branch.Id, cancellationToken);

        var table = await lookups.LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        var policy = branch.ReservationPolicy;
        var zone = BranchZone.For(branch.TimeZoneId);
        var nowUtc = clock.UtcNow;

        // Throws LocalTimeDoesNotExistException for an hour a spring-forward skips, rather than
        // letting TimeZoneInfo's ArgumentException escape as a 500.
        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var endUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);
        var proposed = new BookedInterval(startUtc, endUtc);

        var pinned = await slots.IsPinnedOnItsRoomAsync(table, cancellationToken);
        var hours = await lookups.HoursAroundAsync(branch.Id, command.LocalDate, cancellationToken);

        ReservationSlotRules.Validate(command.LocalDate, command.LocalTime, command.PartySize, table, pinned, hours, policy, zone, proposed, nowUtc);

        // Every booking here is a diner's - the app's or the public page's, whatever channel the
        // client reports, because the channel is self-reported and a rule it could switch off would be
        // no rule. Tonight's are the floor's to decide.
        var shortNotice = ShortNoticeRule.Applies(startUtc, command.LocalDate, nowUtc, zone.LocalDateAt(nowUtc));

        var status = await slots.DecideStatusAsync(
            command.PartySize,
            approvedPartySize: null,
            policy,
            booker.DinerUserId,
            startUtc,
            shortNotice,
            nowUtc,
            cancellationToken);

        // Minted here and returned exactly once, below. Only the hash is stored, so this plaintext
        // exists in this method and in the response and nowhere else - which is what makes it safe
        // to put in a URL somebody will paste into WhatsApp.
        var manageToken = Secrets.NewOpaqueToken();
        var manageTokenHash = Secrets.Hash(manageToken);
        ReservationSlotRules.StatusDecision decided = status;

        // The one way a booking row is created. Everything above this line is validation and
        // everything below it is presentation; the concurrency-critical part is all in there - and
        // what the booking becomes, which the table's evening decides, is built inside it.
        var outcome = await InsertUnderTheTableLockAsync(
            command.ClientCommandId,
            table,
            startUtc,
            policy,
            booker,
            command.AcceptsFreeByUtc,
            async (verdict, ct) =>
            {
                var withTable = await slots.DecideWithTheTableAsync(verdict, status, shortNotice, startUtc, nowUtc, ct);
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
                    dinerUserId: booker.DinerUserId,
                    stayHint: command.StayHint,
                    clientCommandId: command.ClientCommandId,
                    channel: command.Channel,
                    note: note,
                    approvalTrigger: withTable.Status.Trigger,
                    approvalExpiresAtUtc: withTable.Status.ApprovalExpiresAtUtc,
                    promisedFreeByUtc: verdict.MustFreeByUtc,
                    bookerPhoneE164: booker.PhoneE164);

                booking.AttachManageToken(manageTokenHash);

                // Made: the History's line, the table's change stream and - for one that waits - the floor's push are
                // the event's handlers', in the insert's save.
                booking.Book(nowUtc);

                if (withTable.Ask is { } ask)
                {
                    TableShares.Ask(
                        db, booking, ask.EarlierReservationId, ask.FreeByUtc, ask.ExpiresAtUtc, ask.AsksEarlierGuest,
                        ask.FallbackTrigger, nowUtc);
                }

                return booking;
            },
            cancellationToken);

        if (outcome.Replay is { } winner)
        {
            return await views.ToViewAsync(winner, branch, table, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.Occupied is { } sitting)
        {
            // Outside the lock, like the booking conflict below: assembling the floor snapshot is
            // a wide read and holding the table's lock through it would queue every other booker
            // behind a response body.
            throw await slots.OccupiedAsync(SnapshotFor(command), table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            // Outside the lock, deliberately. The floor snapshot is a wide read and holding a
            // table's lock while assembling it would queue every other booker behind a response
            // body.
            throw await slots.ConflictAsync(SnapshotFor(command), table, proposed, conflict, cancellationToken);
        }

        if (outcome.FreeBy is { } freeBy)
        {
            // A full sitting runs into the next booking, and the diner has not agreed to leave in time.
            throw ReservationSlotRules.FreeByNotAccepted(table, freeBy, zone, command.AcceptsFreeByUtc);
        }

        var reservation = outcome.Booking!;

        // The reminder and the nudge, written in the same unit of work as the booking. A booking
        // that exists without its reminder is the failure mode an outbox is for, and the diner finds
        // out about it by not being reminded.
        await reminders.ScheduleRemindersAsync(reservation, branch, table.Label, manageToken, cancellationToken);

        logger.LogInformation(
            "Booked table {TableLabel} at branch {BranchId} for {PartySize} as {Code} ({Status}).",
            table.Label, branch.Id, command.PartySize, reservation.Code, reservation.Status);

        return await views.ToViewAsync(
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
        BookerKey booker,
        DateTime? acceptsFreeByUtc,
        Func<TableSequencingVerdict, CancellationToken, Task<Reservation>> stageAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.InsertUnderTableLockAsync(
                clientCommandId, table, startUtc, policy, booker, acceptsFreeByUtc, stageAsync, cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

    /// <summary>The floor a refused booking is answered with: the slot it asked about.</summary>
    private static AvailabilityRequest SnapshotFor(CreateReservationCommand command) =>
        new(command.BranchId, command.PartySize, command.LocalDate, command.LocalTime);
}
