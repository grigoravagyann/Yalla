using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Staff → Results, a person's page, and a waiter's own numbers - read live off the operational tables, as the reports
/// are. See <c>docs/activity.md</c> §performance.
/// </summary>
/// <remarks>
/// <para>
/// <b>The money is the revenue report's.</b> The bills are the same rows by the same rule (closed in the window on the
/// branch's own dates, admitted, <c>TotalAmd</c>), the cash the same payments and the same takeaway counter, the
/// adjustments the same ones by the same arithmetic - so the totals row says what the Reports screen says for the same
/// dates, to the dram. What this adds is whose each part was.
/// </para>
/// <para>
/// <b>Whose a sale is.</b> Each order carries the table's waiter when it was placed (<c>TabOrder.ServerStaffMemberId</c>);
/// a bill's total is split over its waiters in the proportion of their lines, the dram left over going to the largest
/// remainders, so the shares add up to the bill. Lines placed while nobody served the table are the waiter's at the
/// close; with nobody then either, the sale is the branch's and nobody's (<c>unattributedSalesAmd</c>). A table and its
/// guests count for each waiter who had a share of it or held it at the close - which is why the totals row counts the
/// bills once rather than adding the rows up.
/// </para>
/// <para>
/// <b>A fixed number of statements</b>, whatever the window or the number of people: one per source, each a range on the
/// table's own index, grouped in the database where it can be and in memory where the arithmetic is the bill's.
/// </para>
/// </remarks>
internal sealed class StaffPerformanceReader(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    StaffBranchGuard branchGuard) : IStaffPerformanceQuery
{
    public async Task<StaffPerformanceReport> GetBranchAsync(
        Guid branchId, StaffPerformanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await branchGuard.RequireAtBranchAsync(branchId, "Read the staff results", cancellationToken);

        var branch = await BranchAsync(branchId, cancellationToken);
        var filter = StaffPerformanceRules.Check(query, BranchZone.For(branch.TimeZoneId).LocalDateAt(clock.UtcNow));

        return await ComputeAsync(branchId, branch.TimeZoneId, filter.From, filter.To, cancellationToken);
    }

    public async Task<PersonPerformance> GetPersonAsync(
        Guid branchId, Guid staffMemberId, StaffPerformanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await branchGuard.RequireAtBranchAsync(branchId, "Read a person's results", cancellationToken);

        var branch = await BranchAsync(branchId, cancellationToken);

        // Somebody on this venue's staff - anybody else is not found, whatever they did.
        if (!await db.StaffMembers.AnyAsync(s => s.Id == staffMemberId && s.VenueId == branch.VenueId, cancellationToken))
        {
            throw new KeyNotFoundException($"Staff member {staffMemberId} does not work at this venue.");
        }

        var filter = StaffPerformanceRules.Check(query, BranchZone.For(branch.TimeZoneId).LocalDateAt(clock.UtcNow));

        return await PersonAsync(branchId, branch.TimeZoneId, staffMemberId, filter, cancellationToken);
    }

    public async Task<PersonPerformance> GetMineAsync(StaffPerformanceQuery query, CancellationToken cancellationToken = default)
    {
        var mine = await MineAsync(query, cancellationToken);

        return await PersonAsync(mine.BranchId, mine.TimeZoneId, mine.StaffMemberId, mine.Filter, cancellationToken);
    }

    public async Task<MyResults> GetMyResultsAsync(StaffPerformanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Every list's first page: no list to choose and no cursor to continue - "more" is getMyPerformance's.
        var mine = await MineAsync(query with { List = null, Cursor = null }, cancellationToken);
        var filter = string.IsNullOrWhiteSpace(query.Limit)
            ? mine.Filter with { Limit = StaffPerformanceRules.ResultsDefaultLimit }
            : mine.Filter;

        var (row, daily) = await FiguresAsync(mine.BranchId, mine.TimeZoneId, mine.StaffMemberId, filter, cancellationToken);

        Task<RecentPage> Page(RecentList list) =>
            RecentAsync(mine.BranchId, mine.TimeZoneId, mine.StaffMemberId, filter with { List = list }, cancellationToken);

        // One context: the pages one after the other.
        var recent = new RecentLists(
            await Page(RecentList.Orders),
            await Page(RecentList.Messages),
            await Page(RecentList.Bookings),
            await Page(RecentList.Cash),
            await Page(RecentList.Calls));

        return new MyResults(mine.BranchId, filter.From, filter.To, row, daily, recent);
    }

    /// <summary>The caller, their branch and the query checked: what both "my own" reads start from.</summary>
    private sealed record Mine(Guid BranchId, string TimeZoneId, Guid StaffMemberId, StaffPerformanceFilter Filter);

    private async Task<Mine> MineAsync(StaffPerformanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        const string Operation = "Read your own results";

        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } staffId)
        {
            throw new StaffPermissionException(Operation, actor.Role, StaffRole.Waiter);
        }

        // The stored row: somebody who serves - a waiter or above at a venue. The kitchen has no tables, and the
        // platform has no results of its own.
        var me = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.VenueId, s.BranchId, s.Role, s.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (me is not { IsActive: true } || me.Role is not (StaffRole.Waiter or StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner))
        {
            throw new StaffPermissionException(Operation, me?.Role ?? actor.Role, StaffRole.Waiter);
        }

        // The branch: their own; a venue-wide owner or general manager names one of the venue's.
        // The branch named, when it parses; a malformed one is named in the 422 below with the rest.
        Guid? named = Guid.TryParse(query.BranchId?.Trim(), out var parsed) && parsed != Guid.Empty ? parsed : null;

        if (!string.IsNullOrWhiteSpace(query.BranchId) && named is null)
        {
            StaffPerformanceRules.Check(query, DateOnly.FromDateTime(clock.UtcNow));
        }

        var branchId = me.BranchId ?? named
                       ?? throw new Domain.FieldValidationException(
                       [
                           new Domain.FieldViolation(
                               StaffPerformanceRules.Fields.BranchId,
                               "You work across the venue: say which branch, branchId.",
                               Domain.FieldBounds.Required),
                       ]);

        if (named is { } other && other != branchId)
        {
            throw new StaffBranchScopeException("branch");
        }

        var branch = await BranchAsync(branchId, cancellationToken);

        if (branch.VenueId != me.VenueId)
        {
            throw new StaffBranchScopeException("branch");
        }

        // Checked again against the branch's own today, now its zone is known.
        var filter = StaffPerformanceRules.Check(query, BranchZone.For(branch.TimeZoneId).LocalDateAt(clock.UtcNow));

        return new Mine(branchId, branch.TimeZoneId, staffId, filter);
    }

    // ---------------------------------------------------------------- one person

    private async Task<PersonPerformance> PersonAsync(
        Guid branchId, string timeZoneId, Guid staffMemberId, StaffPerformanceFilter filter, CancellationToken cancellationToken)
    {
        var (row, daily) = await FiguresAsync(branchId, timeZoneId, staffMemberId, filter, cancellationToken);

        return new PersonPerformance(
            branchId, filter.From, filter.To, row, daily, await RecentAsync(branchId, timeZoneId, staffMemberId, filter, cancellationToken));
    }

    /// <summary>A person's row over the window and their chart: a person's page without its list.</summary>
    private async Task<(StaffPerformanceRow Row, List<DailySales> Daily)> FiguresAsync(
        Guid branchId, string timeZoneId, Guid staffMemberId, StaffPerformanceFilter filter, CancellationToken cancellationToken)
    {
        var report = await ComputeAsync(branchId, timeZoneId, filter.From, filter.To, cancellationToken, onlyFor: staffMemberId);
        var row = report.Staff.FirstOrDefault(r => r.StaffMemberId == staffMemberId) ?? await EmptyRowAsync(staffMemberId, cancellationToken);

        // The chart: a week ending on `to` at least, the whole window when it is longer.
        var chartFrom = filter.From < filter.To.AddDays(-(StaffPerformanceRules.ChartDays - 1))
            ? filter.From
            : filter.To.AddDays(-(StaffPerformanceRules.ChartDays - 1));

        var shares = await SharesAsync(branchId, timeZoneId, chartFrom, filter.To, cancellationToken);
        var byDay = shares
            .Select(tab => new
            {
                Date = DateOnly.FromDateTime(BranchTime.ToLocal(tab.ClosedAtUtc, timeZoneId)),
                Amd = tab.Shares.Where(s => s.Server == staffMemberId).Sum(s => s.Amd),
                Counts = tab.Servers.Contains(staffMemberId),
            })
            .Where(x => x.Counts)
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => (Amd: g.Sum(x => x.Amd), Tables: g.Count()));

        var daily = Enumerable.Range(0, filter.To.DayNumber - chartFrom.DayNumber + 1)
            .Select(offset => chartFrom.AddDays(offset))
            .Select(date => byDay.TryGetValue(date, out var day) ? new DailySales(date, day.Amd, day.Tables) : new DailySales(date, 0L, 0))
            .ToList();

        return (row, daily);
    }

    private async Task<RecentPage> RecentAsync(
        Guid branchId, string timeZoneId, Guid staffMemberId, StaffPerformanceFilter filter, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(filter.From, filter.To, timeZoneId);
        var actions = StaffPerformanceRules.ActionsOf(filter.List).ToList();

        var entries = db.ActivityEntries
            .AsNoTracking()
            .Where(e => e.BranchId == branchId
                        && e.StaffMemberId == staffMemberId
                        && e.OccurredAtUtc >= fromUtc
                        && e.OccurredAtUtc < toUtc
                        && actions.Contains(e.Action));

        if (filter.Cursor is { } cursor)
        {
            var atUtc = cursor.OccurredAtUtc;
            var afterId = cursor.EntryId;

            entries = entries.Where(e => e.OccurredAtUtc < atUtc || (e.OccurredAtUtc == atUtc && e.Id.CompareTo(afterId) < 0));
        }

        var rows = await entries
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(filter.Limit + 1)
            .ToListAsync(cancellationToken);

        var shown = rows.Take(filter.Limit).ToList();
        var zone = BranchZone.For(timeZoneId);

        return new RecentPage(
            StaffPerformanceRules.Word(filter.List),
            [.. shown.Select(e => ActivityHistoryQuery.ToItem(e, zone))],
            rows.Count > filter.Limit ? new ActivityCursor(shown[^1].OccurredAtUtc, shown[^1].Id).Encode() : null);
    }

    private async Task<StaffPerformanceRow> EmptyRowAsync(Guid staffMemberId, CancellationToken cancellationToken)
    {
        var person = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffMemberId)
            .Select(s => new { s.FullName, s.Role, s.IsActive })
            .FirstAsync(cancellationToken);

        return new Tally().ToRow(staffMemberId, person.FullName, person.Role, person.IsActive);
    }

    // ---------------------------------------------------------------- the branch

    private async Task<StaffPerformanceReport> ComputeAsync(
        Guid branchId,
        string timeZoneId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken,
        Guid? onlyFor = null)
    {
        // The reports' own boundaries: the branch's local days, half-open, converted once.
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(from, to, timeZoneId);
        var tallies = new Dictionary<Guid, Tally>();
        Tally Of(Guid id) => tallies.TryGetValue(id, out var t) ? t : tallies[id] = new Tally();

        // Sales, tables, guests - the bills that closed in the window, split by whose lines they were.
        var shares = await SharesAsync(branchId, timeZoneId, from, to, cancellationToken);
        long unattributed = 0;

        foreach (var tab in shares)
        {
            foreach (var (server, amd) in tab.Shares)
            {
                if (server is { } id)
                {
                    Of(id).SalesAmd += amd;
                }
                else
                {
                    unattributed += amd;
                }
            }

            foreach (var server in tab.Servers)
            {
                Of(server).Tables++;
                Of(server).Guests += tab.PartySize;
            }
        }

        // Payments on those bills: the tip is the table's waiter's when it was paid, the cash whoever took it.
        var payments = await ClosedTabs(branchId, fromUtc, toUtc)
            .SelectMany(t => t.Payments.Where(p => p.Status == PaymentStatus.Succeeded))
            .GroupBy(p => new
            {
                p.ServerStaffMemberId,
                p.RecordedByStaffId,
                FinalServer = p.Tab.TableSession.ServerStaffMemberId,
                Cash = p.Method == PaymentMethod.Cash,
            })
            .Select(g => new
            {
                g.Key.ServerStaffMemberId,
                g.Key.RecordedByStaffId,
                g.Key.FinalServer,
                g.Key.Cash,
                AmountAmd = g.Sum(p => p.AmountAmd),
                TipAmd = g.Sum(p => p.TipAmd),
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken);

        long tips = 0, cash = 0;
        var cashPayments = 0;

        foreach (var group in payments)
        {
            tips += group.TipAmd;

            if ((group.ServerStaffMemberId ?? group.FinalServer) is { } tipsTo)
            {
                Of(tipsTo).TipsAmd += group.TipAmd;
            }

            if (!group.Cash)
            {
                continue;
            }

            cash += group.AmountAmd;
            cashPayments += group.Count;

            if (group.RecordedByStaffId is { } takenBy)
            {
                Of(takenBy).CashAmd += group.AmountAmd;
                Of(takenBy).CashPayments += group.Count;
            }
        }

        // The takeaway counter: cash is whoever handed it over; the takeaway total is nobody's sale.
        var takeaway = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.BranchId == branchId
                        && o.Status == TakeawayStatus.Collected
                        && o.CollectedAtUtc >= fromUtc
                        && o.CollectedAtUtc < toUtc)
            .GroupBy(o => new { o.CollectedByStaffId, Cash = o.PaymentMethod == TakeawayPaymentMethod.Cash })
            .Select(g => new { g.Key.CollectedByStaffId, g.Key.Cash, Amd = g.Sum(o => o.PaidAmountAmd ?? 0L), Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var group in takeaway.Where(g => g.Cash))
        {
            cash += group.Amd;
            cashPayments += group.Count;

            if (group.CollectedByStaffId is { } handedBy)
            {
                Of(handedBy).CashAmd += group.Amd;
                Of(handedBy).CashPayments += group.Count;
            }
        }

        // Discounts and comps standing, by who gave them - the revenue report's rows, by the bill's arithmetic.
        var standing = db.TabAdjustments
            .AsNoTracking()
            .Where(a => a.Tab.BranchId == branchId
                        && a.CreatedAtUtc >= fromUtc
                        && a.CreatedAtUtc < toUtc
                        && a.VoidedAtUtc == null);

        var adjustments = await standing
            .Select(a => new { a.Id, a.CreatedByStaffId, a.Kind })
            .ToListAsync(cancellationToken);

        // What each took off its bill, by the bill's arithmetic - the stored subtotal is what is left after them.
        var reductions = await AdjustmentReductions.ForAsync(db, standing, cancellationToken);

        long adjustmentsAmd = 0;

        foreach (var adjustment in adjustments)
        {
            var reduction = reductions.GetValueOrDefault(adjustment.Id);
            var tally = Of(adjustment.CreatedByStaffId);

            adjustmentsAmd += reduction;
            tally.AdjustmentsAmd += reduction;

            if (adjustment.Kind == AdjustmentKind.Comp)
            {
                tally.Comps++;
            }
            else
            {
                tally.Discounts++;
            }
        }

        // Lines voided in the window, by who voided them.
        var voids = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => l.TabOrder.Tab.BranchId == branchId
                        && l.VoidedAtUtc >= fromUtc
                        && l.VoidedAtUtc < toUtc
                        && l.VoidedByStaffId != null)
            .GroupBy(l => l.VoidedByStaffId!.Value)
            .Select(g => new { StaffId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var group in voids)
        {
            Of(group.StaffId).Voids += group.Count;
        }

        // Calls claimed in the window: the seconds from the call to the claim.
        var claims = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && r.ClaimedAtUtc >= fromUtc
                        && r.ClaimedAtUtc < toUtc
                        && r.ClaimedByStaffId != null)
            .Select(r => new { StaffId = r.ClaimedByStaffId!.Value, r.CreatedAtUtc, AtUtc = r.ClaimedAtUtc!.Value })
            .ToListAsync(cancellationToken);

        var allClaims = new List<int>(claims.Count);

        foreach (var claim in claims)
        {
            var seconds = Seconds(claim.CreatedAtUtc, claim.AtUtc);

            allClaims.Add(seconds);
            Of(claim.StaffId).ClaimSeconds.Add(seconds);
        }

        // Guest texts answered in the window.
        var replies = await RepliesAsync(branchId, fromUtc, toUtc, cancellationToken);

        foreach (var (staffId, seconds) in replies)
        {
            Of(staffId).ReplySeconds.Add(seconds);
        }

        // Bookings decided, off the History - the decision is not stored on the booking.
        var decisions = await db.ActivityEntries
            .AsNoTracking()
            .Where(e => e.BranchId == branchId
                        && e.OccurredAtUtc >= fromUtc
                        && e.OccurredAtUtc < toUtc
                        && e.StaffMemberId != null
                        && (e.Action == ActivityActions.BookingApproved || e.Action == ActivityActions.BookingDeclined))
            .GroupBy(e => new { StaffId = e.StaffMemberId!.Value, e.Action })
            .Select(g => new { g.Key.StaffId, g.Key.Action, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var group in decisions)
        {
            if (group.Action == ActivityActions.BookingApproved)
            {
                Of(group.StaffId).Approvals += group.Count;
            }
            else
            {
                Of(group.StaffId).Declines += group.Count;
            }
        }

        // Bookings taken by hand (2026-10-31), by the booking's own start in the window: what became of each is on the
        // row - came (seated or completed), never came (a no-show that stands: one withdrawn is confirmed again).
        var taken = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && r.TakenByStaffId != null
                        && r.StartUtc >= fromUtc
                        && r.StartUtc < toUtc)
            .GroupBy(r => new { StaffId = r.TakenByStaffId!.Value, r.Status })
            .Select(g => new { g.Key.StaffId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var group in taken)
        {
            var tally = Of(group.StaffId);
            tally.BookingsTaken += group.Count;

            if (group.Status is ReservationStatus.Seated or ReservationStatus.Completed)
            {
                tally.BookingsCame += group.Count;
            }
            else if (group.Status == ReservationStatus.NoShow)
            {
                tally.BookingsNoShow += group.Count;
            }
        }

        // Who to list: everybody with anything in the window, and the branch's own waiters and managers with nothing.
        var ids = tallies.Keys.ToList();

        var people = await db.StaffMembers
            .AsNoTracking()
            .Where(s => (onlyFor == null || s.Id == onlyFor)
                        && (ids.Contains(s.Id)
                            || (s.BranchId == branchId
                                && s.IsActive
                                && (s.Role == StaffRole.Waiter || s.Role == StaffRole.Manager))))
            .Select(s => new { s.Id, s.FullName, s.Role, s.IsActive })
            .ToListAsync(cancellationToken);

        var rows = people
            .Select(p => (tallies.GetValueOrDefault(p.Id) ?? new Tally()).ToRow(p.Id, p.FullName, p.Role, p.IsActive))
            .OrderByDescending(r => r.SalesAmd)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

        var hall = shares.Sum(s => s.TotalAmd);

        var totals = new StaffPerformanceTotals(
            shares.Count,
            shares.Sum(s => (long)s.PartySize),
            hall,
            unattributed,

            // The revenue report's average tab, by its own expression.
            shares.Count == 0 ? 0L : (long)Math.Round((decimal)hall / shares.Count, 0),
            takeaway.Sum(g => g.Amd),
            tips,
            allClaims.Count,
            StaffPerformanceRules.Median(allClaims),
            replies.Count,
            StaffPerformanceRules.Median([.. replies.Select(r => r.Seconds)]),
            decisions.Where(d => d.Action == ActivityActions.BookingApproved).Sum(d => d.Count),
            decisions.Where(d => d.Action == ActivityActions.BookingDeclined).Sum(d => d.Count),
            cash,
            cashPayments,
            adjustments.Count(a => a.Kind != AdjustmentKind.Comp),
            adjustments.Count(a => a.Kind == AdjustmentKind.Comp),
            adjustmentsAmd,
            voids.Sum(v => v.Count),
            taken.Sum(t => t.Count),
            taken.Where(t => t.Status is ReservationStatus.Seated or ReservationStatus.Completed).Sum(t => t.Count),
            taken.Where(t => t.Status == ReservationStatus.NoShow).Sum(t => t.Count));

        return new StaffPerformanceReport(branchId, from, to, rows, totals);
    }

    /// <summary>The bills the revenue report counts for these dates: closed in the window, admitted.</summary>
    private IQueryable<Domain.Tabs.Tab> ClosedTabs(Guid branchId, DateTime fromUtc, DateTime toUtc) =>
        db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == branchId
                        && t.ClosedAtUtc != null
                        && t.ClosedAtUtc >= fromUtc
                        && t.ClosedAtUtc < toUtc
                        && t.Status == TabStatus.Closed
                        && t.Admission == TabAdmission.Admitted);

    /// <summary>One bill and how it splits.</summary>
    private sealed record TabShares(
        DateTime ClosedAtUtc,
        long TotalAmd,
        int PartySize,
        IReadOnlyList<(Guid? Server, long Amd)> Shares,
        IReadOnlyList<Guid> Servers);

    /// <summary>The bills closed in the window and whose each part of each was. Two statements.</summary>
    private async Task<List<TabShares>> SharesAsync(
        Guid branchId, string timeZoneId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(from, to, timeZoneId);

        var tabs = await ClosedTabs(branchId, fromUtc, toUtc)
            .Select(t => new
            {
                t.Id,
                ClosedAtUtc = t.ClosedAtUtc!.Value,
                t.TotalAmd,
                t.TableSession.PartySize,
                FinalServer = t.TableSession.ServerStaffMemberId,
            })
            .ToListAsync(cancellationToken);

        // Each bill's live lines by the waiter they were placed under, with when that waiter's first order went in. Live
        // by the bill's one rule - not voided; a voided order's lines are voided with it (TabOrder.Void) - so the weights
        // are the lines the total was made of.
        var weights = await ClosedTabs(branchId, fromUtc, toUtc)
            .SelectMany(t => t.Orders)
            .SelectMany(o => o.Lines.Where(l => l.VoidedAtUtc == null), (o, l) => new
            {
                o.TabId,
                o.ServerStaffMemberId,
                o.PlacedAtUtc,
                Amd = (long)l.Quantity * l.UnitPriceAmdSnapshot,
            })
            .GroupBy(x => new { x.TabId, x.ServerStaffMemberId })
            .Select(g => new { g.Key.TabId, g.Key.ServerStaffMemberId, Weight = g.Sum(x => x.Amd), First = g.Min(x => x.PlacedAtUtc) })
            .ToListAsync(cancellationToken);

        var byTab = weights.ToLookup(w => w.TabId);

        return
        [
            .. tabs.Select(tab =>
            {
                // Lines nobody held are the waiter's at the close - who took the table as it stood.
                var held = byTab[tab.Id]
                    .Select(w => (Server: w.ServerStaffMemberId ?? tab.FinalServer, w.Weight, w.First))
                    .GroupBy(w => w.Server)
                    .Select(g => (Server: g.Key, Weight: g.Sum(w => w.Weight), First: g.Min(w => w.First)))
                    .OrderBy(w => w.First)
                    .Select(w => (w.Server, w.Weight))
                    .ToList();

                var split = StaffPerformanceRules.Split(tab.TotalAmd, held, tab.FinalServer);

                var servers = split.Where(s => s.Server is not null && s.Amd > 0).Select(s => s.Server!.Value)
                    .Concat(tab.FinalServer is { } final ? [final] : [])
                    .Distinct()
                    .ToList();

                return new TabShares(tab.ClosedAtUtc, tab.TotalAmd, tab.PartySize, split, servers);
            }),
        ];
    }

    /// <summary>
    /// Every guest text answered in the window: from the guest's first text nobody at the venue had answered to the next
    /// venue message (a text or the on-my-way question), whose writer answered it. One statement - the branch's threads
    /// written to in the window, their guest texts and venue messages up to its end.
    /// </summary>
    private async Task<List<(Guid StaffId, int Seconds)>> RepliesAsync(
        Guid branchId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var messages = await db.ConversationMessages
            .AsNoTracking()
            .Where(m => db.Conversations.Any(c => c.Id == m.ConversationId && c.BranchId == branchId && c.LastMessageUtc >= fromUtc)
                        && m.CreatedAtUtc < toUtc
                        && ((m.Author == MessageAuthor.Guest && m.Kind == MessageKind.Text) || m.Author == MessageAuthor.Venue))
            .OrderBy(m => m.ConversationId)
            .ThenBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Sequence)
            .Select(m => new { m.ConversationId, m.Author, m.CreatedAtUtc, m.StaffMemberId })
            .ToListAsync(cancellationToken);

        var replies = new List<(Guid, int)>();

        foreach (var thread in messages.GroupBy(m => m.ConversationId))
        {
            DateTime? waitingSince = null;

            foreach (var message in thread)
            {
                if (message.Author == MessageAuthor.Guest)
                {
                    waitingSince ??= message.CreatedAtUtc;
                    continue;
                }

                if (waitingSince is { } since && message.CreatedAtUtc >= fromUtc && message.StaffMemberId is { } staffId)
                {
                    replies.Add((staffId, Seconds(since, message.CreatedAtUtc)));
                }

                waitingSince = null;
            }
        }

        return replies;
    }

    private static int Seconds(DateTime fromUtc, DateTime toUtc) => (int)Math.Max(0d, (toUtc - fromUtc).TotalSeconds);

    private async Task<BranchFacts> BranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new BranchFacts(b.VenueId, b.TimeZoneId))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    private sealed record BranchFacts(Guid VenueId, string TimeZoneId);

    /// <summary>One person's running numbers.</summary>
    private sealed class Tally
    {
        public int Tables { get; set; }

        public long Guests { get; set; }

        public long SalesAmd { get; set; }

        public long TipsAmd { get; set; }

        public List<int> ClaimSeconds { get; } = [];

        public List<int> ReplySeconds { get; } = [];

        public int Approvals { get; set; }

        public int Declines { get; set; }

        public long CashAmd { get; set; }

        public int CashPayments { get; set; }

        public int Discounts { get; set; }

        public int Comps { get; set; }

        public long AdjustmentsAmd { get; set; }

        public int Voids { get; set; }

        public int BookingsTaken { get; set; }

        public int BookingsCame { get; set; }

        public int BookingsNoShow { get; set; }

        public StaffPerformanceRow ToRow(Guid id, string name, StaffRole role, bool isActive) =>
            new(
                id,
                name,
                role,
                isActive,
                Tables,
                Guests,
                SalesAmd,
                Tables == 0 ? 0L : (long)Math.Round((decimal)SalesAmd / Tables, 0),
                TipsAmd,
                ClaimSeconds.Count,
                StaffPerformanceRules.Median(ClaimSeconds),
                ReplySeconds.Count,
                StaffPerformanceRules.Median(ReplySeconds),
                Approvals,
                Declines,
                CashAmd,
                CashPayments,
                Discounts,
                Comps,
                AdjustmentsAmd,
                Voids,
                BookingsTaken,
                BookingsCame,
                BookingsNoShow);
    }
}
