using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
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
/// so nobody can spoil another person's record.
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
                                                  && r.CancelledAfterDeadline),
                Unmarked = db.Reservations.Count(r => r.DinerUserId == d.Id
                                                      && !r.PhoneUnverified
                                                      && r.StartUtc > fromUtc
                                                      && r.StartUtc <= nowUtc
                                                      && r.Status == ReservationStatus.Confirmed),
                BlockedUntil = db.BookingBlocks
                    .Where(b => b.DinerUserId == d.Id && b.LiftedAtUtc == null && b.BlockedUntilUtc > nowUtc)
                    .Max(b => (DateTime?)b.BlockedUntilUtc),
            })
            .ToListAsync(cancellationToken);

        var records = new Dictionary<Guid, GuestRecord>();

        foreach (var row in rows)
        {
            var booked = row.Came + row.NoShows + row.Late + row.Unmarked;

            records[row.Account] = new GuestRecord(
                booked, row.Came, row.NoShows, row.Late, booked == 0, row.BlockedUntil);
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
