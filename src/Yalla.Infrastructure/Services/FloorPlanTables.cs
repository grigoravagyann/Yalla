using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The tables' side of the floor plan that more than one part reads: whether anything ever happened at a table,
/// whether a booking is still to come on it, and the table as the editor reads it.
/// </summary>
/// <remarks>
/// <see cref="TablesWithHistoryAsync"/> and <see cref="TableProjection"/>'s <c>IsDeletable</c> are one rule read
/// twice, and live together for that reason.
/// </remarks>
internal sealed class FloorPlanTables(YallaDbContext db, IClock clock)
{
    /// <summary>
    /// Refuses when any of these tables holds a booking still to come: Confirmed or PendingApproval
    /// and not yet ended. Past, cancelled and completed bookings are history and do not count.
    /// </summary>
    /// <remarks>
    /// "Not yet ended" rather than "not yet started": a Confirmed booking whose party is late is still
    /// on its table, and deleting the table under it is the same stranding.
    /// </remarks>
    /// <exception cref="TableHasFutureBookingsException">Naming every such table and booking.</exception>
    public async Task RefuseUpcomingBookingsAsync(IReadOnlyList<Guid> tableIds, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var upcoming = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId != null
                        && tableIds.Contains(r.DiningTableId.Value)
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval)
                        && r.EndUtc > nowUtc)
            .Select(r => new { r.Code, r.DiningTable!.Label })
            .ToListAsync(cancellationToken);

        if (upcoming.Count == 0)
        {
            return;
        }

        db.ChangeTracker.Clear();

        throw new TableHasFutureBookingsException(
            [.. upcoming.Select(u => u.Label).Distinct().Order(StringComparer.Ordinal)],
            [.. upcoming.Select(u => u.Code).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Which of these tables anything ever happened at: a booking, a seating, a bill - or an entry
    /// in the state-change log.
    /// </summary>
    /// <remarks>
    /// The audit log counts, and it is the easy one to forget. A table that was held for a late
    /// party, or marked out of service for a wobbly leg, has no reservation, no session and no tab,
    /// but it does have <c>TableStateChange</c> rows - and that foreign key is <c>Restrict</c>, so
    /// deleting the table would fail in the database with a constraint error nobody can act on
    /// instead of quietly deactivating it here.
    /// </remarks>
    public async Task<HashSet<Guid>> TablesWithHistoryAsync(IReadOnlyList<Guid> tableIds, CancellationToken cancellationToken)
    {
        var reserved = db.Reservations
            .Where(r => r.DiningTableId != null && tableIds.Contains(r.DiningTableId.Value))
            .Select(r => r.DiningTableId!.Value);
        var seated = db.TableSessions.Where(s => tableIds.Contains(s.DiningTableId)).Select(s => s.DiningTableId);
        var billed = db.Tabs.Where(t => tableIds.Contains(t.DiningTableId)).Select(t => t.DiningTableId);
        var audited = db.TableStateChanges
            .Where(c => c.DiningTableId != null && tableIds.Contains(c.DiningTableId.Value))
            .Select(c => c.DiningTableId!.Value);

        var used = await reserved.Union(seated).Union(billed).Union(audited).Distinct().ToListAsync(cancellationToken);

        return [.. used];
    }

    /// <summary>
    /// A table as the editor reads it, including whether deleting it would actually work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IsDeletable</c> is four <c>EXISTS</c> subqueries rather than the three the floor plan
    /// obviously needs, and the fourth is the one that matters: <see cref="TablesWithHistoryAsync"/>
    /// counts <c>TableStateChange</c> rows too, because that foreign key is <c>Restrict</c>. A table
    /// held for a late party has no booking, no seating and no bill, but it does have audit rows -
    /// and if this flag disagreed with the rule the delete actually applies, the editor would offer
    /// a delete that then refuses, which is exactly the confusion the flag exists to remove. The two
    /// must be read as one rule; changing either alone is a bug.
    /// </para>
    /// <para>
    /// An instance expression rather than a static one, because it closes over the context. It runs
    /// once per editing session, not on the floor screen's refresh loop, so four correlated
    /// subqueries against indexed foreign keys are the right trade for one round trip.
    /// </para>
    /// </remarks>
    public System.Linq.Expressions.Expression<Func<DiningTable, FloorTableView>> TableProjection =>
        t => new FloorTableView(
            t.Id, t.Label, t.Seats, t.FloorAreaId, t.IsBookable, t.IsActive, t.QrToken, t.Status,
            !db.Reservations.Any(r => r.DiningTableId == t.Id)
            && !db.TableSessions.Any(ts => ts.DiningTableId == t.Id)
            && !db.Tabs.Any(tb => tb.DiningTableId == t.Id)
            && !db.TableStateChanges.Any(c => c.DiningTableId == t.Id),
            t.PhotoId,
            t.PhotoX,
            t.PhotoY);
}
