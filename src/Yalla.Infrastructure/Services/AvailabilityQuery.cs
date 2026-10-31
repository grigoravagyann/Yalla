using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The availability read model, assembled in one SQL statement - for one slot, or for every quarter hour
/// of one day.
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
/// <para>
/// <b>The day read is the same statement, judged ninety-six times.</b> The day needs the same rows as
/// one slot on it - the bookings around it, the open sittings, its opening blocks and the evening
/// before's - so the day is one round trip too, and each quarter hour goes through
/// <see cref="Answer"/>, the one per-table judgement the single-slot read makes. The time picker
/// and the room screen therefore cannot disagree about a table at a time; a test asks both about
/// every listed time and compares.
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

    private const int MinutesPerDay = 24 * 60;

    public async Task<BranchAvailability?> GetAvailabilityAsync(
        AvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nowUtc = clock.UtcNow;

        var row = await SlotQuery(request, nowUtc).FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        await WithSpecialDaysAsync(row, RelevantDates(request.LocalDate, AnchorUtc(request, nowUtc)), cancellationToken);

        return Project(row, request, nowUtc);
    }

    public string GetAvailabilityQuerySql(AvailabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SlotQuery(request, clock.UtcNow).ToQueryString();
    }

    public async Task<BranchDaySlots?> GetDaySlotsAsync(
        DaySlotsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nowUtc = clock.UtcNow;

        var row = await DayQuery(request).FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        await WithSpecialDaysAsync(row, RelevantDates(request.LocalDate, request.LocalDate.ToDateTime(TimeOnly.MinValue)), cancellationToken);

        // A branch that books by time only answers in guests, from its own bookings that hold a place on the day.
        if (row.Policy.BookingMode == BookingMode.TimeOnly)
        {
            row.TimeOnlyBookings = await TimeOnlyBookingsAsync(row.BranchId, request, cancellationToken);
        }

        return ProjectDay(row, request, nowUtc);
    }

    public string GetDaySlotsQuerySql(DaySlotsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DayQuery(request).ToQueryString();
    }

    /// <summary>
    /// The special days on the dates the rules consult, which replace those dates' weekly blocks - read beside the
    /// main statement rather than in it.
    /// </summary>
    /// <remarks>
    /// A second, small statement on purpose: inside the main projection they were a sibling collection of the
    /// tables, and a special day with two blocks sent every table row, with its bookings, twice.
    /// </remarks>
    private async Task WithSpecialDaysAsync(AvailabilityRow row, DateOnly[] dates, CancellationToken cancellationToken)
    {
        row.SpecialFrom = dates.Min();
        row.SpecialTo = dates.Max();
        row.SpecialDays = await OpeningCalendars.SpecialDaysAsync(
            db, row.BranchId, row.SpecialFrom, row.SpecialTo, cancellationToken);
    }

    /// <summary>The one query, anchored on the slot asked about.</summary>
    private IQueryable<AvailabilityRow> SlotQuery(AvailabilityRequest request, DateTime nowUtc)
    {
        var anchorUtc = AnchorUtc(request, nowUtc);

        return BuildQuery(
            request.BranchId, anchorUtc, RelevantDates(request.LocalDate, anchorUtc), request.ExcludeReservationId);
    }

    /// <summary>
    /// The one query, anchored on noon of the day asked about.
    /// </summary>
    /// <remarks>
    /// Noon so the guard window sits evenly over the day: the earliest start (midnight, fourteen
    /// hours ahead of UTC) and the latest sitting's end (23:45 plus the longest turn and clearing
    /// time, twelve hours behind) are both well inside two days of it. The opening blocks are the
    /// day's and the evening before it, exactly as for one slot on that day.
    /// </remarks>
    private IQueryable<AvailabilityRow> DayQuery(DaySlotsRequest request)
    {
        var anchorUtc = DateTime.SpecifyKind(request.LocalDate.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);

        return BuildQuery(
            request.BranchId, anchorUtc, RelevantDates(request.LocalDate, anchorUtc), request.ExcludeReservationId);
    }

    /// <summary>
    /// The one query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard bounds and the excluded booking are passed in rather than read inside the
    /// expression so they become SQL parameters, which keeps the plan cacheable and lets tests
    /// drive the clock.
    /// </para>
    /// <para>
    /// <paramref name="excludeReservationId"/> drops one booking from the bookings join - the
    /// diner's own, while they change it - so the table they already hold reads as free to them
    /// and every other booking still counts. <see cref="Guid.Empty"/> stands for "none", which no
    /// booking's id can be.
    /// </para>
    /// </remarks>
    private IQueryable<AvailabilityRow> BuildQuery(
        Guid branchId,
        DateTime anchorUtc,
        DateOnly[] relevantDates,
        Guid? excludeReservationId)
    {
        var relevantDays = relevantDates.Select(d => d.DayOfWeek).Distinct().ToArray();
        var guardFromUtc = anchorUtc - GuardWindow;
        var guardToUtc = anchorUtc + GuardWindow;
        var excludedId = excludeReservationId ?? Guid.Empty;

        return db.Branches

            // Not an optimisation, a requirement: the branch's owned ReservationPolicy is
            // projected whole below, and EF refuses to track an owned entity without its owner.
            // It belongs here rather than at the call site so the SQL-reporting path cannot
            // diverge from the path that actually runs.
            .AsNoTracking()

            // Diner browsing. A suspended or deleted venue does not exist here - it keeps every
            // row and stays visible to its owner through the admin surface, but a diner must not
            // be able to find it or book it - and nor does a branch not taking guests yet.
            .Where(b => b.Id == branchId)
            .Where(DinerVisibility.Visible(db))
            .Select(b => new AvailabilityRow
            {
                BranchId = b.Id,
                BranchName = b.Name,
                TimeZoneId = b.TimeZoneId,
                Policy = b.ReservationPolicy,

                // Only the weekdays the opening-hours rule can consult. Two sibling collections in
                // one projection cross-multiply, so every extra opening block here is another copy
                // of every table row on the wire - and a branch is open seven days but a request
                // asks about one of them and the evening before it.
                OpeningHours = b.OpeningHours.Where(h => relevantDays.Contains(h.Day)).ToList(),

                // The special days on those dates are read beside this statement: see WithSpecialDaysAsync.
                // Pinned tables only. A diner books by tapping a table's pin on a room photo, so a
                // table with no pin has nowhere to be tapped and is not offered - however bookable
                // it is. Staff still seat it; that is the floor read, not this one. And the pin must
                // be on a photo of the table's own room: the rooms read draws a table on its room's
                // photos, so a pin left on another room's picture is a pin nobody can see, and a
                // table offered here that the room view never shows.
                Tables = b.DiningTables
                    .Where(t => t.IsActive
                                && t.PhotoId != null
                                && t.PhotoX != null
                                && t.PhotoY != null
                                && db.FloorAreaPhotos.Any(p => p.FloorAreaId == t.FloorAreaId && p.PhotoId == t.PhotoId))
                    .Select(t => new AvailabilityTableRow
                    {
                        TableId = t.Id,
                        Label = t.Label,
                        Seats = t.Seats,
                        FloorAreaId = t.FloorAreaId,
                        FloorAreaName = t.FloorArea == null ? null : t.FloorArea.Name,
                        FloorAreaDisplayOrder = t.FloorArea == null ? null : t.FloorArea.DisplayOrder,
                        IsBookable = t.IsBookable,
                        Status = t.Status,
                        PhotoId = t.PhotoId!.Value,
                        PhotoX = t.PhotoX!.Value,
                        PhotoY = t.PhotoY!.Value,

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

                        // The booking the party sitting there came on, so their sitting is judged as that
                        // booking rather than as a walk-in in its way.
                        OpenSessionReservationId = db.TableSessions
                            .Where(s => s.DiningTableId == t.Id && s.ClosedAtUtc == null)
                            .Select(s => s.ReservationId)
                            .FirstOrDefault(),

                        // The left join. A table with none of these is not dropped - it is the
                        // most available table in the room. Served by
                        // IX_Reservations_DiningTableId_StartUtc_EndUtc.
                        Bookings = db.Reservations
                            .Where(r => r.DiningTableId == t.Id
                                        && r.Id != excludedId
                                        && (r.Status == ReservationStatus.Confirmed
                                            || r.Status == ReservationStatus.PendingApproval
                                            || r.Status == ReservationStatus.Seated)
                                        && (r.EndUtc > guardFromUtc || r.Status == ReservationStatus.Seated)
                                        && r.StartUtc < guardToUtc

                                        // Nor a booking only asking for the left-out booking's table: a
                                        // diner changing their booking is not held to its question, as
                                        // the move itself is not (TableEvening).
                                        && !db.TableShareRequests.Any(q => q.EarlierReservationId == excludedId
                                                                           && q.LaterReservationId == r.Id
                                                                           && q.Status == TableShareRequestStatus.Pending))
                            .OrderBy(r => r.StartUtc)
                            .Select(r => new AvailabilityBookingRow
                            {
                                ReservationId = r.Id,
                                StartUtc = r.StartUtc,
                                EndUtc = r.EndUtc,
                                Status = r.Status,
                                PromisedFreeByUtc = r.PromisedFreeByUtc,

                                // Whether its guest is already being asked to free the table for
                                // somebody - other than the booking being changed, which is not in its
                                // own way.
                                IsBeingAsked = db.TableShareRequests.Any(q => q.EarlierReservationId == r.Id
                                                                             && q.Status == TableShareRequestStatus.Pending
                                                                             && q.LaterReservationId != excludedId),
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
    /// The dates whose opening blocks could possibly apply - their weekdays' blocks and their special days.
    /// </summary>
    /// <remarks>
    /// The rule examines the local day the sitting starts on and the one before it, because a
    /// branch open 10:00-01:00 has a Friday block that runs into Saturday morning. When the diner
    /// named a date, that day and its predecessor are the whole answer. When they did not, the
    /// branch's local date can be a day either side of the UTC one, so the window widens by one in
    /// each direction - still fewer than a full week.
    /// </remarks>
    private static DateOnly[] RelevantDates(DateOnly? localDate, DateTime anchorUtc)
    {
        var anchorDate = DateOnly.FromDateTime(anchorUtc);

        // Named a date: the sitting is on that day, so that day and the evening before it.
        // Named none: the branch's local date can be a day either side of the UTC one, so widen
        // by one in each direction - still four days rather than a whole week.
        int[] offsets = localDate is null ? [-2, -1, 0, 1] : [-1, 0];

        return [.. offsets.Select(offset => anchorDate.AddDays(offset))];
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
                row.Hours, localDate.ToDateTime(localTime), zone.ToLocal(endUtc));

        var proposed = new BookedInterval(startUtc, endUtc);
        // Every reason knowable before booking: the slot is at short notice, the branch approves
        // everything by hand, or the party is over its threshold. It was the threshold alone once, so
        // a branch with auto-confirm off was promising an instant confirmation it was never going to
        // give - and tonight's slot is the floor's to decide, whatever the branch's settings.
        var needsApproval = ReservationRules.AwaitsApproval(
            request.PartySize, policy, startUtc, localDate, nowUtc, zone.LocalDateAt(nowUtc));

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
            LocalDate = localDate,
            LocalTime = localTime,
            PartySize = request.PartySize,
            RequestedStartUtc = startUtc,
            RequestedEndUtc = endUtc,
            TurnTimeMinutes = policy.TurnTimeMinutes,
            BufferMinutes = policy.BufferMinutes,
            CancellationDeadlineMinutes = policy.CancellationDeadlineMinutes,
            CancellationDeadlineUtc = ReservationRules.CancellationDeadline(startUtc, policy),
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
        // The booking that still matters at the requested instant: the earliest one not already over
        // by then - which, for a table that can only be asked for, is the one still sitting there.
        // Drives the derived state.
        var next = table.Bookings
            .Where(b => b.EndUtc > proposed.StartUtc)
            .MinBy(b => b.StartUtc);

        // The first one after the requested start, which is what bounds the window.
        var following = table.Bookings
            .Where(b => b.StartUtc > proposed.StartUtc)
            .MinBy(b => b.StartUtc);

        var answer = requestReason is { } refused
            ? new TableAnswer(refused, null)
            : Answer(table, proposed.StartUtc, policy, partySize, nowUtc);

        var reason = answer.Reason;
        var isAvailable = reason is null;
        var requestable = answer.IsRequestable;
        var mustFreeBy = isAvailable ? answer.Verdict?.MustFreeByUtc : null;

        // Null means no limit, and no limit is the answer a diner would rather have. The same
        // arithmetic the floor plan uses for "how long can I give this table away for?" - which is the
        // time to free it by when a full sitting would run into the next booking.
        var availableUntilUtc = isAvailable
            ? TableStateProjection.FreeUntil(following?.StartUtc, policy.BufferMinutes)
            : null;

        var windowMinutes = availableUntilUtc is null
            ? (int?)null
            : (int)(availableUntilUtc.Value - proposed.StartUtc).TotalMinutes;

        return new TableAvailability
        {
            TableId = table.TableId,
            Label = table.Label,
            Seats = table.Seats,
            FloorAreaId = table.FloorAreaId,
            FloorAreaName = table.FloorAreaName,
            FloorAreaDisplayOrder = table.FloorAreaDisplayOrder ?? 0,
            IsBookable = table.IsBookable,
            PhysicalStatus = table.Status,
            PhotoId = table.PhotoId,
            PhotoX = table.PhotoX,
            PhotoY = table.PhotoY,

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

            // A table that can only be asked for waits for somebody's yes, whatever else is true.
            RequiresApproval = needsApproval || requestable,
            IsRequestable = requestable,
            MustFreeByUtc = mustFreeBy,
            MustFreeByLocal = mustFreeBy is { } until ? zone.LocalTimeAt(until) : null,

            Window = isAvailable
                ? new TableAvailabilityWindow(
                    AvailableFromUtc: proposed.StartUtc,
                    AvailableUntilUtc: availableUntilUtc,
                    HasNoLaterBooking: following is null,
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
    /// Whether this table can take this party at this start - free, only once the guest before agrees,
    /// or not, and why - the one per-table judgement both reads make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule here is the one the booking writer applies inside the table's lock - the table's own
    /// rules, then <see cref="TableSequencing.Judge"/> over the same bookings, promises, open questions
    /// and sitting - so a table either read offers is a table a booking accepts, and one it says must be
    /// freed by 21:25 is one a booking must accept 21:25 for. The day read calls this for every quarter
    /// hour and the single-slot read for the one asked about; keeping it in one place is what stops the
    /// time picker striking a time the room screen would have offered a table at, or the reverse.
    /// </para>
    /// <para>
    /// The order is the order of the reasons a diner can act on: the table itself (does not take
    /// bookings, is broken, does not fit them), then the table's evening.
    /// </para>
    /// </remarks>
    private static TableAnswer Answer(
        AvailabilityTableRow table,
        DateTime startUtc,
        ReservationPolicy policy,
        int partySize,
        DateTime nowUtc)
    {
        // Physical status is checked by CheckTable only for the here and now; beyond the horizon it
        // would be a statement about tonight applied to tomorrow.
        var physicalReason = TableStateProjection.IsWithinPhysicalHorizon(startUtc, nowUtc)
            ? ReservationRules.CheckTable(table.Status, table.IsBookable, table.Seats, partySize, policy)
            : ReservationRules.CheckTable(
                table.Status == TableStatus.OutOfService ? TableStatus.OutOfService : TableStatus.Free,
                table.IsBookable,
                table.Seats,
                partySize,
                policy);

        if (physicalReason is not null)
        {
            return new TableAnswer(physicalReason, null);
        }

        var verdict = TableSequencing.Judge(
            startUtc,
            policy.TurnTimeMinutes,
            policy.BufferMinutes,
            table.Bookings.Select(b => new SequencedBooking(
                b.ReservationId, b.StartUtc, b.EndUtc, b.Status, b.PromisedFreeByUtc, b.IsBeingAsked)),
            table.OpenSessionSeatedAtUtc is { } seatedAtUtc
                ? new OpenSitting(seatedAtUtc, table.OpenSessionReservationId)
                : null);

        return new TableAnswer(verdict.Refusal, verdict);
    }

    /// <summary>
    /// Every quarter of an hour of the day whose sitting fits one opening block, each judged on its tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request-level rules are the single-slot read's, in the same terms: a time whose sitting
    /// does not fit one opening block is left out - it was never on offer - and a time too soon or
    /// too far out is listed, struck, with that reason, so the picker can say why. A time the clocks
    /// skip does not exist and is left out too.
    /// </para>
    /// <para>
    /// Everything else is <see cref="Answer"/> over the very rows the single-slot read judges.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The bookings by time only that may share a sitting with a time on the requested day: from the evening before
    /// to the morning after, wide enough for any turn time.
    /// </summary>
    private async Task<List<(BookedInterval Sitting, int Guests)>> TimeOnlyBookingsAsync(
        Guid branchId,
        DaySlotsRequest request,
        CancellationToken cancellationToken)
    {
        var dayUtc = DateTime.SpecifyKind(request.LocalDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var fromUtc = dayUtc - GuardWindow;
        var toUtc = dayUtc + GuardWindow;
        var excludedId = request.ExcludeReservationId ?? Guid.Empty;

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && r.BookedWithoutTable
                        && r.Id != excludedId
                        && GuestsAtOnce.Holding.Contains(r.Status)
                        && r.EndUtc > fromUtc
                        && r.StartUtc < toUtc)
            .Select(r => new { r.StartUtc, r.EndUtc, r.PartySize })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => (new BookedInterval(r.StartUtc, r.EndUtc), r.PartySize))];
    }

    private static BranchDaySlots ProjectDay(AvailabilityRow row, DaySlotsRequest request, DateTime nowUtc)
    {
        var zone = BranchZone.For(row.TimeZoneId);
        var policy = row.Policy;
        var localToday = zone.LocalDateAt(nowUtc);
        var hours = row.Hours;

        var slots = new List<DaySlot>();

        for (var minutes = 0; minutes < MinutesPerDay; minutes += DaySlotStep.Minutes)
        {
            var localTime = new TimeOnly(minutes / 60, minutes % 60);

            if (!zone.TryToUtc(request.LocalDate, localTime, out var startUtc))
            {
                continue;
            }

            var endUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);

            if (ReservationRules.CheckOpeningHours(
                    hours, request.LocalDate.ToDateTime(localTime), zone.ToLocal(endUtc)) is not null)
            {
                continue;
            }

            var time = localTime.ToString("HH:mm", CultureInfo.InvariantCulture);

            // Knowable before booking, and asked per time: see Project. Today's times wait for the
            // floor, and so do tomorrow's under six hours away; the rest are confirmed on the spot.
            var needsApproval = ReservationRules.AwaitsApproval(
                request.PartySize, policy, startUtc, request.LocalDate, nowUtc, localToday);

            if (ReservationRules.CheckTiming(startUtc, nowUtc, request.LocalDate, localToday, policy) is { } timing)
            {
                slots.Add(new DaySlot
                {
                    Time = time,
                    StartUtc = startUtc,
                    IsAvailable = false,
                    RequiresApproval = needsApproval,
                    FreeTableCount = 0,
                    UnavailableReason = timing,
                });

                continue;
            }

            if (policy.BookingMode == BookingMode.TimeOnly)
            {
                var left = GuestsAtOnce.LeftWithin(
                    row.TimeOnlyBookings, new BookedInterval(startUtc, endUtc), policy.GuestsAtOnce ?? 0);
                var fits = left >= request.PartySize;

                slots.Add(new DaySlot
                {
                    Time = time,
                    StartUtc = startUtc,
                    IsAvailable = fits,
                    RequiresApproval = needsApproval,
                    FreeTableCount = 0,
                    GuestsLeft = left,
                    UnavailableReason = fits ? null : ReservationRejectionReason.TimeFull,
                });

                continue;
            }

            var answers = row.Tables
                .Select(t => Answer(t, startUtc, policy, request.PartySize, nowUtc))
                .ToList();
            var free = answers.Count(a => a.IsFree);
            var requestable = answers.Count(a => a.IsRequestable);
            var available = answers.Where(a => a.IsAvailable).ToList();

            // "Until 21:25" only when no table on offer gives a full sitting; then the most generous.
            var mustFreeBy = available.Count > 0 && available.All(a => a.Verdict!.MustFreeByUtc is not null)
                ? available.Max(a => a.Verdict!.MustFreeByUtc)
                : null;

            slots.Add(new DaySlot
            {
                Time = time,
                StartUtc = startUtc,
                IsAvailable = free + requestable > 0,
                IsRequestable = free == 0 && requestable > 0,
                RequiresApproval = needsApproval || (free == 0 && requestable > 0),
                FreeTableCount = free,
                RequestableTableCount = requestable,
                MustFreeByUtc = mustFreeBy,
                MustFreeByLocal = mustFreeBy is { } until
                    ? zone.LocalTimeAt(until).ToString("HH:mm", CultureInfo.InvariantCulture)
                    : null,
                UnavailableReason = free + requestable > 0 ? null : StruckReason(answers.Select(a => a.Reason).ToList()),
            });
        }

        return new BranchDaySlots
        {
            BranchId = row.BranchId,
            Date = request.LocalDate,
            PartySize = request.PartySize,
            StepMinutes = DaySlotStep.Minutes,
            UnavailableReason = DayReason(slots),
            SpecialDay = hours.SpecialDayOn(request.LocalDate) is { } special ? SpecialOpeningDayView.From(special) : null,
            Slots = slots,
        };
    }

    /// <summary>
    /// The one reason to give for a time with no free table, out of every table's own.
    /// </summary>
    /// <remarks>
    /// "Taken" before anything else: when tables that would seat the party are booked or sat at, that
    /// is the fact that sends a diner to another time - even in a room of two-seaters that do not fit
    /// them. Otherwise the commonest reason, which for a party of twelve is that nothing seats them.
    /// Ties go to the lower reason, so the answer is the same on every call. A branch with no table on
    /// offer at all has nothing that takes bookings.
    /// </remarks>
    private static ReservationRejectionReason StruckReason(IReadOnlyCollection<ReservationRejectionReason?> reasons)
    {
        static ReservationRejectionReason? Commonest(IEnumerable<ReservationRejectionReason> candidates) =>
            candidates
                .GroupBy(r => r)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => (ReservationRejectionReason?)g.Key)
                .FirstOrDefault();

        var known = reasons.OfType<ReservationRejectionReason>().ToList();

        return Commonest(known.Where(r => r is ReservationRejectionReason.TableAlreadyBooked
                             or ReservationRejectionReason.TableCurrentlyOccupied
                             or ReservationRejectionReason.TableJustBooked
                             or ReservationRejectionReason.NextBookingTooSoon
                             or ReservationRejectionReason.EarlierGuestBeingAsked))
               ?? Commonest(known)
               ?? ReservationRejectionReason.TableNotBookable;
    }

    /// <summary>
    /// Why the whole day is refused, or null when any time on it was judged on its tables.
    /// </summary>
    /// <remarks>
    /// No time fits an opening block: shut all day. Every listed time refused for the same timing
    /// reason: a day already past, the rest of today inside the lead time, or a date beyond the
    /// booking window.
    /// </remarks>
    private static ReservationRejectionReason? DayReason(IReadOnlyList<DaySlot> slots)
    {
        if (slots.Count == 0)
        {
            return ReservationRejectionReason.OutsideOpeningHours;
        }

        var first = slots[0].UnavailableReason;

        return first is ReservationRejectionReason.LeadTimeTooShort or ReservationRejectionReason.OutsideBookingWindow
               && slots.All(s => s.UnavailableReason == first)
            ? first
            : null;
    }

    /// <summary>
    /// The answer when the requested local time does not exist at all, so no slot can be computed.
    /// Every pinned table is still listed - the room's photos are still drawn - each labelled with the one
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
            LocalDate = localDate,
            LocalTime = localTime,
            PartySize = request.PartySize,
            RequestedStartUtc = nowUtc,
            RequestedEndUtc = nowUtc,
            TurnTimeMinutes = row.Policy.TurnTimeMinutes,
            BufferMinutes = row.Policy.BufferMinutes,
            CancellationDeadlineMinutes = row.Policy.CancellationDeadlineMinutes,

            // No slot, so no deadline to state. Absent rather than a made-up instant.
            CancellationDeadlineUtc = null,
            UnavailableReason = reason,
            AsOfUtc = nowUtc,
            Tables = row.Tables
                .Select(t => new TableAvailability
                {
                    TableId = t.TableId,
                    Label = t.Label,
                    Seats = t.Seats,
                    FloorAreaId = t.FloorAreaId,
                    FloorAreaName = t.FloorAreaName,
                    FloorAreaDisplayOrder = t.FloorAreaDisplayOrder ?? 0,
                    IsBookable = t.IsBookable,
                    PhysicalStatus = t.Status,
                    PhotoId = t.PhotoId,
                    PhotoX = t.PhotoX,
                    PhotoY = t.PhotoY,
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

        /// <summary>The owned policy, carried whole so the rules get every threshold they need.</summary>
        public ReservationPolicy Policy { get; init; } = null!;

        public List<OpeningHours> OpeningHours { get; init; } = [];

        public DateOnly SpecialFrom { get; set; }

        public DateOnly SpecialTo { get; set; }

        /// <summary>Set after the statement, from the second one.</summary>
        public IReadOnlyList<SpecialDay> SpecialDays { get; set; } = [];

        public List<AvailabilityTableRow> Tables { get; init; } = [];

        /// <summary>For a branch that books by time only: its bookings around the day, read beside the statement.</summary>
        public List<(BookedInterval Sitting, int Guests)> TimeOnlyBookings { get; set; } = [];

        /// <summary>The week and the special days read beside it, as the one calendar every rule asks.</summary>
        public OpeningCalendar Hours => new(
            OpeningHours.Select(h => (h.Day, new OpeningBlock(h.OpensAt, h.ClosesAt, h.ClosesNextDay))),
            SpecialDays,
            SpecialFrom,
            SpecialTo);
    }

    private sealed class AvailabilityTableRow
    {
        public Guid TableId { get; init; }

        public string Label { get; init; } = null!;

        public int Seats { get; init; }

        public Guid? FloorAreaId { get; init; }

        public string? FloorAreaName { get; init; }

        public int? FloorAreaDisplayOrder { get; init; }

        public bool IsBookable { get; init; }

        public TableStatus Status { get; init; }

        public Guid PhotoId { get; init; }

        public double PhotoX { get; init; }

        public double PhotoY { get; init; }

        /// <summary>When the party currently at this table sat down. Null when nobody is at it.</summary>
        public DateTime? OpenSessionSeatedAtUtc { get; init; }

        public Guid? OpenSessionId { get; init; }

        public Guid? OpenSessionReservationId { get; init; }

        public List<AvailabilityBookingRow> Bookings { get; init; } = [];
    }

    private sealed class AvailabilityBookingRow
    {
        public Guid ReservationId { get; init; }

        public DateTime StartUtc { get; init; }

        public DateTime EndUtc { get; init; }

        public ReservationStatus Status { get; init; }

        public DateTime? PromisedFreeByUtc { get; init; }

        public bool IsBeingAsked { get; init; }
    }

    /// <summary>
    /// One table's answer at one start: the table's own reason it cannot be had (it takes no bookings, is
    /// broken, does not fit the party), or the table's evening's verdict.
    /// </summary>
    private sealed record TableAnswer(ReservationRejectionReason? Reason, TableSequencingVerdict? Verdict)
    {
        public bool IsFree => Reason is null && Verdict is { IsFree: true };

        public bool IsRequestable => Reason is null && Verdict is { NeedsEarlierGuest: true };

        public bool IsAvailable => Reason is null;
    }
}
