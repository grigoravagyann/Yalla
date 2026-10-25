using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Reservations;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The console's History: the feed, its CSV, and one subject's timeline. Reads only. See <c>docs/activity.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may read.</b> A manager or above at the branch, by the stored row (<see cref="StaffBranchGuard"/>): a branch
/// manager their own branch only, the owner and the general manager every branch of their venue, a platform admin any.
/// Waiters and the kitchen: 403 - their own numbers are <c>/api/staff/me/performance</c>.
/// </para>
/// <para>
/// <b>Venue-wide entries</b> - a change to an owner's account, which names no branch - are on every branch's feed for
/// somebody who covers the whole venue, and on none for a branch manager.
/// </para>
/// <para>
/// <b>One statement per page</b>: the window and the cursor are a range on <c>IX_ActivityEntries_BranchId_OccurredAtUtc</c>,
/// newest first, then by id, so equal instants neither repeat nor skip across pages - and for somebody venue-wide a
/// second, on the filtered <c>IX_ActivityEntries_VenueId_OccurredAtUtc</c> (branch-less entries only), merged in memory.
/// </para>
/// </remarks>
internal sealed class ActivityHistoryQuery(YallaDbContext db, IClock clock, StaffBranchGuard branchGuard) : IActivityQuery
{
    public async Task<ActivityPage> ListAsync(Guid branchId, ActivityFeedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (scope, zone, filter) = await PrepareAsync(branchId, query, "Read the history", cancellationToken);

        var rows = await ReadAsync(scope, zone, filter, withCursor: true, filter.Limit + 1, cancellationToken);

        var shown = rows.Take(filter.Limit).ToList();
        var next = rows.Count > filter.Limit
            ? new ActivityCursor(shown[^1].OccurredAtUtc, shown[^1].Id).Encode()
            : null;

        return new ActivityPage([.. shown.Select(e => ToItem(e, zone))], next, filter.From, filter.To);
    }

    public async Task<ActivityExport> ExportAsync(Guid branchId, ActivityFeedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The cursor and the page size are the screen's; a file is the whole window.
        var (scope, zone, filter) = await PrepareAsync(
            branchId, query with { Cursor = null, Limit = null }, "Export the history", cancellationToken);

        var rows = await ReadAsync(
            scope, zone, filter, withCursor: false, ActivityFeedRules.MaxExportRows + 1, cancellationToken);

        if (rows.Count > ActivityFeedRules.MaxExportRows)
        {
            throw new ActivityExportTooLargeException(rows.Count);
        }

        return new ActivityExport([.. rows.Select(e => ToItem(e, zone))], filter.From, filter.To);
    }

    public async Task<ActivityTimeline> TimelineAsync(
        Guid branchId, string subjectType, Guid subjectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);

        var scope = await ScopeAsync(branchId, "Read a timeline", cancellationToken);
        var zone = BranchZone.For(scope.TimeZoneId);
        var type = subjectType.Trim().ToLowerInvariant();

        // The console's word for a takeaway order; the entries say takeaway.
        if (type == "takeawayorder")
        {
            type = ActivitySubjects.Takeaway;
        }

        TabTimelineHeader? tabHeader = null;
        BookingTimelineHeader? booking = null;
        TakeawayTimelineHeader? takeaway = null;
        IReadOnlyList<TimelineLine> lines = [];
        IQueryable<ActivityEntry> entries;

        switch (type)
        {
            case ActivitySubjects.Tab:
                (tabHeader, lines) = await TabAsync(branchId, subjectId, cancellationToken);
                var sessionId = tabHeader.TableSessionId;

                entries = db.ActivityEntries.Where(e => e.BranchId == branchId
                                                        && (e.TableSessionId == sessionId
                                                            || (e.SubjectType == ActivitySubjects.Tab && e.SubjectId == subjectId)));
                break;

            case ActivitySubjects.Reservation:
                booking = await BookingAsync(branchId, subjectId, cancellationToken);
                entries = db.ActivityEntries.Where(e => e.BranchId == branchId
                                                        && e.SubjectType == ActivitySubjects.Reservation
                                                        && e.SubjectId == subjectId);
                break;

            case ActivitySubjects.Takeaway:
                (takeaway, lines) = await TakeawayAsync(branchId, subjectId, cancellationToken);
                entries = db.ActivityEntries.Where(e => e.BranchId == branchId
                                                        && e.SubjectType == ActivitySubjects.Takeaway
                                                        && e.SubjectId == subjectId);
                break;

            default:
                entries = db.ActivityEntries.Where(e => (e.BranchId == branchId
                                                         || (scope.VenueWide && e.BranchId == null && e.VenueId == scope.VenueId))
                                                        && e.SubjectType == type
                                                        && e.SubjectId == subjectId);
                break;
        }

        var rows = await entries
            .AsNoTracking()
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        // A subject with no header has nothing to show but its entries; none at all is a subject this branch never saw.
        if (rows.Count == 0 && tabHeader is null && booking is null && takeaway is null)
        {
            throw new KeyNotFoundException($"Nothing at this branch is recorded about {type} {subjectId}.");
        }

        var people = rows
            .Where(e => e.StaffMemberId is not null && e.ActorName is not null)
            .GroupBy(e => e.StaffMemberId!.Value)
            .Select(g => new ActivityPerson(g.Key, g.Last().ActorName!))
            .ToList();

        return new ActivityTimeline(
            type, subjectId, tabHeader, booking, takeaway, lines, [.. rows.Select(e => ToItem(e, zone))], people);
    }

    // ---------------------------------------------------------------- the feed

    private async Task<(Scope Scope, BranchZone Zone, ActivityFeedFilter Filter)> PrepareAsync(
        Guid branchId, ActivityFeedQuery query, string operation, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(branchId, operation, cancellationToken);
        var zone = BranchZone.For(scope.TimeZoneId);
        var filter = ActivityFeedRules.Check(query, zone.LocalDateAt(clock.UtcNow));

        return (scope, zone, filter);
    }

    /// <summary>
    /// The newest <paramref name="take"/> entries of the window, newest first then by id.
    /// </summary>
    /// <remarks>
    /// For somebody venue-wide the branch's entries and the venue's branch-less ones are two reads, each an ordered
    /// range on its own index that stops after <paramref name="take"/> rows, merged here - not one <c>OR</c>, which
    /// SQL Server answers by collecting and sorting the whole window (a year of a busy branch) for every page. The
    /// merge orders the id as SQL Server does (<see cref="SqlGuid"/>), so the cursor's tie-break is the database's.
    /// </remarks>
    private async Task<List<ActivityEntry>> ReadAsync(
        Scope scope, BranchZone zone, ActivityFeedFilter filter, bool withCursor, int take, CancellationToken cancellationToken)
    {
        var branchId = scope.BranchId;
        var venueId = scope.VenueId;

        var rows = await Filtered(db.ActivityEntries.Where(e => e.BranchId == branchId), zone, filter, withCursor)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (!scope.VenueWide)
        {
            return rows;
        }

        var venueWide = await Filtered(
                db.ActivityEntries.Where(e => e.BranchId == null && e.VenueId == venueId), zone, filter, withCursor)
            .Take(take)
            .ToListAsync(cancellationToken);

        return venueWide.Count == 0
            ? rows
            :
            [
                .. rows.Concat(venueWide)
                    .OrderByDescending(e => e.OccurredAtUtc)
                    .ThenByDescending(e => new SqlGuid(e.Id))
                    .Take(take),
            ];
    }

    private static IQueryable<ActivityEntry> Filtered(
        IQueryable<ActivityEntry> source, BranchZone zone, ActivityFeedFilter filter, bool withCursor)
    {
        var fromUtc = zone.ToUtcLenient(filter.From, TimeOnly.MinValue);
        var toUtc = zone.ToUtcLenient(filter.To.AddDays(1), TimeOnly.MinValue);

        var entries = source
            .AsNoTracking()
            .Where(e => e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc < toUtc);

        if (filter.StaffMemberId is { } staffMemberId)
        {
            entries = entries.Where(e => e.StaffMemberId == staffMemberId);
        }

        if (filter.Category is { } category)
        {
            entries = entries.Where(e => e.Category == category);
        }

        if (filter.ActorKind is { } actorKind)
        {
            entries = entries.Where(e => e.ActorKind == actorKind);
        }

        if (filter.Search is { } search)
        {
            // Lowered on both sides rather than trusting the column's collation - Armenian and Cyrillic included. The
            // window is one branch's, so nothing here needs an index of its own.
            var lowered = search.ToLowerInvariant();
            var code = filter.SearchAsTakeawayCode;

            entries = entries.Where(e => (e.TableLabel != null && e.TableLabel.ToLower() == lowered)
                                         || (e.GuestName != null && e.GuestName.ToLower().Contains(lowered))
                                         || (e.SubjectCode != null && (e.SubjectCode == code || e.SubjectCode.ToLower() == lowered))
                                         || (e.ItemNames != null && e.ItemNames.ToLower().Contains(lowered)));
        }

        if (withCursor && filter.Cursor is { } cursor)
        {
            var atUtc = cursor.OccurredAtUtc;
            var afterId = cursor.EntryId;

            // Strictly after the last row shown, in the list's own order - the id breaks a tie of instants, compared as the
            // database orders it.
            entries = entries.Where(e => e.OccurredAtUtc < atUtc || (e.OccurredAtUtc == atUtc && e.Id.CompareTo(afterId) < 0));
        }

        return entries.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id);
    }

    // ---------------------------------------------------------------- the subjects

    private async Task<(TabTimelineHeader Header, IReadOnlyList<TimelineLine> Lines)> TabAsync(
        Guid branchId, Guid tabId, CancellationToken cancellationToken)
    {
        var tab = await db.Tabs
                      .AsNoTracking()
                      .Where(t => t.Id == tabId && t.BranchId == branchId)
                      .Select(t => new
                      {
                          t.Id,
                          t.TableSessionId,
                          t.DiningTable.Label,
                          t.TableSession.PartySize,
                          t.TableSession.SeatedAtUtc,
                          t.TableSession.SeatedByStaffId,
                          t.TableSession.ServerStaffMemberId,
                          t.TableSession.ServerAssignedAtUtc,
                          t.OpenedAtUtc,
                          t.ClosedAtUtc,
                          t.Status,
                          t.SubtotalAmd,
                          t.ServiceChargeAmd,
                          t.TotalAmd,
                          t.PaidAmd,
                          Tips = t.Payments.Where(p => p.Status == PaymentStatus.Succeeded).Sum(p => (long?)p.TipAmd) ?? 0L,
                      })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {tabId} was not found at this branch.");

        var lines = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => l.TabOrder.TabId == tabId)
            .OrderBy(l => l.TabOrder.PlacedAtUtc)
            .ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TabOrderId,
                l.NameSnapshot,
                l.Quantity,
                l.UnitPriceAmdSnapshot,
                l.VoidedAtUtc,
                l.VoidedByStaffId,
                VoidedBy = l.VoidedByStaff != null ? l.VoidedByStaff.FullName : null,
                l.VoidReason,
                l.TabOrder.PlacedAtUtc,
                l.TabOrder.PlacedByStaffId,
                PlacedBy = l.TabOrder.PlacedByStaff != null ? l.TabOrder.PlacedByStaff.FullName : null,
                Guest = l.TabOrder.PlacedByParticipant != null ? l.TabOrder.PlacedByParticipant.DisplayName : null,
                l.TabOrder.ServerStaffMemberId,
            })
            .ToListAsync(cancellationToken);

        // The table's waiters in turn: whoever held it first (the seat, the opener, the first order) from when it became
        // theirs, then each transfer.
        var transfers = await db.ActivityEntries
            .AsNoTracking()
            .Where(e => e.TableSessionId == tab.TableSessionId && e.Action == ActivityActions.TableTransferred)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => new { e.OccurredAtUtc, e.SummaryJson })
            .ToListAsync(cancellationToken);

        var turns = new List<(Guid Id, DateTime FromUtc)>();

        // Before the first hand-over the table was whoever that hand-over was from - nobody, when a manager gave out a
        // table a guest's scan seated that nobody had served yet. With no hand-over, whoever holds it now.
        var firstServer = transfers.Count > 0
            ? FirstServer(transfers[0].SummaryJson)
            : tab.ServerStaffMemberId
              ?? tab.SeatedByStaffId
              ?? lines.Select(l => l.ServerStaffMemberId).FirstOrDefault(s => s is not null);

        if (firstServer is { } first)
        {
            // From when it became theirs: the seat when they seated it; otherwise when they opened, let in or first
            // ordered on a table a guest's scan or booking had seated - not the guest's own arrival.
            DateTime firstFrom;

            if (first == tab.SeatedByStaffId)
            {
                firstFrom = tab.SeatedAtUtc;
            }
            else if (transfers.Count == 0 && tab.ServerAssignedAtUtc is { } assigned)
            {
                firstFrom = assigned;
            }
            else
            {
                var claimed = await db.ActivityEntries
                    .AsNoTracking()
                    .Where(e => e.TableSessionId == tab.TableSessionId
                                && e.StaffMemberId == first
                                && (e.Action == ActivityActions.TableTabOpened
                                    || e.Action == ActivityActions.TableTabAdmitted
                                    || e.Action == ActivityActions.OrderSent
                                    || e.Action == ActivityActions.OrderPreOrderSent))
                    .MinAsync(e => (DateTime?)e.OccurredAtUtc, cancellationToken);

                firstFrom = claimed
                            ?? lines.Where(l => l.ServerStaffMemberId == first).Select(l => (DateTime?)l.PlacedAtUtc).Min()
                            ?? tab.SeatedAtUtc;

                if (transfers.Count > 0 && firstFrom > transfers[0].OccurredAtUtc)
                {
                    firstFrom = transfers[0].OccurredAtUtc;
                }
            }

            turns.Add((first, firstFrom));
        }

        foreach (var transfer in transfers)
        {
            if (ToServer(transfer.SummaryJson) is { } to)
            {
                turns.Add((to, transfer.OccurredAtUtc));
            }
        }

        var names = await NamesAsync(turns.Select(t => t.Id), cancellationToken);

        var header = new TabTimelineHeader(
            tab.Id,
            tab.TableSessionId,
            tab.Label,
            tab.PartySize,
            tab.OpenedAtUtc,
            tab.SeatedAtUtc,
            tab.ClosedAtUtc,
            (int)tab.Status,
            tab.SubtotalAmd,
            tab.ServiceChargeAmd,
            tab.TotalAmd,
            tab.PaidAmd,
            tab.Tips,
            [.. turns.Select(t => new TimelineServer(t.Id, names.GetValueOrDefault(t.Id) ?? string.Empty, t.FromUtc))]);

        return (header,
        [
            .. lines.Select(l => new TimelineLine(
                l.Id,
                l.TabOrderId,
                l.NameSnapshot,
                l.Quantity,
                l.UnitPriceAmdSnapshot,
                // The bill's one rule: a line counts until it is voided. Voiding a whole order voids its lines
                // (TabOrder.Void), so its lines read 0 here as they do on the bill - never a second rule of this page's.
                l.VoidedAtUtc is null ? l.UnitPriceAmdSnapshot * l.Quantity : 0L,
                l.PlacedAtUtc,
                l.PlacedByStaffId,
                l.PlacedBy ?? l.Guest,
                l.PlacedByStaffId is null,
                l.ServerStaffMemberId,
                l.VoidedAtUtc,
                l.VoidedByStaffId,
                l.VoidedBy,
                l.VoidReason)),
        ]);
    }

    private async Task<BookingTimelineHeader> BookingAsync(Guid branchId, Guid reservationId, CancellationToken cancellationToken)
    {
        var found = await db.Reservations
                        .AsNoTracking()
                        .Where(r => r.Id == reservationId && r.BranchId == branchId)
                        .Select(r => new
                        {
                            r.Id,
                            r.Code,
                            r.GuestName,
                            r.PartySize,
                            r.LocalDate,
                            r.LocalStartTime,
                            r.Status,
                            r.DiningTable.Label,
                            TabId = db.TableSessions.Where(s => s.ReservationId == r.Id).Select(s => s.TabId).FirstOrDefault(),
                        })
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new KeyNotFoundException($"Booking {reservationId} was not found at this branch.");

        return new BookingTimelineHeader(
            found.Id,
            found.Code,
            found.GuestName,
            found.PartySize,
            found.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            found.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            (int)found.Status,
            found.Label,
            found.TabId);
    }

    private async Task<(TakeawayTimelineHeader Header, IReadOnlyList<TimelineLine> Lines)> TakeawayAsync(
        Guid branchId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.TakeawayOrders
                        .AsNoTracking()
                        .Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.Id == orderId && o.BranchId == branchId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Takeaway order {orderId} was not found at this branch.");

        var header = new TakeawayTimelineHeader(
            order.Id,
            order.Code,
            order.GuestName,
            (int)order.Status,
            order.TotalAmd,
            order.PaidAmountAmd,
            (int?)order.PaymentMethod,
            order.CreatedAtUtc,
            order.CollectedAtUtc);

        return (header,
        [
            .. order.Lines.OrderBy(l => l.Position).Select(l => new TimelineLine(
                l.Id, order.Id, l.NameSnapshot, l.Quantity, l.UnitPriceAmdSnapshot, l.LineTotalAmd, order.CreatedAtUtc,
                null, order.GuestName, true, null, null, null, null, null)),
        ]);
    }

    private static Guid? FirstServer(string? transferSummary) => GuidIn(transferSummary, "fromStaffMemberId");

    private static Guid? ToServer(string? transferSummary) => GuidIn(transferSummary, "toStaffMemberId");

    private static Guid? GuidIn(string? json, string property)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
               && value.TryGetGuid(out var id)
            ? id
            : null;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();

        return wanted.Count == 0
            ? []
            : await db.StaffMembers
                .AsNoTracking()
                .Where(s => wanted.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.FullName, cancellationToken);
    }

    // ---------------------------------------------------------------- scope and shape

    internal sealed record Scope(Guid BranchId, Guid VenueId, string TimeZoneId, bool VenueWide);

    /// <summary>The caller at the branch (403), the branch (404), and whether they cover the whole venue.</summary>
    private async Task<Scope> ScopeAsync(Guid branchId, string operation, CancellationToken cancellationToken)
    {
        var staffId = await branchGuard.RequireAtBranchAsync(branchId, operation, cancellationToken);

        var branch = await db.Branches
                         .AsNoTracking()
                         .Where(b => b.Id == branchId)
                         .Select(b => new { b.VenueId, b.TimeZoneId })
                         .FirstOrDefaultAsync(cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var role = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => s.Role)
            .FirstAsync(cancellationToken);

        return new Scope(
            branchId,
            branch.VenueId,
            branch.TimeZoneId,
            role == StaffRole.PlatformAdmin || StaffRoleRules.CoversWholeVenue(role));
    }

    internal static ActivityItem ToItem(ActivityEntry entry, BranchZone zone)
    {
        var summary = Parse(entry.SummaryJson) ?? Parse("{}")!.Value;

        return new ActivityItem(
            entry.Id,
            entry.OccurredAtUtc,
            zone.ToLocal(entry.OccurredAtUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ActivityWords.Of(entry.ActorKind),
            entry.StaffMemberId,
            entry.ActorName,
            entry.DeviceId,
            entry.DeviceLabel,
            ActivityWords.Of(entry.Category),
            entry.Action,
            entry.SubjectType,
            entry.SubjectId,
            entry.SubjectCode,
            entry.TableSessionId,
            entry.TableLabel,
            entry.GuestName,
            entry.ItemNames,
            entry.AmountAmd,
            entry.IsMoney,
            Parse(entry.BeforeJson),
            Parse(entry.AfterJson),
            entry.Reason,
            summary,
            ActivityFeedRules.SecondsOf(summary));
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
