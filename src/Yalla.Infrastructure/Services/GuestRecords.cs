using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reads guest records (<see cref="GuestRecord"/>) and booking blocks (<see cref="BookingBlock"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement for a whole list</b>, whatever its length: for every account on the page, its counts and its
/// block in force, each a correlated seek on <c>IX_Reservations_DinerUserId_StartUtc</c> /
/// <c>IX_BookingBlocks_DinerUserId_BlockedUntilUtc</c>. Never a query per booking. A maintained aggregate was considered and rejected: every
/// status change (seat, complete, no-show, cancel, the sweeps) would have to write it, and the twelve-month window
/// slides every day; at this scale the indexed read is cheap and cannot drift.
/// </para>
/// <para>
/// Counted only from bookings the account made with a proved number (<c>PhoneUnverified</c> false): a phone number
/// typed on somebody else's booking, a web booking with no account, or a booking made without proof never counts -
/// so nobody can spoil another person's record. What each count means - "came" only what the venue vouched for,
/// late cancellations only of confirmed bookings made before the deadline, the rest of the bookings whose time came
/// "unmarked" - is on <see cref="GuestRecord"/>.
/// </para>
/// <para>
/// <b>Takeaway orders not collected</b> are read beside the bookings, in the same statement, as their own count
/// (<see cref="GuestRecord.TakeawayNoShows"/>, a seek on <c>IX_TakeawayOrders_DinerUserId_CreatedAtUtc</c>), and count
/// toward the no-show block as a booking's no-show does (<see cref="BlockingNoShowDaysAsync"/>). A takeaway order is
/// always placed by a proved account, so there is no stranger's order to leave out.
/// </para>
/// </remarks>
internal static class GuestRecords
{
    /// <summary>The records of these accounts at <paramref name="nowUtc"/>; an account with nothing is a new guest.</summary>
    public static async Task<IReadOnlyDictionary<Guid, GuestRecord>> ForAccountsAsync(
        YallaDbContext db,
        IEnumerable<Guid> accounts,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var accountIds = accounts.Distinct().ToList();

        if (accountIds.Count == 0)
        {
            return new Dictionary<Guid, GuestRecord>();
        }

        var fromUtc = nowUtc.AddDays(-GuestRecordRules.WindowDays);

        // One statement: per account, its counts and its block, each a seek on the account's index.
        var rows = await db.DinerUsers
            .AsNoTracking()
            .Where(d => accountIds.Contains(d.Id))
            .Select(d => new
            {
                Account = d.Id,
                Came = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                  && !r.PhoneUnverified
                                                  && r.StartUtc > fromUtc
                                                  && r.StartUtc <= nowUtc
                                                  && (r.Status == ReservationStatus.Seated
                                                      || r.Status == ReservationStatus.Completed)
                                                  && db.TableSessions.Any(s => s.ReservationId == r.Id
                                                                               && (s.SeatedByStaffId != null
                                                                                   || db.TabOrders.Any(o => o.TabId == s.TabId
                                                                                       && (o.Status == TabOrderStatus.InKitchen
                                                                                           || o.Status == TabOrderStatus.Ready
                                                                                           || o.Status == TabOrderStatus.Served))
                                                                                   || db.Payments.Any(pay => pay.TabId == s.TabId
                                                                                       && pay.Status == PaymentStatus.Succeeded)))),
                Seated = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                    && !r.PhoneUnverified
                                                    && r.StartUtc > fromUtc
                                                    && r.StartUtc <= nowUtc
                                                    && (r.Status == ReservationStatus.Seated
                                                        || r.Status == ReservationStatus.Completed)),
                NoShows = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                     && !r.PhoneUnverified
                                                     && r.StartUtc > fromUtc
                                                     && r.StartUtc <= nowUtc
                                                     && r.Status == ReservationStatus.NoShow),
                Late = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                  && !r.PhoneUnverified
                                                  && r.StartUtc > fromUtc
                                                  && r.StartUtc <= nowUtc
                                                  && r.Status == ReservationStatus.CancelledByDiner
                                                  && r.LateCancellationCounts),
                Lapsed = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                    && !r.PhoneUnverified
                                                    && r.StartUtc > fromUtc
                                                    && r.EndUtc <= nowUtc
                                                    && r.Status == ReservationStatus.Confirmed),

                // Over the promised ready time - when the guest failed to come, as a booking's start is.
                TakeawayNoShows = db.TakeawayOrders.Count(o => o.DinerUserId == d.Id
                                                               && o.Status == TakeawayStatus.NoShow
                                                               && o.ReadyAtUtc > fromUtc
                                                               && o.ReadyAtUtc <= nowUtc),

                // The account's block, or one holding its number - a re-registered account still sees it (N1).
                BlockedUntil = db.BookingBlocks
                    .Where(b => (b.DinerUserId == d.Id || (d.PhoneE164 != null && b.PhoneE164 == d.PhoneE164))
                                && b.LiftedAtUtc == null
                                && b.BlockedUntilUtc > nowUtc)
                    .Max(b => (DateTime?)b.BlockedUntilUtc),
            })
            .ToListAsync(cancellationToken);

        var records = new Dictionary<Guid, GuestRecord>();

        foreach (var row in rows)
        {
            // Seated or completed without the venue vouching for it - opened from the code alone - is unmarked (S1).
            var unmarked = row.Lapsed + (row.Seated - row.Came);
            var booked = row.Came + row.NoShows + row.Late + unmarked;

            records[row.Account] = new GuestRecord(
                booked, row.Came, row.NoShows, row.Late, unmarked, booked == 0, row.TakeawayNoShows, row.BlockedUntil);
        }

        // An id with no account row (none in practice) is a new guest.
        foreach (var id in accountIds)
        {
            records.TryAdd(id, GuestRecord.New);
        }

        return records;
    }

    /// <summary>One account's record.</summary>
    public static async Task<GuestRecord> ForAccountAsync(
        YallaDbContext db,
        Guid account,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        (await ForAccountsAsync(db, [account], nowUtc, cancellationToken))[account];

    /// <summary>
    /// The no-shows of an account that count toward a block as of <paramref name="referenceUtc"/>, as the distinct
    /// (venue, service day) pairs they fall on - at most one per venue per day counts (review B1b). Takeaway orders
    /// not collected count exactly as bookings missed do, in the same pairs: a missed booking and an uncollected order
    /// at one venue on one day are one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counted: the account's own bookings made with a proved number, marked no-show after the guest's grace
    /// (<c>NoShowCountsTowardBlock</c>, review B1a), not withdrawn, whose start is in the
    /// <see cref="NoShowBlockRules.WindowHours"/> hours before the reference - and after the account's last block
    /// ended or was lifted (review S4), so no-shows a lifted block was made of never block again.
    /// </para>
    /// <para>
    /// And the account's takeaway orders marked not collected, whose <b>promised ready time</b> is in the same window -
    /// the time the guest failed to come, as a booking's start is. A takeaway no-show can only be marked thirty minutes
    /// after that (<c>TakeawayRules.NoShowFromUtc</c>), which is its grace; it cannot be withdrawn. Its service day is
    /// the order's <c>LocalDate</c>.
    /// </para>
    /// </remarks>
    public static async Task<int> BlockingNoShowDaysAsync(
        YallaDbContext db,
        Guid account,
        DateTime referenceUtc,
        CancellationToken cancellationToken)
    {
        var windowStartUtc = referenceUtc.AddHours(-NoShowBlockRules.WindowHours);

        var lastEndedUtc = await db.BookingBlocks
            .AsNoTracking()
            .Where(b => b.DinerUserId == account && (b.LiftedAtUtc ?? b.BlockedUntilUtc) <= referenceUtc)
            .MaxAsync(b => (DateTime?)(b.LiftedAtUtc ?? b.BlockedUntilUtc), cancellationToken);

        var sinceUtc = lastEndedUtc is { } ended && ended > windowStartUtc ? ended : windowStartUtc;

        var bookings = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DinerUserId == account
                        && !r.PhoneUnverified
                        && r.Status == ReservationStatus.NoShow
                        && r.NoShowCountsTowardBlock
                        && r.StartUtc > sinceUtc
                        && r.StartUtc <= referenceUtc)
            .Select(r => new { r.Branch.VenueId, r.LocalDate })
            .Distinct()
            .ToListAsync(cancellationToken);

        var takeaway = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.DinerUserId == account
                        && o.Status == TakeawayStatus.NoShow
                        && o.ReadyAtUtc > sinceUtc
                        && o.ReadyAtUtc <= referenceUtc)
            .Join(db.Branches, o => o.BranchId, b => b.Id, (o, b) => new { b.VenueId, o.LocalDate })
            .Distinct()
            .ToListAsync(cancellationToken);

        return bookings
            .Select(d => (d.VenueId, d.LocalDate))
            .Union(takeaway.Select(d => (d.VenueId, d.LocalDate)))
            .Count();
    }

    /// <summary>
    /// When the block on this account - or on its proved number - ends, if one is in force at
    /// <paramref name="nowUtc"/>; otherwise null.
    /// </summary>
    public static Task<DateTime?> BlockedUntilAsync(
        YallaDbContext db,
        Guid account,
        string? phoneE164,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        db.BookingBlocks
            .AsNoTracking()
            .Where(b => (b.DinerUserId == account || (phoneE164 != null && b.PhoneE164 == phoneE164))
                        && b.LiftedAtUtc == null
                        && b.BlockedUntilUtc > nowUtc)
            .MaxAsync(b => (DateTime?)b.BlockedUntilUtc, cancellationToken);

    /// <summary>Refuses a booking - new or moved - while the account or its number is blocked.</summary>
    /// <exception cref="BookingBlockedForNoShowsException">A block is in force.</exception>
    public static async Task RequireNotBlockedAsync(
        YallaDbContext db,
        Guid account,
        string? phoneE164,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (await BlockedUntilAsync(db, account, phoneE164, nowUtc, cancellationToken) is { } until)
        {
            throw new BookingBlockedForNoShowsException(until);
        }
    }
}
