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
        // Only creation is gated: cancelling and seating an existing booking still work.
        VenueGate.RequireOpenForBusiness(branch);

        // A branch that never agreed to take bookings from its public page does not take them.
        // Checked here rather than at the endpoint because the public page books through this same
        // service - there is no separate public booking route to guard - and because a rule that
        // lives in the service holds for any caller, not only the one that remembered.
        RequireWebBookingsAccepted(command.Channel, branch);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        var policy = branch.ReservationPolicy;
        var zone = BranchZone.For(branch.TimeZoneId);
        var nowUtc = clock.UtcNow;

        // Throws LocalTimeDoesNotExistException for an hour a spring-forward skips, rather than
        // letting TimeZoneInfo's ArgumentException escape as a 500.
        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var endUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);
        var proposed = new BookedInterval(startUtc, endUtc);

        Validate(command, table, branch, policy, zone, proposed, nowUtc);

        var status = await DecideStatusAsync(command, policy, dinerUserId, nowUtc, cancellationToken);

        var reservation = Reservation.Create(
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
            initialStatus: status.Status,
            dinerUserId: dinerUserId,
            stayHint: command.StayHint,
            clientCommandId: command.ClientCommandId,
            channel: command.Channel);

        // Minted here and returned exactly once, below. Only the hash is stored, so this plaintext
        // exists in this method and in the response and nowhere else - which is what makes it safe
        // to put in a URL somebody will paste into WhatsApp.
        var manageToken = Secrets.NewOpaqueToken();
        reservation.AttachManageToken(Secrets.Hash(manageToken));

        // The one way a booking row is created. Everything above this line is validation and
        // everything below it is presentation; the concurrency-critical part is all in there.
        var outcome = await InsertUnderTheTableLockAsync(
            reservation, table, proposed, policy, dinerUserId, cancellationToken);

        if (outcome.Replay is { } winner)
        {
            return await ToViewAsync(winner, branch, table, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.Occupied is { } sitting)
        {
            // Outside the lock, like the booking conflict below: assembling the floor snapshot is
            // a wide read and holding the table's lock through it would queue every other booker
            // behind a response body.
            throw await OccupiedAsync(command, table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            // Outside the lock, deliberately. The floor snapshot is a wide read and holding a
            // table's lock while assembling it would queue every other booker behind a response
            // body.
            throw await ConflictAsync(command, table, proposed, conflict, cancellationToken);
        }

        // The reminder and the nudge, written in the same unit of work as the booking. A booking
        // that exists without its reminder is the failure mode an outbox is for, and the diner finds
        // out about it by not being reminded.
        await ScheduleRemindersAsync(reservation, branch, table.Label, manageToken, cancellationToken);

        logger.LogInformation(
            "Booked table {TableLabel} at branch {BranchId} for {PartySize} as {Code} ({Status}).",
            table.Label, branch.Id, command.PartySize, reservation.Code, reservation.Status);

        return await ToViewAsync(
            reservation, branch, table, wasReplay: false, status.Trigger, cancellationToken, manageToken);
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
        Reservation reservation,
        DiningTable table,
        BookedInterval proposed,
        ReservationPolicy policy,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.InsertUnderTableLockAsync(
                reservation, table, proposed, policy, dinerUserId, cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

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

        // Cancelling the cause cancels the message, in the same transaction. A reminder arriving for
        // a booking somebody cancelled an hour ago is worse than no reminder at all - it is the push
        // the diner remembers, and it teaches them the notifications are wrong.
        await outbox.CancelAsync(
            OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

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
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,
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
        // The same check approve and reject make. BranchScoped on the route widens a manager with
        // a home branch to their whole venue; this narrows them back, so the list never shows a
        // booking its reader would be refused permission to decide.
        await RequireManagerForBranchAsync(branchId, "List a branch's bookings", cancellationToken);

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
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => BuildView(
                r.Reservation,
                r.BranchName,
                r.TimeZoneId,
                r.TableLabel,
                policy.CancellationDeadlineMinutes,
                wasReplay: false,
                trigger: PendingTriggerFor(r.Reservation, policy)))
            .ToList();
    }

    /// <summary>
    /// Why a pending booking is waiting, worked out again at read time.
    /// </summary>
    /// <remarks>
    /// The trigger is not stored on the booking, so this replays <see cref="DecideStatusAsync"/>'s
    /// order against the branch's current policy: approve-everything first, then the party-size
    /// threshold, and otherwise the one remaining reason, the diner's no-show history. A policy
    /// changed since the booking was made can therefore name a different reason than the diner was
    /// shown - it never names one for a booking that is not pending.
    /// </remarks>
    private static ApprovalTrigger? PendingTriggerFor(Reservation reservation, ReservationPolicy policy)
    {
        if (reservation.Status != ReservationStatus.PendingApproval)
        {
            return null;
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
        CreateReservationCommand command,
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
            availability = await availabilityQuery.GetAvailabilityAsync(
                new AvailabilityRequest(command.BranchId, command.PartySize, command.LocalDate, command.LocalTime),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the occupancy conflict.");
        }

        return new TableCurrentlyOccupiedException(
            command.BranchId,
            table.Id,
            table.Label,
            proposed,
            sitting.SeatedAtUtc,
            projected.EndUtc,
            sitting.Id,
            availability);
    }

    private async Task<TableAlreadyBookedException> ConflictAsync(
        CreateReservationCommand command,
        DiningTable table,
        BookedInterval proposed,
        Reservation conflict,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Table {TableLabel} was taken for {Start:o} before this booking committed; it clashes with {Code}.",
            table.Label, proposed.StartUtc, conflict.Code);

        BranchAvailability? availability = null;

        try
        {
            // So the app can redraw the floor and show what changed, rather than only being told
            // no. Best effort: failing to build it must not replace a 409 the client can act on
            // with a 500 it cannot.
            availability = await availabilityQuery.GetAvailabilityAsync(
                new AvailabilityRequest(command.BranchId, command.PartySize, command.LocalDate, command.LocalTime),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the booking conflict.");
        }

        return new TableAlreadyBookedException(
            command.BranchId,
            table.Id,
            table.Label,
            proposed,
            BookedInterval.Of(conflict),
            conflict.Id,
            availability);
    }

    // ---------------------------------------------------------------- rules

    /// <summary>
    /// Every branch rule, each answering with its own named exception.
    /// </summary>
    /// <remarks>
    /// The rules themselves are pure functions in the domain and are shared with the availability
    /// read model, so a table the screen offers is a table this method accepts. All this does is
    /// turn the reason into the exception that carries the numbers behind it.
    /// </remarks>
    private static void Validate(
        CreateReservationCommand command,
        DiningTable table,
        Branch branch,
        ReservationPolicy policy,
        BranchZone zone,
        BookedInterval proposed,
        DateTime nowUtc)
    {
        var localToday = zone.LocalDateAt(nowUtc);

        var timing = ReservationRules.CheckTiming(
            proposed.StartUtc, nowUtc, command.LocalDate, localToday, policy);

        switch (timing)
        {
            case ReservationRejectionReason.LeadTimeTooShort:
                throw new LeadTimeTooShortException(
                    proposed.StartUtc, nowUtc.AddMinutes(policy.MinLeadMinutes), policy.MinLeadMinutes);

            case ReservationRejectionReason.OutsideBookingWindow:
                throw new OutsideBookingWindowException(
                    command.LocalDate, localToday.AddDays(policy.BookingWindowDays), policy.BookingWindowDays);
        }

        var localStart = command.LocalDate.ToDateTime(command.LocalTime);
        var localEnd = zone.ToLocal(proposed.EndUtc);

        if (ReservationRules.CheckOpeningHours(branch.OpeningHours, localStart, localEnd) is not null)
        {
            throw new OutsideOpeningHoursException(
                command.LocalDate, command.LocalTime, TimeOnly.FromDateTime(localEnd));
        }

        var tableReason = ReservationRules.CheckTable(
            table.Status, table.IsBookable, table.Seats, command.PartySize, policy);

        switch (tableReason)
        {
            case ReservationRejectionReason.TableNotBookable:
                throw new TableNotBookableException(table.Id, table.Label);

            case ReservationRejectionReason.TableOutOfService:
                throw new TableOutOfServiceException(table.Id, table.Label);

            case ReservationRejectionReason.PartyExceedsCapacity:
                throw new PartyExceedsTableCapacityException(
                    table.Id, table.Label, command.PartySize, table.Seats);

            case ReservationRejectionReason.SeatOverhangExceeded:
                throw new SeatOverhangExceededException(
                    table.Id, table.Label, command.PartySize, table.Seats, policy.MaxSeatOverhang!.Value);
        }
    }

    /// <summary>
    /// Confirmed, or waiting for a human - and which of the three reasons it was.
    /// </summary>
    private async Task<StatusDecision> DecideStatusAsync(
        CreateReservationCommand command,
        ReservationPolicy policy,
        Guid dinerUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!policy.AutoConfirm)
        {
            return new StatusDecision(
                ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking);
        }

        if (ReservationRules.NeedsApproval(command.PartySize, policy))
        {
            // Not a rejection. Twelve people is the most valuable booking of the night and the one
            // most likely to need tables moved, so a human looks at it.
            return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.LargeParty);
        }

        if (!noShowPolicy.Enabled)
        {
            return new StatusDecision(ReservationStatus.Confirmed, null);
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
            return new StatusDecision(ReservationStatus.Confirmed, null);
        }

        logger.LogInformation(
            "Diner {DinerUserId} has {NoShows} no-shows in the last {WindowDays} days; this booking needs approval.",
            dinerUserId, noShows, noShowPolicy.WindowDays);

        return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.NoShowHistory);
    }

    private async Task<ReservationView> DecideAsync(
        DecideReservationCommand command,
        bool approve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await LoadReservationAsync(command.ReservationId, cancellationToken);
        var operation = approve ? "Approve a booking" : "Reject a booking";

        await RequireManagerForBranchAsync(reservation.BranchId, operation, cancellationToken);

        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;

        if (approve)
        {
            reservation.Confirm(nowUtc);
        }
        else
        {
            reservation.Reject(nowUtc, command.Reason);
        }

        // The decision is worth telling the diner about: they are on a screen that says "waiting".
        outbox.Enqueue(
            OutboxMessageTypes.ReservationDecided,
            new
            {
                reservationId = reservation.Id,
                dinerUserId = reservation.DinerUserId,
                venueName = branch.Venue?.Name ?? branch.Name,
                branchName = branch.Name,
                tableLabel = string.Empty,
                localStartTime = reservation.LocalStartTime,
                reservationCode = reservation.Code,
                graceExtensionMinutes = 0,
                approved = approve,
            },
            clock.UtcNow,
            OutboxMessageTypes.KeyFor("reservation", reservation.Id, approve ? "approved" : "rejected"));

        if (!approve)
        {
            // A rejected booking is not going to happen, so its reminder and nudge must not fire.
            await outbox.CancelAsync(
                OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Booking {Code} was {Decision} by staff {StaffId}.",
            reservation.Code, approve ? "approved" : "rejected", actor.StaffMemberId);

        return await ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
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
        var policy = branch.ReservationPolicy;
        var nowUtc = clock.UtcNow;

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

            // The manage link, for a booking whose diner has no app to be pushed to.
            //
            // Written now, before any channel exists that could send it. The app reminder already
            // carries a one-tap cancel; a web booking's reminder has to carry a URL instead, and
            // the token is knowable only here - it is never stored in plaintext and never returned
            // again. A dispatcher added later that had to go and mint one would find it cannot.
            //
            // Only for Web. A capability that opens somebody's booking should be written into as
            // few places as possible, and an app booking has a better cancel route already.
            manageUrl = reservation.Channel == ReservationChannel.Web
                ? publicWeb.Value.ManageBookingUrlTemplate.Replace(
                    "{token}", Uri.EscapeDataString(manageToken), StringComparison.Ordinal)
                : null,
        };

        var remindAt = reservation.StartUtc.AddHours(-policy.ReminderHoursBefore);

        if (remindAt > nowUtc)
        {
            outbox.Enqueue(
                OutboxMessageTypes.ReservationReminder,
                notice,
                remindAt,
                OutboxMessageTypes.KeyFor("reservation", reservation.Id, "reminder"));
        }

        // The nudge is always in the future when the booking is made, because a booking cannot start
        // in the past. Written unconditionally for that reason.
        outbox.Enqueue(
            OutboxMessageTypes.ReservationLateNudge,
            notice,
            reservation.StartUtc.AddMinutes(policy.LateNudgeAfterMinutes),
            OutboxMessageTypes.KeyFor("reservation", reservation.Id, "late-nudge"));

        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- the diner extends their hold

    public async Task<ExtendHoldResult> ExtendHoldAsync(
        ExtendHoldCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Extend a hold");
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

        // Whatever is left unsent for this booking goes with it. A nudge asking "still coming?"
        // after a waiter has already given the table away is the worst of both.
        await outbox.CancelAsync(
            OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

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
    /// held until somebody senior walks past, which is how the feature ends up unused.
    /// </remarks>
    private async Task<Guid> RequireStaffForBranchAsync(
        Guid branchId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role is not (StaffRole.Waiter or StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
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

        if (staff.Role == StaffRole.PlatformAdmin || staff.BranchId == branchId)
        {
            return staffId;
        }

        // An owner or manager is venue-scoped rather than branch-scoped, which is the point of that
        // account. A waiter is confined to the branch they are enrolled at.
        if (staff.Role is StaffRole.Owner or StaffRole.Manager
            && staff.VenueId is { } venueId
            && await authorization.BranchBelongsToVenueAsync(branchId, venueId, cancellationToken))
        {
            return staffId;
        }

        throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
    }

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

    /// <summary>
    /// A manager or owner, scoped to this branch.
    /// </summary>
    /// <remarks>
    /// A staff member with a null <c>BranchId</c> works across every branch of their own venue - an
    /// owner does - but never across venues. Both halves are checked: a manager of one restaurant
    /// must not be able to approve bookings at another chain's branch by guessing an id.
    /// </remarks>
    private async Task RequireManagerForBranchAsync(
        Guid branchId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role is not (StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        // A platform admin belongs to no venue and may decide for any branch.
        if (staff.Role == StaffRole.PlatformAdmin)
        {
            return;
        }

        // The same read the BranchScoped policy does, through the same interface, rather than a
        // second copy of the query that could drift from it.
        if (staff.VenueId is not { } venueId
            || !await authorization.BranchBelongsToVenueAsync(branchId, venueId, cancellationToken))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        // A null BranchId means every branch of their own venue - an owner works everywhere. The
        // venue check above is what stops that meaning every branch in the system.
        if (staff.BranchId is { } assignedBranchId && assignedBranchId != branchId)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }
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
        var table = await db.DiningTables
            .AsNoTracking()
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
    /// Builds the view, fetching only the label the caller did not already have in hand.
    /// </summary>
    private async Task<ReservationView> ToViewAsync(
        Reservation reservation,
        Branch branch,
        DiningTable? table,
        bool wasReplay,
        ApprovalTrigger? trigger,
        CancellationToken cancellationToken,
        string? manageToken = null)
    {
        var tableLabel = table?.Label
                         ?? await db.DiningTables
                             .AsNoTracking()
                             .Where(t => t.Id == reservation.DiningTableId)
                             .Select(t => t.Label)
                             .FirstOrDefaultAsync(cancellationToken)
                         ?? string.Empty;

        return BuildView(
            reservation,
            branch.Name,
            branch.TimeZoneId,
            tableLabel,
            branch.ReservationPolicy.CancellationDeadlineMinutes,
            wasReplay,
            trigger,
            manageToken);
    }

    private static ReservationView ToView(MineRow row) =>
        BuildView(
            row.Reservation,
            row.BranchName,
            row.TimeZoneId,
            row.TableLabel,
            row.CancellationDeadlineMinutes,
            wasReplay: false,
            trigger: null);

    private static ReservationView BuildView(
        Reservation reservation,
        string branchName,
        string timeZoneId,
        string tableLabel,
        int cancellationDeadlineMinutes,
        bool wasReplay,
        ApprovalTrigger? trigger,
        string? manageToken = null)
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
            TableLabel = tableLabel,
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
            ConfirmedAtUtc = reservation.ConfirmedAtUtc,
            CancelledAtUtc = reservation.CancelledAtUtc,
            CancellationReason = reservation.CancellationReason,
            CancelledAfterDeadline = reservation.CancelledAfterDeadline,
            CancellationDeadlineUtc =
                ReservationRules.CancellationDeadline(reservation.StartUtc, cancellationDeadlineMinutes),
            ClientCommandId = reservation.ClientCommandId,
            WasReplay = wasReplay,
            AwaitingApprovalBecause =
                reservation.Status == ReservationStatus.PendingApproval ? trigger : null,

            // Null on every path but creation. The server holds only the hash, so a later read
            // could not return it even if one wanted to.
            ManageToken = manageToken,
        };
    }

    private sealed record StatusDecision(ReservationStatus Status, ApprovalTrigger? Trigger);

    private sealed class MineRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public string TableLabel { get; init; } = null!;

        /// <summary>The branch's setting as it stands, which a cancellation now is judged by.</summary>
        public int CancellationDeadlineMinutes { get; init; }
    }
}
