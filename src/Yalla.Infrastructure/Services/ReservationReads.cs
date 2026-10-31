using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The lists of bookings: the diner's own, a branch's by status for the console, and the floor's panel of
/// the ones awaiting approval.
/// </summary>
internal sealed class ReservationReads(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ReservationLookups lookups)
{
    public async Task<MyReservations> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var dinerUserId = lookups.RequireDiner("Read my bookings");
        var nowUtc = clock.UtcNow;

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DinerUserId == dinerUserId)
            .Select(r => new ReservationViews.MineRow
            {
                Reservation = r,
                PhoneUnverifiedNow = r.PhoneUnverified
                                     && !db.DinerUsers.Any(d => d.Id == r.DinerUserId && d.PhoneVerifiedAtUtc != null),
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                TableLabel = r.DiningTable!.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,
                TakenByName = db.StaffMembers.Where(s => s.Id == r.TakenByStaffId).Select(s => s.FullName).FirstOrDefault(),

                // A later guest asking for this booking's table, while the question is open and theirs
                // to answer - beside the booking, in the same statement.
                Incoming = db.TableShareRequests
                    .Where(q => q.EarlierReservationId == r.Id
                                && q.Status == TableShareRequestStatus.Pending
                                && q.AsksEarlierGuest
                                && q.ExpiresAtUtc > nowUtc)
                    .OrderBy(q => q.CreatedAtUtc)
                    .Select(q => new ReservationViews.IncomingRow
                    {
                        RequestId = q.Id,
                        RequestedStartUtc = q.RequestedStartUtc,
                        FreeByUtc = q.FreeByUtc,
                        ExpiresAtUtc = q.ExpiresAtUtc,
                    })
                    .FirstOrDefault(),

                // And the other side: this booking waiting on the guest before it to answer in the app.
                AwaitsEarlierGuestAnswer = db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                                          && q.Status == TableShareRequestStatus.Pending
                                                                          && q.AsksEarlierGuest),
            })
            .ToListAsync(cancellationToken);

        // Upcoming means "still going to happen": not finished, and not already called off. A
        // booking cancelled for tomorrow belongs in the history, not at the top of the screen.
        var upcoming = rows
            .Where(r => r.Reservation.EndUtc > nowUtc && ReservationOverlap.Blocks(r.Reservation.Status))
            .OrderBy(r => r.Reservation.StartUtc)
            .Select(r => ReservationViews.ToView(r))
            .ToList();

        var past = rows
            .Where(r => r.Reservation.EndUtc <= nowUtc || !ReservationOverlap.Blocks(r.Reservation.Status))
            .OrderByDescending(r => r.Reservation.StartUtc)
            .Select(r => ReservationViews.ToView(r))
            .ToList();

        // The diner's own record - what venues see beside their bookings - once, beside the lists.
        var record = await GuestRecords.ForAccountAsync(db, dinerUserId, nowUtc, cancellationToken);

        // Every booking's pre-order in one go, upcoming and past alike - and its thread with the venue in one line.
        var all = await PreOrderReads.AttachAsync(db, [.. upcoming, .. past], nowUtc, cancellationToken);
        all = await GuestThreads.AttachToBookingsAsync(db, all, nowUtc, cancellationToken);

        return new MyReservations([.. all.Take(upcoming.Count)], [.. all.Skip(upcoming.Count)], record);
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

        return await ListWhereAsync(branchId, r => r.BranchId == branchId && r.Status == status, cancellationToken);
    }

    public async Task<IReadOnlyList<ReservationView>> ListWithoutTableAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        // The floor's read, so the floor's guard: a waiter or above at this branch, who seats these.
        await lookups.RequireStaffForBranchAsync(branchId, "List the bookings with no table yet", cancellationToken);

        var nowUtc = clock.UtcNow;

        return await ListWhereAsync(
            branchId,
            r => r.BranchId == branchId
                 && r.BookedWithoutTable
                 && r.DiningTableId == null
                 && r.Status == ReservationStatus.Confirmed
                 && r.EndUtc > nowUtc,
            cancellationToken);
    }

    /// <summary>The branch's bookings matching <paramref name="where"/>, as the console's list builds them.</summary>
    private async Task<IReadOnlyList<ReservationView>> ListWhereAsync(
        Guid branchId,
        System.Linq.Expressions.Expression<Func<Reservation, bool>> where,
        CancellationToken cancellationToken)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.ReservationPolicy)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(where)
            .OrderBy(r => r.LocalDate)
            .ThenBy(r => r.LocalStartTime)
            .ThenBy(r => r.Id)
            .Take(BranchReservationList.MaxRows)
            .Select(r => new ReservationViews.MineRow
            {
                Reservation = r,
                PhoneUnverifiedNow = r.PhoneUnverified
                                     && !db.DinerUsers.Any(d => d.Id == r.DinerUserId && d.PhoneVerifiedAtUtc != null),
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                TableLabel = r.DiningTable!.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                CancellationDeadlineMinutes = r.Branch.ReservationPolicy.CancellationDeadlineMinutes,

                // Who took it by hand, for the console's and the floor's "Ընդունեց՝ {name}".
                TakenByName = db.StaffMembers.Where(s => s.Id == r.TakenByStaffId).Select(s => s.FullName).FirstOrDefault(),

                // Beside each row in the same statement, so the console can tell a booking only the guest
                // before can answer from one the floor decides - both count down two hours.
                AwaitsEarlierGuestAnswer = db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                                          && q.Status == TableShareRequestStatus.Pending
                                                                          && q.AsksEarlierGuest),
            })
            .ToListAsync(cancellationToken);

        // Every guest's record in one grouped statement, not one per row - for the bookings still to come or today's.
        var nowUtc = clock.UtcNow;
        var today = rows.Count == 0 ? default : BranchZone.For(rows[0].TimeZoneId).LocalDateAt(nowUtc);
        var records = await GuestRecords.ForAccountsAsync(
            db, ReservationViews.RecordedAccounts(rows.Select(r => (r.Reservation, r.PhoneUnverifiedNow ?? false)), today), nowUtc, cancellationToken);

        var views = rows
            .Select(r => ReservationViews.BuildView(
                r.Reservation,
                r.BranchName,
                r.TimeZoneId,
                new ReservationViews.TableFacts(r.TableLabel, r.RoomId, r.RoomName),
                policy.CancellationDeadlineMinutes,
                wasReplay: false,
                trigger: ReservationViews.PendingTriggerFor(r.Reservation, policy),
                awaitsEarlierGuestAnswer: r.AwaitsEarlierGuestAnswer,
                phoneUnverifiedNow: r.PhoneUnverifiedNow,
                takenByName: r.TakenByName) with
            {
                GuestRecord = ReservationViews.RecordOf(records, r.Reservation, r.PhoneUnverifiedNow ?? false, today),
            })
            .ToList();

        // Each booking's pre-order, for the whole page at once.
        return await PreOrderReads.AttachAsync(db, views, nowUtc, cancellationToken, forGuest: false);
    }

    public async Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        // The floor's read, so the floor's guard: a waiter or above at this branch, as for deciding.
        await lookups.RequireStaffForBranchAsync(branchId, "List the bookings awaiting approval", cancellationToken);

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
                PhoneUnverifiedNow = r.PhoneUnverified
                                     && !db.DinerUsers.Any(d => d.Id == r.DinerUserId && d.PhoneVerifiedAtUtc != null),
                BranchName = r.Branch.Name,
                TimeZoneId = r.Branch.TimeZoneId,
                Policy = r.Branch.ReservationPolicy,
                TableLabel = r.DiningTable!.Label,
                RoomId = r.DiningTable.FloorAreaId,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,

                // The guest this booking is asking to free the table, when it is asking one - whatever day
                // their booking is on (a 23:00 before a 00:30 is yesterday's).
                Asked = db.Reservations
                    .Where(p => db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                               && q.EarlierReservationId == p.Id
                                                               && q.Status == TableShareRequestStatus.Pending))
                    .Select(p => new AwaitingPreviousBooking(
                        p.Id, p.StartUtc, p.LocalStartTime, p.PartySize, p.GuestName, p.GuestPhone, p.Status,
                        p.PromisedFreeByUtc))
                    .FirstOrDefault(),

                // Otherwise whoever had the table before, that day: still to come, at it now, gone, or
                // never turned up. A cancelled booking was never going to be at the table, so it is skipped.
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

        // Every guest's record in one grouped statement, not one per card.
        var records = await GuestRecords.ForAccountsAsync(
            db,
            rows.Where(r => ReservationViews.ShowsRecord(r.Reservation, r.PhoneUnverifiedNow, BranchZone.For(r.TimeZoneId).LocalDateAt(nowUtc)))
                .Select(r => r.Reservation.DinerUserId!.Value),
            nowUtc,
            cancellationToken);

        var items = rows
            .Select(row =>
            {
                var previous = row.Asked ?? row.Previous;

                var view = ReservationViews.BuildView(
                    row.Reservation,
                    row.BranchName,
                    row.TimeZoneId,
                    new ReservationViews.TableFacts(row.TableLabel, row.RoomId, row.RoomName),
                    row.Policy.CancellationDeadlineMinutes,
                    wasReplay: false,
                    trigger: ReservationViews.PendingTriggerFor(row.Reservation, row.Policy),

                    // False for every card: the list leaves out the bookings waiting on the guest
                    // before them in the app - those are not the floor's to answer.
                    awaitsEarlierGuestAnswer: false,
                    phoneUnverifiedNow: row.PhoneUnverifiedNow) with
                {
                    GuestRecord = ReservationViews.RecordOf(
                        records, row.Reservation, row.PhoneUnverifiedNow, BranchZone.For(row.TimeZoneId).LocalDateAt(nowUtc)),
                };

                return new AwaitingApproval
                {
                    Reservation = withPhones ? view : view with { GuestPhone = string.Empty },
                    ApprovalExpiresAtUtc = view.ApprovalExpiresAtUtc,

                    // Never null here: the booking is pending, and a pending booking always has a reason.
                    Trigger = view.AwaitingApprovalBecause ?? ApprovalTrigger.NoShowHistory,
                    PreviousBooking = withPhones || previous is null
                        ? previous
                        : previous with { GuestPhone = null },
                    NextBooking = row.Next,
                    CurrentSitting = row.Sitting,
                    EarlierGuestFreeByUtc = row.EarlierGuestFreeByUtc,
                };
            })
            .ToList();

        // Each request's pre-order, for the whole panel at once.
        var withPreOrders = await PreOrderReads.AttachAsync(
            db, [.. items.Select(i => i.Reservation)], nowUtc, cancellationToken, forGuest: false);

        return [.. items.Select((item, i) => item with { Reservation = withPreOrders[i] })];
    }

    /// <summary>One booking awaiting approval, with its table's evening, as the single query reads it.</summary>
    private sealed class AwaitingRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        /// <summary>The owned policy, for the deadline and for the reason of a row from before the column.</summary>
        public ReservationPolicy Policy { get; init; } = null!;

        public string? TableLabel { get; init; }

        public Guid? RoomId { get; init; }

        public string? RoomName { get; init; }

        public AwaitingPreviousBooking? Asked { get; init; }

        public AwaitingPreviousBooking? Previous { get; init; }

        public AwaitingNextBooking? Next { get; init; }

        public AwaitingCurrentSitting? Sitting { get; init; }

        public DateTime? EarlierGuestFreeByUtc { get; init; }

        /// <summary>Made without proof, and not proved since by the booking's account.</summary>
        public bool PhoneUnverifiedNow { get; init; }
    }
}
