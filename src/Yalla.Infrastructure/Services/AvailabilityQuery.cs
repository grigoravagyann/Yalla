using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The availability read model, assembled in one SQL statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>One round trip.</b> The branch, its policy, its opening hours, every active table and the
/// bookings around the requested slot all arrive together. The naive shape - load the tables, then
/// ask "anything booked on this one?" per table - is an N+1 on the screen a diner stares at while
/// deciding whether to eat here.
/// </para>
/// <para>
/// <b>The bookings join is a left join, and that is load-bearing.</b> Each table's bookings are a
/// nested collection in the projection, which EF Core emits as a <c>LEFT JOIN</c>. An inner join
/// would compile, pass a casual eye, and silently drop every table with no bookings - which is
/// exactly the set of tables the diner most wants to see. A test asserts a table with nothing
/// booked still appears.
/// </para>
/// <para>
/// <b>The rules run in memory, not in SQL.</b> The conflict rule and the branch rules are pure
/// functions in the domain, and the same functions decide whether a booking is accepted. Writing
/// them a second time in SQL would let the screen and the endpoint disagree about the same table,
/// which reads to a diner as the product being broken. So the query fetches the small set of
/// bookings around the slot and <see cref="ReservationOverlap"/> judges them - the identical code
/// path the creation service takes inside its lock.
/// </para>
/// <para>
/// The bookings are fetched over a deliberately generous UTC guard window rather than the exact
/// slot, because the exact slot is not known until the branch's time zone has been read - and
/// reading it first would cost the second round trip this shape exists to avoid. Any zone is
/// within 14 hours of UTC, so two days either side of the anchor covers every offset and every
/// turn time, while still being a handful of rows per table.
/// </para>
/// </remarks>
internal sealed class AvailabilityQuery(YallaDbContext db, IClock clock) : IAvailabilityQuery
{
    /// <summary>
    /// How far either side of the requested local instant to fetch bookings.
    /// </summary>
    /// <remarks>
    /// Covers the widest real time-zone offset (14 hours) plus any sane turn time, with room to
    /// spare. It bounds the rows, not the correctness: everything inside is judged by the overlap
    /// rule, and nothing outside it can possibly overlap.
    /// </remarks>
    private static readonly TimeSpan GuardWindow = TimeSpan.FromDays(2);

    public async Task<BranchAvailability?> GetAvailabilityAsync(
        AvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nowUtc = clock.UtcNow;

        var row = await BuildQuery(request, nowUtc).FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Project(row, request, nowUtc);
    }

    public string GetAvailabilityQuerySql(AvailabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildQuery(request, clock.UtcNow).ToQueryString();
    }

    /// <summary>
    /// The one query.
    /// </summary>
    /// <remarks>
    /// <paramref name="nowUtc"/> and the guard bounds are passed in rather than read inside the
    /// expression so they become SQL parameters, which keeps the plan cacheable and lets tests
    /// drive the clock.
    /// </remarks>
    private IQueryable<AvailabilityRow> BuildQuery(AvailabilityRequest request, DateTime nowUtc)
    {
        var anchorUtc = AnchorUtc(request, nowUtc);
        var guardFromUtc = anchorUtc - GuardWindow;
        var guardToUtc = anchorUtc + GuardWindow;
        var relevantDays = RelevantDays(request, anchorUtc);

        return db.Branches

            // Not an optimisation, a requirement: the branch's owned ReservationPolicy is
            // projected whole below, and EF refuses to track an owned entity without its owner.
            // It belongs here rather than at the call site so the SQL-reporting path cannot
            // diverge from the path that actually runs.
            .AsNoTracking()

            // Diner browsing. A suspended or deleted venue does not exist here - it keeps every
            // row and stays visible to its owner through the admin surface, but a diner must not
            // be able to find it or book it.
            .Where(b => b.Id == request.BranchId
                        && b.IsActive
                        && b.Venue.IsActive
                        && b.Venue.SuspendedAtUtc == null
                        && b.Venue.DeletedAtUtc == null)
            .Select(b => new AvailabilityRow
            {
                BranchId = b.Id,
                BranchName = b.Name,
                TimeZoneId = b.TimeZoneId,
                FloorWidth = b.FloorWidth,
                FloorHeight = b.FloorHeight,
                Policy = b.ReservationPolicy,

                // Only the weekdays the opening-hours rule can consult. Two sibling collections in
                // one projection cross-multiply, so every extra opening block here is another copy
                // of every table row on the wire - and a branch is open seven days but a request
                // asks about one of them and the evening before it.
                OpeningHours = b.OpeningHours.Where(h => relevantDays.Contains(h.Day)).ToList(),
                Tables = b.DiningTables
                    .Where(t => t.IsActive)
                    .Select(t => new AvailabilityTableRow
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

                        // The open sitting, as a second left join beside the bookings one. This is
                        // what used to be missing: TableSession is the authoritative occupancy
                        // record, so without it a walk-in seated at seven did not stop a phone
                        // booking the same table for eight. A correlated subquery, so a table with
                        // nobody at it yields NULL rather than being dropped.
                        OpenSessionSeatedAtUtc = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (DateTime?)s.SeatedAtUtc)
                            .FirstOrDefault(),
                        OpenSessionId = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => (Guid?)s.Id)
                            .FirstOrDefault(),

                        // The left join. A table with none of these is not dropped - it is the
                        // most available table in the room. Served by
                        // IX_Reservations_DiningTableId_StartUtc_EndUtc.
                        Bookings = db.Reservations
                            .Where(r => r.DiningTableId == t.Id
                                        && (r.Status == ReservationStatus.Confirmed
                                            || r.Status == ReservationStatus.PendingApproval
                                            || r.Status == ReservationStatus.Seated)
                                        && r.EndUtc > guardFromUtc
                                        && r.StartUtc < guardToUtc)
                            .OrderBy(r => r.StartUtc)
                            .Select(r => new AvailabilityBookingRow
                            {
                                ReservationId = r.Id,
                                StartUtc = r.StartUtc,
                                EndUtc = r.EndUtc,
                                Status = r.Status,
                            })
                            .ToList(),
                    })
                    .ToList(),
            });
    }

    /// <summary>
    /// A UTC instant near the requested slot, computed <i>without</i> the branch's zone.
    /// </summary>
    /// <remarks>
    /// Only ever used to bound the guard window, never to answer anything. Treating the diner's
    /// local wall clock as though it were UTC is wrong by at most the zone's offset, which the
    /// guard window absorbs many times over.
    /// </remarks>
    /// <summary>
    /// The weekdays whose opening blocks could possibly apply.
    /// </summary>
    /// <remarks>
    /// The rule examines the local day the sitting starts on and the one before it, because a
    /// branch open 10:00-01:00 has a Friday block that runs into Saturday morning. When the diner
    /// named a date, that day and its predecessor are the whole answer. When they did not, the
    /// branch's local date can be a day either side of the UTC one, so the window widens by one in
    /// each direction - still fewer than a full week.
    /// </remarks>
    private static DayOfWeek[] RelevantDays(AvailabilityRequest request, DateTime anchorUtc)
    {
        var anchorDate = DateOnly.FromDateTime(anchorUtc);

        // Named a date: the sitting is on that day, so that day and the evening before it.
        // Named none: the branch's local date can be a day either side of the UTC one, so widen
        // by one in each direction - still four days rather than a whole week.
        int[] offsets = request.LocalDate is null ? [-2, -1, 0, 1] : [-1, 0];

        return offsets
            .Select(offset => anchorDate.AddDays(offset).DayOfWeek)
            .Distinct()
            .ToArray();
    }

    private static DateTime AnchorUtc(AvailabilityRequest request, DateTime nowUtc)
    {
        // Each half falls back on its own. A caller that names a date but no time - "what does
        // Christmas look like?" - must still anchor on Christmas, or the guard window would be
        // built around today and the query would return none of that evening's bookings.
        var localDate = request.LocalDate ?? DateOnly.FromDateTime(nowUtc);
        var localTime = request.LocalTime ?? TimeOnly.FromDateTime(nowUtc);

        return DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Utc);
    }

    private static BranchAvailability Project(AvailabilityRow row, AvailabilityRequest request, DateTime nowUtc)
    {
        var zone = BranchZone.For(row.TimeZoneId);
        var policy = row.Policy;

        // Omitted date or time means "now, at the branch" - what somebody standing outside the
        // door is asking. Resolved here because it needs the zone, which only arrived with the row.
        var localDate = request.LocalDate ?? zone.LocalDateAt(nowUtc);
        var localTime = request.LocalTime ?? zone.LocalTimeAt(nowUtc);

        if (!zone.TryToUtc(localDate, localTime, out var startUtc))
        {
            // The clocks go forward over the time they picked, so there is no slot to answer
            // about. Every table is unavailable for the same reason; say so once at the top.
            return EmptyAnswer(
                row, request, zone, localDate, localTime, nowUtc,
                ReservationRejectionReason.LocalTimeDoesNotExist);
        }

        var endUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);

        // The rules that refuse the whole request before any table is considered. Reported once
        // rather than repeated on forty tables that are each unavailable for the same reason.
        var requestReason =
            ReservationRules.CheckTiming(startUtc, nowUtc, localDate, zone.LocalDateAt(nowUtc), policy)
            ?? ReservationRules.CheckOpeningHours(
                row.OpeningHours, localDate.ToDateTime(localTime), zone.ToLocal(endUtc));

        var proposed = new BookedInterval(startUtc, endUtc);
        var needsApproval = ReservationRules.NeedsApproval(request.PartySize, policy);

        var tables = row.Tables
            .Select(t => ProjectTable(t, proposed, zone, policy, request.PartySize, requestReason, needsApproval, nowUtc))
            // Ordered here rather than in SQL, for the same reason as the floor query: the client
            // draws by area then label, and "7" before "10" needs natural ordering that SQL
            // collation will not give.
            .OrderBy(t => t.FloorAreaDisplayOrder)
            .ThenBy(t => t.Label.Length)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BranchAvailability
        {
            BranchId = row.BranchId,
            BranchName = row.BranchName,
            TimeZoneId = row.TimeZoneId,
            FloorWidth = row.FloorWidth,
            FloorHeight = row.FloorHeight,
            LocalDate = localDate,
            LocalTime = localTime,
            PartySize = request.PartySize,
            RequestedStartUtc = startUtc,
            RequestedEndUtc = endUtc,
            TurnTimeMinutes = policy.TurnTimeMinutes,
            BufferMinutes = policy.BufferMinutes,
            UnavailableReason = requestReason,
            AsOfUtc = nowUtc,
            Tables = tables,
        };
    }

    private static TableAvailability ProjectTable(
        AvailabilityTableRow table,
        BookedInterval proposed,
        BranchZone zone,
        ReservationPolicy policy,
        int partySize,
        ReservationRejectionReason? requestReason,
        bool needsApproval,
        DateTime nowUtc)
    {
        // The next booking that still matters at the requested instant: the earliest one not
        // already over by then. Drives both the derived state and the window.
        var next = table.Bookings
            .Where(b => b.EndUtc > proposed.StartUtc)
            .MinBy(b => b.StartUtc);

        // The same pure rule the creation service applies inside its lock, over the same statuses.
        var conflict = table.Bookings.FirstOrDefault(b =>
            ReservationOverlap.Conflicts(
                proposed, new BookedInterval(b.StartUtc, b.EndUtc), b.Status, policy.BufferMinutes));

        // And the people already sitting there, projected forward by the branch's turn time. The
        // same rule the booking service applies inside its lock, so the sheet and the refusal agree.
        var occupied = table.OpenSessionSeatedAtUtc is { } seatedAtUtc
                       && SessionOccupancy.Conflicts(
                           proposed, seatedAtUtc, policy.TurnTimeMinutes, policy.BufferMinutes);

        // Physical status is checked by CheckTable only for the here and now; beyond the horizon it
        // would be a statement about tonight applied to tomorrow.
        var physicalReason = TableStateProjection.IsWithinPhysicalHorizon(proposed.StartUtc, nowUtc)
            ? ReservationRules.CheckTable(table.Status, table.IsBookable, table.Seats, partySize, policy)
            : ReservationRules.CheckTable(
                table.Status == TableStatus.OutOfService ? TableStatus.OutOfService : TableStatus.Free,
                table.IsBookable,
                table.Seats,
                partySize,
                policy);

        var reason = requestReason
                     ?? physicalReason
                     ?? (occupied ? ReservationRejectionReason.TableCurrentlyOccupied : (ReservationRejectionReason?)null)
                     ?? (conflict is null ? null : ReservationRejectionReason.TableAlreadyBooked);

        var isAvailable = reason is null;

        // Null means no limit, and no limit is the answer a diner would rather have. The same
        // arithmetic the floor plan uses for "how long can I give this table away for?".
        var availableUntilUtc = isAvailable
            ? TableStateProjection.FreeUntil(next?.StartUtc, policy.BufferMinutes)
            : null;

        var windowMinutes = availableUntilUtc is null
            ? (int?)null
            : (int)(availableUntilUtc.Value - proposed.StartUtc).TotalMinutes;

        return new TableAvailability
        {
            TableId = table.TableId,
            Label = table.Label,
            Seats = table.Seats,
            X = table.X,
            Y = table.Y,
            Width = table.Width,
            Height = table.Height,
            RotationDegrees = table.RotationDegrees,
            Shape = table.Shape,
            FloorAreaId = table.FloorAreaId,
            FloorAreaName = table.FloorAreaName,
            FloorAreaDisplayOrder = table.FloorAreaDisplayOrder ?? 0,
            IsBookable = table.IsBookable,
            PhysicalStatus = table.Status,

            // Derived for the requested instant, not for now: a table free this afternoon but
            // booked at 20:00 reads as ReservedSoon when the question is about 20:00 - and a table
            // occupied tonight reads as free when the question is about tomorrow.
            State = TableStateProjection.DeriveAt(
                table.Status,
                table.OpenSessionSeatedAtUtc,
                next?.StartUtc,
                proposed.StartUtc,
                nowUtc,
                policy.TurnTimeMinutes,
                policy.BufferMinutes),

            IsAvailable = isAvailable,
            UnavailableReason = reason,
            RequiresApproval = needsApproval,

            Window = isAvailable
                ? new TableAvailabilityWindow(
                    AvailableFromUtc: proposed.StartUtc,
                    AvailableUntilUtc: availableUntilUtc,
                    HasNoLaterBooking: next is null,
                    WindowMinutes: windowMinutes,
                    IsShorterThanTurnTime: windowMinutes is { } minutes && minutes < policy.TurnTimeMinutes,
                    AvailableFromLocal: zone.LocalTimeAt(proposed.StartUtc),
                    AvailableUntilLocal: availableUntilUtc is null ? null : zone.LocalTimeAt(availableUntilUtc.Value))
                : null,

            NextReservationId = next?.ReservationId,
            NextReservationStartUtc = next?.StartUtc,
        };
    }

    /// <summary>
    /// The answer when the requested local time does not exist at all, so no slot can be computed.
    /// Every table is still listed - the floor plan is still drawn - each labelled with the one
    /// reason.
    /// </summary>
    private static BranchAvailability EmptyAnswer(
        AvailabilityRow row,
        AvailabilityRequest request,
        BranchZone zone,
        DateOnly localDate,
        TimeOnly localTime,
        DateTime nowUtc,
        ReservationRejectionReason reason) =>
        new()
        {
            BranchId = row.BranchId,
            BranchName = row.BranchName,
            TimeZoneId = row.TimeZoneId,
            FloorWidth = row.FloorWidth,
            FloorHeight = row.FloorHeight,
            LocalDate = localDate,
            LocalTime = localTime,
            PartySize = request.PartySize,
            RequestedStartUtc = nowUtc,
            RequestedEndUtc = nowUtc,
            TurnTimeMinutes = row.Policy.TurnTimeMinutes,
            BufferMinutes = row.Policy.BufferMinutes,
            UnavailableReason = reason,
            AsOfUtc = nowUtc,
            Tables = row.Tables
                .Select(t => new TableAvailability
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
                    State = TableStateProjection.Derive(t.Status, null, nowUtc, row.Policy.BufferMinutes),
                    IsAvailable = false,
                    UnavailableReason = reason,
                    RequiresApproval = false,

                    // No slot could be computed, so there is no window to describe. Null rather
                    // than a zero-length one, which would read as "available for no time at all".
                    Window = null,
                })
                .OrderBy(t => t.FloorAreaDisplayOrder)
                .ThenBy(t => t.Label.Length)
                .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

    private sealed class AvailabilityRow
    {
        public Guid BranchId { get; init; }

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public int FloorWidth { get; init; }

        public int FloorHeight { get; init; }

        /// <summary>The owned policy, carried whole so the rules get every threshold they need.</summary>
        public ReservationPolicy Policy { get; init; } = null!;

        public List<OpeningHours> OpeningHours { get; init; } = [];

        public List<AvailabilityTableRow> Tables { get; init; } = [];
    }

    private sealed class AvailabilityTableRow
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

        /// <summary>When the party currently at this table sat down. Null when nobody is at it.</summary>
        public DateTime? OpenSessionSeatedAtUtc { get; init; }

        public Guid? OpenSessionId { get; init; }

        public List<AvailabilityBookingRow> Bookings { get; init; } = [];
    }

    private sealed class AvailabilityBookingRow
    {
        public Guid ReservationId { get; init; }

        public DateTime StartUtc { get; init; }

        public DateTime EndUtc { get; init; }

        public ReservationStatus Status { get; init; }
    }
}
