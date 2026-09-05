using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Floor;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The floor read model, assembled in one SQL statement.
/// </summary>
/// <remarks>
/// <para>
/// The shape matters more than it looks. Projecting the branch with its tables as a nested
/// collection, and folding the open session and next booking in as correlated subqueries, makes
/// EF Core emit a single statement with <c>OUTER APPLY</c>s - not one query per table. A version
/// of this that loaded tables and then looped asking "any booking for this one?" would issue
/// dozens of round trips on the most-called endpoint in the product.
/// </para>
/// <para>
/// The derived state is computed in memory, after the rows arrive, by
/// <see cref="TableStateProjection"/> - the same code the state-change service uses. Doing it in
/// SQL would duplicate the rule in a second language and let the two drift.
/// </para>
/// </remarks>
internal sealed class FloorQuery(YallaDbContext db, IClock clock) : IFloorQuery
{
    /// <summary>
    /// The most changes one catch-up call will return.
    /// </summary>
    /// <remarks>
    /// A client that has been away for a whole service could otherwise ask for thousands of rows
    /// in one response. The page says whether more remain, so catching up is a loop rather than a
    /// single unbounded read - and a client that far behind is usually better off refetching the
    /// floor anyway, which the sequence numbers let it decide.
    /// </remarks>
    public const int MaxChangePageSize = 500;

    public async Task<BranchFloorState?> GetFloorStateAsync(
        Guid branchId,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;

        var row = await BuildQuery(branchId, atUtc)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new BranchFloorState
        {
            MaxSequence = row.MaxSequence,
            BranchId = row.BranchId,
            BranchName = row.BranchName,
            TimeZoneId = row.TimeZoneId,
            FloorWidth = row.FloorWidth,
            FloorHeight = row.FloorHeight,
            AsOfUtc = atUtc,
            Tables = row.Tables
                .Select(t => new TableFloorState
                {
                    TableId = t.TableId,
                    Label = t.Label,
                    Seats = t.Seats,
                    X = t.X,
                    Y = t.Y,
                    Width = t.Width,
                    Height = t.Height,
                    RotationDegrees = t.RotationDegrees,
                    Shape = t.Shape,
                    FloorAreaId = t.FloorAreaId,
                    FloorAreaName = t.FloorAreaName,
                    FloorAreaDisplayOrder = t.FloorAreaDisplayOrder ?? 0,
                    IsBookable = t.IsBookable,
                    PhysicalStatus = t.Status,
                    // Physical status only counts while the query is about now; beyond that the
                    // sitting is projected forward and the momentary status is dropped entirely.
                    State = TableStateProjection.DeriveAt(
                        t.Status,
                        t.SeatedAtUtc,
                        t.NextReservationStartUtc,
                        atUtc,
                        nowUtc,
                        row.TurnTimeMinutes,
                        row.BufferMinutes),
                    CurrentSessionId = t.OpenSessionId,
                    SeatedAtUtc = t.SeatedAtUtc,
                    PartySize = t.PartySize,
                    OccupancySource = t.OccupancySource,
                    NextReservationId = t.NextReservationId,
                    NextReservationStartUtc = t.NextReservationStartUtc,
                    FreeUntilUtc = TableStateProjection.FreeUntil(
                        t.NextReservationStartUtc, row.BufferMinutes),
                })
                // Ordered here rather than in SQL: the client draws by area then label, and
                // "7" before "10" needs natural ordering that SQL collation will not give.
                .OrderBy(t => t.FloorAreaDisplayOrder)
                .ThenBy(t => t.Label.Length)
                .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public async Task<BranchChangePage?> GetChangesAsync(
        Guid branchId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Branches.AsNoTracking().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            return null;
        }

        var capped = Math.Clamp(limit, 1, MaxChangePageSize);

        // The end of the stream is read first. Reading it after the page would let a change land
        // in between and make a full page look like the end.
        var maxSequence = await db.TableStateChanges
            .AsNoTracking()
            .Where(c => c.BranchId == branchId)
            .MaxAsync(c => (long?)c.Sequence, cancellationToken) ?? 0L;

        // One more than asked for, so "are there further entries" is answered without a count.
        var rows = await db.TableStateChanges
            .AsNoTracking()
            .Where(c => c.BranchId == branchId && c.Sequence > afterSequence)
            .OrderBy(c => c.Sequence)
            .Take(capped + 1)
            .Select(c => new BranchChange(
                c.Sequence,
                c.Id,
                c.DiningTableId,
                c.DiningTable.Label,
                c.FromStatus,
                c.ToStatus,
                c.Reason,
                c.ActorType,
                c.ActorId,
                c.AtUtc,
                c.TableSessionId,
                c.ReservationId))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > capped;

        return new BranchChangePage(
            branchId,
            afterSequence,
            maxSequence,
            hasMore,
            hasMore ? rows.Take(capped).ToList() : rows);
    }

    public string GetFloorQuerySql(Guid branchId, DateTime atUtc) =>
        BuildQuery(branchId, atUtc).ToQueryString();

    /// <summary>
    /// The one query. <paramref name="atUtc"/> is passed in rather than read inside the
    /// expression so it becomes a SQL parameter, which keeps the plan cacheable and lets tests
    /// drive the clock.
    /// </summary>
    private IQueryable<FloorRow> BuildQuery(Guid branchId, DateTime atUtc) =>
        db.Branches
            .Where(b => b.Id == branchId)
            .Select(b => new FloorRow
            {
                BranchId = b.Id,
                BranchName = b.Name,
                TimeZoneId = b.TimeZoneId,
                FloorWidth = b.FloorWidth,
                FloorHeight = b.FloorHeight,
                BufferMinutes = b.ReservationPolicy.BufferMinutes,

                // Needed to project the open sitting forward. Read per query rather than stored on
                // the session: an owner who shortens turn time this afternoon means it for the
                // tables occupied this afternoon.
                TurnTimeMinutes = b.ReservationPolicy.TurnTimeMinutes,

                // Where the branch's change stream ends, so every floor response tells a client its
                // position in the stream. Folded in as a correlated subquery rather than read
                // separately: this endpoint is the most-called in the product and it stays one
                // round trip. Zero when the branch has no history yet.
                MaxSequence = db.TableStateChanges
                    .Where(c => c.BranchId == b.Id)
                    .Max(c => (long?)c.Sequence) ?? 0L,
                Tables = b.DiningTables
                    .Where(t => t.IsActive)
                    .Select(t => new FloorTableRow
                    {
                        TableId = t.Id,
                        Label = t.Label,
                        Seats = t.Seats,
                        X = t.X,
                        Y = t.Y,
                        Width = t.Width,
                        Height = t.Height,
                        RotationDegrees = t.RotationDegrees,
                        Shape = t.Shape,
                        FloorAreaId = t.FloorAreaId,
                        FloorAreaName = t.FloorArea == null ? null : t.FloorArea.Name,
                        FloorAreaDisplayOrder = t.FloorArea == null ? null : t.FloorArea.DisplayOrder,
                        IsBookable = t.IsBookable,
                        Status = t.Status,

                        // The open occupancy. Served by the filtered unique index on
                        // (DiningTableId) where ClosedAtUtc IS NULL, so this reads one row from a
                        // tiny index rather than scanning the session history.
                        OpenSessionId = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (Guid?)s.Id)
                            .FirstOrDefault(),
                        SeatedAtUtc = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (DateTime?)s.SeatedAtUtc)
                            .FirstOrDefault(),
                        PartySize = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (int?)s.PartySize)
                            .FirstOrDefault(),
                        OccupancySource = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (TableSessionSource?)s.Source)
                            .FirstOrDefault(),

                        // The next booking that still matters: not yet over, not cancelled. This
                        // is the whole reason TableStatus has no stored Reserved member.
                        // Served by IX_Reservations_DiningTableId_StartUtc_EndUtc.
                        NextReservationId = db.Reservations
                            .Where(r => r.DiningTableId == t.Id
                                        && r.EndUtc > atUtc
                                        && (r.Status == ReservationStatus.Confirmed
                                            || r.Status == ReservationStatus.PendingApproval))
                            .OrderBy(r => r.StartUtc)
                            .Select(r => (Guid?)r.Id)
                            .FirstOrDefault(),
                        NextReservationStartUtc = db.Reservations
                            .Where(r => r.DiningTableId == t.Id
                                        && r.EndUtc > atUtc
                                        && (r.Status == ReservationStatus.Confirmed
                                            || r.Status == ReservationStatus.PendingApproval))
                            .OrderBy(r => r.StartUtc)
                            .Select(r => (DateTime?)r.StartUtc)
                            .FirstOrDefault(),
                    })
                    .ToList(),
            });

    private sealed class FloorRow
    {
        public Guid BranchId { get; init; }

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public int FloorWidth { get; init; }

        public int FloorHeight { get; init; }

        public int BufferMinutes { get; init; }

        public int TurnTimeMinutes { get; init; }

        public long MaxSequence { get; init; }

        public List<FloorTableRow> Tables { get; init; } = [];
    }

    private sealed class FloorTableRow
    {
        public Guid TableId { get; init; }

        public string Label { get; init; } = null!;

        public int Seats { get; init; }

        public int X { get; init; }

        public int Y { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public double RotationDegrees { get; init; }

        public TableShape Shape { get; init; }

        public Guid? FloorAreaId { get; init; }

        public string? FloorAreaName { get; init; }

        public int? FloorAreaDisplayOrder { get; init; }

        public bool IsBookable { get; init; }

        public TableStatus Status { get; init; }

        public Guid? OpenSessionId { get; init; }

        public DateTime? SeatedAtUtc { get; init; }

        public int? PartySize { get; init; }

        public TableSessionSource? OccupancySource { get; init; }

        public Guid? NextReservationId { get; init; }

        public DateTime? NextReservationStartUtc { get; init; }
    }
}
