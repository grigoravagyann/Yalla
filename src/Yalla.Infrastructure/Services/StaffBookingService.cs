using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Bookings a member of staff takes for a guest by hand (2026-10-31, "Ձեռքով ամրագրում"), and the follow-up that is
/// theirs: the sheet's day, the booking, the guests to call, the guest's "coming" recorded over the phone. See
/// <c>docs/reservations.md</c>, "Staff-taken bookings".
/// </summary>
/// <remarks>
/// <para>
/// <b>A sibling of <see cref="ReservationCreation"/>, not a flag on it.</b> A diner's booking is judged by the diner's
/// account - its proof, its block, its other bookings, its number's limit, its no-show history - and by what the branch
/// takes from the diner's channel. None of that is true of a booking the floor takes: there is no account, the number
/// is the venue's to call, and the person on the floor has judged the room. So the staff path keeps the rules about the
/// slot - the venue open for business, the booking window, the opening hours, the table's seats and overhang, the table's
/// evening, the room's guests at once - and skips every rule about the booker. Kept apart, so a rule added to one path is
/// a decision about the other rather than an accident.
/// </para>
/// <para>
/// No approval and no question to the guest before: the booking is <c>Confirmed</c> at once, and a table the diner's
/// read could only ask for is booked outright, with the time to leave by that the next booking needs taken without
/// asking. No diner reminders (<c>ReservationNotice.DinerUserId</c> would have nobody) and no manage link (nobody to
/// give it to). What follows is the taker's: the <c>bookingToCall</c> push thirty minutes before, the to-call list, and
/// the booking in their results - hung on the booking's events and its <c>TakenByStaffId</c>.
/// </para>
/// </remarks>
internal sealed class StaffBookingService : IStaffBookingService
{
    /// <summary>What a staff-recorded "coming" is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string GuestComingCommandType = "reservation.guest-coming";

    private readonly YallaDbContext db;
    private readonly IClock clock;
    private readonly ICurrentActor actor;
    private readonly IAvailabilityQuery availability;
    private readonly ReservationWriter reservations;
    private readonly ILogger<StaffBookingService> logger;
    private readonly ReservationLookups lookups;
    private readonly ReservationViews views;
    private readonly ReservationSlotRules slots;

    public StaffBookingService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        IAuthorizationQueries authorization,
        IAvailabilityQuery availability,
        NoShowPolicy noShowPolicy,
        ReservationWriter reservations,
        ILogger<StaffBookingService> logger,
        ILogger<ReservationService> bookingLogger)
    {
        this.db = db;
        this.clock = clock;
        this.actor = actor;
        this.availability = availability;
        this.reservations = reservations;
        this.logger = logger;

        lookups = new ReservationLookups(db, clock, actor, authorization);
        views = new ReservationViews(db, clock, actor, lookups);
        slots = new ReservationSlotRules(db, availability, noShowPolicy, bookingLogger);
    }

    // ---------------------------------------------------------------- booking

    public async Task<ReservationView> CreateAsync(
        StaffCreateReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = await lookups.RequireStaffForBranchAsync(command.BranchId, "Book a table for a guest", cancellationToken);

        // The note is part of the body, so it is refused before anything is looked up, as on a diner's booking.
        var note = Reservation.NormaliseNote(command.Note);

        // Cheap path first: a retry that arrives after the original committed never reaches the lock.
        if (await reservations.FindStaffReplayAsync(command.ClientCommandId, command.BranchId, cancellationToken) is { } replay)
        {
            logger.LogInformation(
                "Staff booking command {ClientCommandId} was already applied as {Code}; returning the original.",
                command.ClientCommandId, replay.Code);

            var replayBranch = await lookups.LoadBranchAsync(replay.BranchId, cancellationToken);

            return await views.ToViewAsync(replay, replayBranch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        var branch = await lookups.LoadBranchAsync(command.BranchId, cancellationToken);

        // The one gate about the venue that holds for the floor too: a venue that stopped paying, or was deleted, takes
        // no new bookings from anybody. The channel gates and the visibility gate are about diners, and are not asked.
        VenueGate.RequireOpenForBusiness(branch);

        var policy = branch.ReservationPolicy;
        var zone = BranchZone.For(branch.TimeZoneId);
        var nowUtc = clock.UtcNow;

        // Throws LocalTimeDoesNotExistException for an hour a spring-forward skips, as a diner's booking does.
        var startUtc = zone.ToUtc(command.LocalDate, command.LocalTime);
        var proposed = new BookedInterval(startUtc, startUtc.AddMinutes(policy.TurnTimeMinutes));
        var hours = await lookups.HoursAroundAsync(branch.Id, command.LocalDate, cancellationToken);

        ValidateStaffTime(command.LocalDate, command.LocalTime, hours, policy, zone, proposed, nowUtc);

        if (policy.BookingMode == BookingMode.TimeOnly)
        {
            if (command.TableId is not null)
            {
                throw new FieldValidationException(
                [
                    new FieldViolation("tableId", "This branch books by time only: leave the table out; the waiter chooses it when the party arrives."),
                ]);
            }

            return await CreateWithoutTableAsync(command, note, staffId, branch, proposed, nowUtc, cancellationToken);
        }

        if (command.TableId is not { } tableId)
        {
            throw new FieldValidationException(
            [
                new FieldViolation("tableId", "Pick a table: this branch books a table.", FieldBounds.Required),
            ]);
        }

        var table = await lookups.LoadTableAsync(command.BranchId, tableId, cancellationToken);

        // Any in-service table - pinned or not, bookable from the app or not - that seats the party.
        switch (ReservationRules.CheckStaffTable(table.Status, table.Seats, command.PartySize, policy))
        {
            case ReservationRejectionReason.TableOutOfService:
                throw new TableOutOfServiceException(table.Id, table.Label);

            case ReservationRejectionReason.PartyExceedsCapacity:
                throw new PartyExceedsTableCapacityException(table.Id, table.Label, command.PartySize, table.Seats);

            case ReservationRejectionReason.SeatOverhangExceeded:
                throw new SeatOverhangExceededException(
                    table.Id, table.Label, command.PartySize, table.Seats, policy.MaxSeatOverhang!.Value);
        }

        InsertOutcome outcome;

        try
        {
            outcome = await reservations.InsertByStaffUnderTableLockAsync(
                command.ClientCommandId,
                branch.Id,
                table,
                startUtc,
                policy,
                (verdict, _) =>
                {
                    // Confirmed on the spot, linked to the taker; the time to leave by, when the next booking needs
                    // one, taken without asking. Made: the History's line (the taker's), the table's change stream and
                    // the taker's "call the guest" push are the event's handlers', in the insert's save.
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
                        initialStatus: ReservationStatus.Confirmed,
                        dinerUserId: null,
                        clientCommandId: command.ClientCommandId,
                        channel: ReservationChannel.Staff,
                        note: note,
                        promisedFreeByUtc: verdict.MustFreeByUtc,
                        takenByStaffId: staffId);

                    booking.Book(nowUtc);

                    return Task.FromResult(booking);
                },
                cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }

        if (outcome.Replay is { } winner)
        {
            return await views.ToViewAsync(winner, branch, table, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.Occupied is { } sitting)
        {
            throw await slots.OccupiedAsync(SnapshotFor(command), table, proposed, sitting, policy, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            throw await slots.ConflictAsync(SnapshotFor(command), table, proposed, conflict, cancellationToken);
        }

        var reservation = outcome.Booking!;

        logger.LogInformation(
            "Staff member {StaffId} booked table {TableLabel} at branch {BranchId} for {PartySize} as {Code}.",
            staffId, table.Label, branch.Id, command.PartySize, reservation.Code);

        return await views.ToViewAsync(reservation, branch, table, wasReplay: false, trigger: null, cancellationToken);
    }

    /// <summary>A staff booking at a branch that books by time only: the room's guests at once, under the branch's lock, no table.</summary>
    private async Task<ReservationView> CreateWithoutTableAsync(
        StaffCreateReservationCommand command,
        string? note,
        Guid staffId,
        Branch branch,
        BookedInterval proposed,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var policy = branch.ReservationPolicy;
        var capacity = policy.GuestsAtOnce!.Value;

        if (command.PartySize > capacity)
        {
            throw new TimeFullException(branch.Id, command.PartySize, capacity);
        }

        InsertWithoutTableOutcome outcome;

        try
        {
            outcome = await reservations.InsertByStaffWithoutTableAsync(
                command.ClientCommandId,
                branch.Id,
                proposed,
                command.PartySize,
                capacity,
                _ =>
                {
                    var booking = Reservation.Create(
                        branchId: branch.Id,
                        diningTableId: null,
                        startUtc: proposed.StartUtc,
                        localDate: command.LocalDate,
                        localStartTime: command.LocalTime,
                        partySize: command.PartySize,
                        guestName: command.GuestName,
                        guestPhone: command.GuestPhone,
                        code: ReservationCode.Generate(),
                        policy: policy,
                        initialStatus: ReservationStatus.Confirmed,
                        dinerUserId: null,
                        clientCommandId: command.ClientCommandId,
                        channel: ReservationChannel.Staff,
                        note: note,
                        takenByStaffId: staffId);

                    booking.Book(nowUtc);

                    return Task.FromResult(booking);
                },
                cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }

        if (outcome.Replay is { } winner)
        {
            return await views.ToViewAsync(winner, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.GuestsLeft is { } left)
        {
            throw new TimeFullException(branch.Id, command.PartySize, left);
        }

        var reservation = outcome.Booking!;

        logger.LogInformation(
            "Staff member {StaffId} booked by time only at branch {BranchId} for {PartySize} as {Code}.",
            staffId, branch.Id, command.PartySize, reservation.Code);

        return await views.ToViewAsync(reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
    }

    /// <summary>
    /// The slot's rules for a staff booking: a start already past, the booking window and the opening hours - no lead
    /// time, which is a rule about diners.
    /// </summary>
    private static void ValidateStaffTime(
        DateOnly localDate,
        TimeOnly localTime,
        OpeningCalendar hours,
        ReservationPolicy policy,
        BranchZone zone,
        BookedInterval proposed,
        DateTime nowUtc)
    {
        var localToday = zone.LocalDateAt(nowUtc);

        switch (ReservationRules.CheckStaffTiming(proposed.StartUtc, nowUtc, localDate, localToday, policy))
        {
            case ReservationRejectionReason.LeadTimeTooShort:
                // Already past: the lead-time refusal with no minutes to it, so the client's one code covers both.
                throw new LeadTimeTooShortException(proposed.StartUtc, nowUtc, minLeadMinutes: 0);

            case ReservationRejectionReason.OutsideBookingWindow:
                throw new OutsideBookingWindowException(
                    localDate, localToday.AddDays(policy.BookingWindowDays), policy.BookingWindowDays);
        }

        var localStart = localDate.ToDateTime(localTime);
        var localEnd = zone.ToLocal(proposed.EndUtc);

        if (ReservationRules.CheckOpeningHours(hours, localStart, localEnd) is not null)
        {
            throw new OutsideOpeningHoursException(localDate, localTime, TimeOnly.FromDateTime(localEnd));
        }
    }

    /// <summary>The floor a refused booking is answered with: the slot it asked about.</summary>
    private static AvailabilityRequest SnapshotFor(StaffCreateReservationCommand command) =>
        new(command.BranchId, command.PartySize, command.LocalDate, command.LocalTime);

    // ---------------------------------------------------------------- the sheet's day

    public async Task<StaffDaySlots> GetDaySlotsAsync(StaffDaySlotsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await lookups.RequireStaffForBranchAsync(request.BranchId, "Read the day's times for a staff booking", cancellationToken);

        return await availability.GetStaffDaySlotsAsync(request, cancellationToken)
               ?? throw new KeyNotFoundException($"Branch {request.BranchId} was not found.");
    }

    // ---------------------------------------------------------------- the guests to call

    public async Task<IReadOnlyList<BookingToCall>> ListToCallAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var staffId = await lookups.RequireStaffForBranchAsync(branchId, "List the guests to call", cancellationToken);

        var graceMinutes = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => (int?)b.ReservationPolicy.GraceMinutes)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;

        // From thirty minutes before the start (when the taker is pushed) until the start plus the grace, or the
        // extended hold when that is later - while the guest has not said they are coming and the booking stands.
        var callFromUtc = nowUtc.AddMinutes(BookingToCallRules.MinutesBefore);
        var graceCutoffUtc = nowUtc.AddMinutes(-graceMinutes);

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && r.Channel == ReservationChannel.Staff
                        && r.TakenByStaffId != null
                        && r.Status == ReservationStatus.Confirmed
                        && r.GuestConfirmedOnWayAtUtc == null
                        && r.StartUtc <= callFromUtc
                        && (r.StartUtc > graceCutoffUtc || (r.HoldExpiresAtUtc != null && r.HoldExpiresAtUtc > nowUtc)))
            .OrderBy(r => r.StartUtc)
            .ThenBy(r => r.Id)
            .Take(BranchReservationList.MaxRows)
            .Select(r => new
            {
                r.Id,
                r.Code,
                r.GuestName,
                r.GuestPhone,
                r.PartySize,
                r.StartUtc,
                r.LocalDate,
                r.LocalStartTime,
                r.DiningTableId,
                TableLabel = r.DiningTable == null ? null : r.DiningTable.Label,
                r.Note,
                TakenBy = r.TakenByStaffId!.Value,
                TakenByName = db.StaffMembers.Where(s => s.Id == r.TakenByStaffId).Select(s => s.FullName).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // The guests' numbers are the venue's to call, not the platform's to read.
        var withPhones = actor.Role != StaffRole.PlatformAdmin;

        return
        [
            .. rows
                .Select(r => new BookingToCall(
                    r.Id,
                    r.Code,
                    r.GuestName,
                    withPhones ? r.GuestPhone : string.Empty,
                    r.PartySize,
                    r.StartUtc,
                    r.LocalDate,
                    r.LocalStartTime,
                    r.DiningTableId,
                    r.TableLabel,
                    r.Note,
                    r.TakenBy,
                    r.TakenByName ?? string.Empty,
                    r.TakenBy == staffId))
                .OrderByDescending(c => c.IsMine)
                .ThenBy(c => c.StartUtc),
        ];
    }

    // ---------------------------------------------------------------- the guest is coming

    public async Task<ReservationView> MarkGuestComingAsync(MarkGuestComingCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);
        var staffId = await lookups.RequireStaffForBranchAsync(reservation.BranchId, "Record that the guest is coming", cancellationToken);

        // A retry of this answer: the booking as it stands now. The same id on anything else is refused.
        if (await IsComingReplayAsync(command, cancellationToken))
        {
            return await views.ReplayViewAsync(reservation.Id, cancellationToken);
        }

        var nowUtc = clock.UtcNow;
        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);

        // Said already - by the guest or by a colleague: the first answer stands, and this is answered with it.
        if (!reservation.ConfirmOnMyWay(nowUtc, staffId))
        {
            return await views.ToViewAsync(reservation, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        db.ProcessedCommands.Add(new ProcessedCommand(
            command.ClientCommandId,
            GuestComingCommandType,
            ActorType.Staff,
            staffId,
            JsonSerializer.Serialize(new ComingRecord(reservation.Id), ReservationChanges.RecordJson),
            200,
            nowUtc));

        try
        {
            // The History's line (the staff member's), the automatic question withdrawn, signal 5 and the rail's signal
            // 6 are the event's handlers', in this save.
            await reservations.SaveAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two tablets at once, or the guest answered in the app the same moment. The first answer stands.
            db.ChangeTracker.Clear();
            var current = await lookups.LoadReservationAsync(reservation.Id, cancellationToken);

            if (current.GuestConfirmedOnWayAtUtc is null)
            {
                throw;
            }

            return await views.ToViewAsync(current, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            // The same id was recorded by a racing retry: its answer is this one's.
            db.ChangeTracker.Clear();

            return await views.ReplayViewAsync(reservation.Id, cancellationToken);
        }

        logger.LogInformation(
            "Booking {Code}: staff member {StaffId} recorded that the guest is coming.", reservation.Code, staffId);

        return await views.ToViewAsync(reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
    }

    /// <summary>
    /// Whether this answer was already recorded under this id - or throws when the id belongs to another command or
    /// another booking.
    /// </summary>
    private async Task<bool> IsComingReplayAsync(MarkGuestComingCommand command, CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .Where(c => c.ClientCommandId == command.ClientCommandId)
            .Select(c => new { c.CommandType, c.ResponseJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (processed is null)
        {
            return false;
        }

        if (processed.CommandType != GuestComingCommandType
            || JsonSerializer.Deserialize<ComingRecord>(processed.ResponseJson, ReservationChanges.RecordJson) is not { } record
            || record.ReservationId != command.ReservationId)
        {
            throw new ClientCommandIdAlreadyUsedException(
                command.ClientCommandId,
                "That clientCommandId already belongs to another command. Generate a fresh one per answer, "
                + "and reuse it only when retrying that same answer.");
        }

        return true;
    }

    /// <summary>What a staff-recorded "coming" records: which booking.</summary>
    private sealed record ComingRecord(Guid ReservationId);
}
