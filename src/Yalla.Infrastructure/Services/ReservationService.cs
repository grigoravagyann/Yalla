using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
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
/// </remarks>
internal sealed class ReservationService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAvailabilityQuery availabilityQuery,
    IAuthorizationQueries authorization,
    NoShowPolicy noShowPolicy,
    BookingLockOptions lockOptions,
    ILogger<ReservationService> logger) : IReservationService
{
    /// <summary>SQL Server: "Lock request time out period exceeded."</summary>
    private const int LockTimeoutErrorNumber = 1222;

    /// <summary>
    /// How many times to re-roll a colliding reservation code before giving up.
    /// </summary>
    /// <remarks>
    /// One in ~887 million per attempt, so three is already superstition. It exists because the
    /// unique index - not the generator - is what guarantees the code is unique, and a system that
    /// relies on an index must have an answer for the index firing.
    /// </remarks>
    private const int CodeAttempts = 3;

    public async Task<ReservationView> CreateAsync(
        CreateReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Book a table");

        // Cheap path first: a retry that arrives after the original committed never reaches the
        // lock at all. The unique index below is what makes the racing case safe.
        if (await FindReplayAsync(command.ClientCommandId, dinerUserId, cancellationToken) is { } replay)
        {
            logger.LogInformation(
                "Booking command {ClientCommandId} was already applied as {Code}; returning the original.",
                command.ClientCommandId, replay.Code);

            var replayBranch = await LoadBranchAsync(replay.BranchId, cancellationToken);

            return await ToViewAsync(
                replay, replayBranch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        var branch = await LoadBranchAsync(command.BranchId, cancellationToken);
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
            clientCommandId: command.ClientCommandId);

        var outcome = await InsertUnderTableLockAsync(
            reservation, table, proposed, policy, dinerUserId, cancellationToken);

        if (outcome.Replay is { } winner)
        {
            return await ToViewAsync(winner, branch, table, wasReplay: true, trigger: null, cancellationToken);
        }

        if (outcome.Conflict is { } conflict)
        {
            // Outside the lock, deliberately. The floor snapshot is a wide read and holding a
            // table's lock while assembling it would queue every other booker behind a response
            // body.
            throw await ConflictAsync(command, table, proposed, conflict, cancellationToken);
        }

        logger.LogInformation(
            "Booked table {TableLabel} at branch {BranchId} for {PartySize} as {Code} ({Status}).",
            table.Label, branch.Id, command.PartySize, reservation.Code, reservation.Status);

        return await ToViewAsync(reservation, branch, table, wasReplay: false, status.Trigger, cancellationToken);
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

        var branch = await LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var late = ReservationRules.IsLateCancellation(reservation.StartUtc, nowUtc, branch.ReservationPolicy);

        // Late is recorded, never refused. A diner who cannot cancel simply does not turn up, and
        // a no-show costs the venue the same table plus the chance to resell it.
        reservation.CancelByDiner(nowUtc, command.Reason, late);

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

    // ---------------------------------------------------------------- the lock

    /// <summary>
    /// The whole concurrency-critical section: lock the table, re-check, insert, commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-check happens <b>inside</b> the lock and never before it. A check taken before the
    /// lock proves only that the table was free at some earlier moment, which is precisely the
    /// window the lock exists to close.
    /// </para>
    /// <para>
    /// <c>SET LOCK_TIMEOUT</c> bounds the wait so one stuck transaction cannot hang every booking
    /// for that table. Hitting it is reported as a retryable failure, distinct from losing the
    /// slot - the caller should try again, whereas a real conflict will never succeed.
    /// </para>
    /// </remarks>
    private async Task<InsertOutcome> InsertUnderTableLockAsync(
        Reservation reservation,
        DiningTable table,
        BookedInterval proposed,
        ReservationPolicy policy,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp(lockOptions.LockTimeoutMilliseconds, 100, 60_000);

        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            // SET LOCK_TIMEOUT takes a literal and will not accept a parameter, so this is the one
            // place the value is formatted into the statement. It is an int, clamped just above,
            // and never touches user input - there is nothing here to inject.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection.
            await db.Database.ExecuteSqlRawAsync($"SET LOCK_TIMEOUT {timeoutMs};", cancellationToken);
#pragma warning restore EF1002

            // UPDLOCK serialises bookers against each other without blocking readers; HOLDLOCK
            // keeps it to the end of the transaction rather than releasing it the instant the
            // statement finishes, which is what makes the re-check below meaningful.
            await db.Database.ExecuteSqlRawAsync(
                "SELECT Id FROM DiningTables WITH (UPDLOCK, HOLDLOCK) WHERE Id = @tableId",
                [new SqlParameter("@tableId", table.Id)],
                cancellationToken);

            // The command id is re-checked here, and before the overlap check, for a reason that
            // is easy to get backwards. Two retries of the SAME booking race: the first commits
            // while the second waits on this lock. By the time the second gets here the first is
            // visible, and an overlap check run first would call the diner's own booking a
            // conflict and answer 409 for a table they already have.
            if (await FindReplayAsync(
                    reservation.ClientCommandId, dinerUserId, cancellationToken) is { } original)
            {
                db.Entry(reservation).State = EntityState.Detached;
                return InsertOutcome.Replayed(original);
            }

            if (await FindConflictAsync(table.Id, proposed, policy, cancellationToken) is { } conflict)
            {
                // Nothing was written. Leave the transaction and build the answer outside it.
                db.Entry(reservation).State = EntityState.Detached;
                return InsertOutcome.Conflicted(conflict);
            }

            db.Reservations.Add(reservation);

            if (await SaveWithFreshCodeAsync(reservation, dinerUserId, cancellationToken) is { } winner)
            {
                return InsertOutcome.Replayed(winner);
            }

            await transaction.CommitAsync(cancellationToken);

            return InsertOutcome.Inserted;
        }
        catch (SqlException ex) when (ex.Number == LockTimeoutErrorNumber)
        {
            db.Entry(reservation).State = EntityState.Detached;

            logger.LogWarning(
                "Timed out after {TimeoutMs}ms waiting for the booking lock on table {TableLabel}.",
                timeoutMs, table.Label);

            throw new ReservationLockTimeoutException(table.Id, table.Label, timeoutMs);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: LockTimeoutErrorNumber })
        {
            db.Entry(reservation).State = EntityState.Detached;

            throw new ReservationLockTimeoutException(table.Id, table.Label, timeoutMs);
        }
        finally
        {
            await ResetLockTimeoutAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The overlap re-check, as one indexed range read.
    /// </summary>
    /// <remarks>
    /// The range predicate is the overlap rule rearranged so SQL Server can seek it - see
    /// <see cref="ReservationOverlap.SearchWindow"/> - and every row it returns is then put through
    /// <see cref="ReservationOverlap"/> itself. The rule proved at its boundaries by the unit tests
    /// is therefore the rule that decides the insert, and the SQL is only an index-friendly way of
    /// narrowing the candidates.
    /// </remarks>
    private async Task<Reservation?> FindConflictAsync(
        Guid tableId,
        BookedInterval proposed,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        var (searchFromUtc, searchToUtc) = ReservationOverlap.SearchWindow(proposed, policy.BufferMinutes);

        var candidates = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval
                            || r.Status == ReservationStatus.Seated)
                        && r.EndUtc > searchFromUtc
                        && r.StartUtc < searchToUtc)
            .OrderBy(r => r.StartUtc)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(r => ReservationOverlap.Conflicts(
            proposed, BookedInterval.Of(r), r.Status, policy.BufferMinutes));
    }

    /// <summary>
    /// Saves the booking, re-rolling the door code if it collides, and answering a lost
    /// idempotency race with the booking that won.
    /// </summary>
    /// <returns>The winning booking when this was a racing replay, otherwise null.</returns>
    private async Task<Reservation?> SaveWithFreshCodeAsync(
        Reservation reservation,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            catch (DbUpdateException ex)
                when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ReservationClientCommand))
            {
                // Two retries of the same offline command raced each other. The other one did the
                // work; this one answers from its row. The index is what makes this safe - a
                // check-then-insert would have let both through.
                db.Entry(reservation).State = EntityState.Detached;

                // Scoped to the caller. The index is unique across every diner, so the row that
                // won may not be theirs at all - and handing it back would answer a stranger with
                // somebody else's door code, guest name and telephone number.
                var winner = await FindReplayAsync(
                    reservation.ClientCommandId, dinerUserId, cancellationToken);

                if (winner is null)
                {
                    logger.LogWarning(
                        "Booking command {ClientCommandId} is already held by another diner's booking.",
                        reservation.ClientCommandId);

                    throw new ClientCommandIdAlreadyUsedException(reservation.ClientCommandId);
                }

                logger.LogInformation(
                    "Booking command {ClientCommandId} was applied concurrently; replaying {Code}.",
                    reservation.ClientCommandId, winner.Code);

                return winner;
            }
            catch (DbUpdateException ex)
                when (attempt < CodeAttempts && UniqueViolation.IsOn(ex, DatabaseIndexNames.ReservationCode))
            {
                logger.LogWarning(
                    "Reservation code {Code} was already taken; generating another.", reservation.Code);

                reservation.ReplaceCode(ReservationCode.Generate());
            }
        }
    }

    /// <summary>
    /// Puts the connection's lock timeout back.
    /// </summary>
    /// <remarks>
    /// <c>SET LOCK_TIMEOUT</c> is a property of the connection, and connections are pooled. The
    /// pool's reset would clear it eventually, but the same context goes on to serve the rest of
    /// this request first - and a stray five-second timeout on an unrelated query is the kind of
    /// bug that only appears under load.
    /// </remarks>
    private async Task ResetLockTimeoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT -1;", cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // The connection is already gone or the transaction is doomed. Nothing to restore,
            // and throwing here would replace the real failure with a meaningless one.
            logger.LogDebug(ex, "Could not restore the connection's lock timeout.");
        }
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

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Booking {Code} was {Decision} by staff {StaffId}.",
            reservation.Code, approve ? "approved" : "rejected", actor.StaffMemberId);

        return await ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
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
            || actor.Role is not (StaffRole.Manager or StaffRole.Owner))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        // The same read the BranchScoped policy does, through the same interface, rather than a
        // second copy of the query that could drift from it.
        if (staff is not { IsActive: true }
            || !await authorization.BranchBelongsToVenueAsync(branchId, staff.VenueId, cancellationToken))
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

    /// <summary>
    /// The booking a previous attempt with this command id already made, <b>by this diner</b>.
    /// </summary>
    /// <remarks>
    /// The diner is half the question, not a refinement of it. A replay is the same caller sending
    /// the same command again; the same id from a different caller is a collision, and answering it
    /// with the booking that holds the id would disclose that booking's door code, guest name and
    /// phone number to somebody who only had to reuse a Guid.
    /// </remarks>
    private Task<Reservation?> FindReplayAsync(
        Guid clientCommandId,
        Guid dinerUserId,
        CancellationToken cancellationToken) =>
        db.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ClientCommandId == clientCommandId && r.DinerUserId == dinerUserId,
                cancellationToken);

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
        CancellationToken cancellationToken)
    {
        var tableLabel = table?.Label
                         ?? await db.DiningTables
                             .AsNoTracking()
                             .Where(t => t.Id == reservation.DiningTableId)
                             .Select(t => t.Label)
                             .FirstOrDefaultAsync(cancellationToken)
                         ?? string.Empty;

        return BuildView(reservation, branch.Name, branch.TimeZoneId, tableLabel, wasReplay, trigger);
    }

    private static ReservationView ToView(MineRow row) =>
        BuildView(row.Reservation, row.BranchName, row.TimeZoneId, row.TableLabel, wasReplay: false, trigger: null);

    private static ReservationView BuildView(
        Reservation reservation,
        string branchName,
        string timeZoneId,
        string tableLabel,
        bool wasReplay,
        ApprovalTrigger? trigger)
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
            ClientCommandId = reservation.ClientCommandId,
            WasReplay = wasReplay,
            AwaitingApprovalBecause =
                reservation.Status == ReservationStatus.PendingApproval ? trigger : null,
        };
    }

    private sealed record StatusDecision(ReservationStatus Status, ApprovalTrigger? Trigger);

    private sealed class MineRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public string TableLabel { get; init; } = null!;
    }

    /// <summary>What came out of the locked section.</summary>
    private readonly record struct InsertOutcome(Reservation? Conflict, Reservation? Replay)
    {
        public static InsertOutcome Inserted => new(null, null);

        public static InsertOutcome Conflicted(Reservation conflict) => new(conflict, null);

        public static InsertOutcome Replayed(Reservation winner) => new(null, winner);
    }
}
