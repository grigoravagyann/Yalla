using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>
/// Builds one participant's view of a tab from the whole tab and that participant's flags.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place the visibility rules are applied. There is deliberately no
/// <c>if (canSeeTotal)</c> in any endpoint or query: a check scattered across handlers is a check
/// one handler forgets, and with money the failure is a leak rather than a crash. Everything a
/// client can see about a tab comes out of this function, given the snapshot and who is asking.
/// </para>
/// <para>
/// It is a pure function over in-memory records, so it is unit-tested directly - every rule in
/// <c>docs/tabs.md</c> has a test that calls this with the relevant flags and inspects the shape.
/// </para>
/// </remarks>
public static class TabProjection
{
    /// <summary>What a tab from before admission existed reads as: admitted, for no recorded reason.</summary>
    public static readonly TabAdmissionView LegacyAdmitted = new(TabAdmission.Admitted, null, null, null, null);

    /// <summary>
    /// The tab as <paramref name="viewerParticipantId"/> may see it, or null when they are not on
    /// this tab at all.
    /// </summary>
    /// <remarks>
    /// Four rules, in order of what they protect:
    /// <list type="number">
    /// <item>
    /// <b>Own items, always.</b> The viewer's own lines and their subtotal are present whatever
    /// their status or flags. That is what settles "I didn't order that" at the table. "Own" is
    /// <see cref="IsOwnLine"/>: what they ordered, what a waiter keyed in on their behalf, and a
    /// shared item they were at the table for - and a table-attributed line they were at the table
    /// for only when they may see the table total.
    /// </item>
    /// <item>
    /// <b>Nothing silently disappears.</b> A voided line stays on the tab and stays visible,
    /// labelled with who removed it and why; a comp or a discount appears as its own entry with the
    /// manager's reason. Both are excluded from every total. This is not a courtesy - a bill whose
    /// number drops with no visible cause is the fastest way to make somebody distrust the app, and
    /// the person who then has to explain it is a waiter standing at the table.
    /// </item>
    /// <item>
    /// <b>The table aggregate and other people's items only with <c>CanSeeTableTotal</c>, and only
    /// once approved.</b> When hidden, the members are <i>absent</i> - not zero, not null - with
    /// <c>tableTotalVisible</c> false beside them so no client can mistake the gap for a free bill.
    /// The adjustments follow: a viewer without the total gets only those on their own lines. A
    /// comp off the whole tab, or off somebody else's dish, is table money - a percentage off the
    /// whole tab and what it took off give the subtotal away - and their part of it is on
    /// <c>/shares</c>.
    /// </item>
    /// <item>
    /// <b>The roster only once approved.</b> A pending joiner sees their own row and nothing else,
    /// so the next table cannot read who is sitting here by scanning the wrong code.
    /// </item>
    /// </list>
    /// Menu prices are not part of this view and stay visible to everyone through the menu
    /// endpoints, so a guest without the total can always work out what their own order costs. The
    /// service charge percentage travels with the tab for the same reason: it is a fact about the
    /// venue rather than an aggregate, and a bill has to state it from the first item.
    /// </remarks>
    public static TabView? Project(TabSnapshot tab, Guid viewerParticipantId)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var me = tab.Participants.FirstOrDefault(p => p.ParticipantId == viewerParticipantId);

        if (me is null)
        {
            return null;
        }

        var names = tab.Participants.ToDictionary(p => p.ParticipantId, p => p.DisplayName);

        // Rule 3 decides rule 1 in one place, so it is worked out first.
        var tableTotalVisible = TabPermissions.MaySeeTableTotal(me.Status, me.CanSeeTableTotal);

        // Rule 1 - own items, regardless of every other flag. See IsOwnLine for which those are, and
        // for the one line /shares charges somebody for that is not itemised on their bill.
        //
        // Rule 2 - voided lines are among them. They used to be filtered out of both arrays here,
        // which meant a diner never saw them in any form and the endpoint's own documentation said
        // the opposite. LineTotalAmd is already zero once voided, so including them changes no
        // arithmetic; it changes what the person watching the bill is told.
        var mine = tab.Lines.Where(l => IsOwnLine(l, me.ParticipantId, tableTotalVisible)).ToList();
        var myLines = mine.Select(l => ToView(l, names)).ToList();
        var myLineIds = mine.Select(l => l.LineId).ToHashSet();

        // Split lines are apportioned by /shares; the honest own-subtotal is the unsplit, unvoided
        // lines, and the rest are listed with their flags set.
        var myItemsSubtotalAmd = mine
            .Where(l => !l.IsSplitAcrossParticipants && !l.IsVoided)
            .Sum(l => l.LineTotalAmd);

        // Rule 3 - the aggregate and everyone else's items.
        var tableTotal = tableTotalVisible
            ? new TabTotalsView(tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd)
            : null;

        var tableLines = tableTotalVisible
            ? tab.Lines.Select(l => ToView(l, names)).ToList()
            : null;

        // Tips are money the table handed over, so they go where the table total goes. Outside
        // TableTotal all the same: a tip is not part of what is owed.
        long? tipsAmd = tableTotalVisible ? tab.TipsAmd : null;

        // The comps and discounts go where the lines go. Dropped rather than shown without their
        // amounts: the diner app draws every adjustment with what it took off, and a row with no
        // number is not one it can draw.
        var adjustments = tab.Adjustments
            .Where(a => tableTotalVisible || (a.TabOrderLineId is { } lineId && myLineIds.Contains(lineId)))
            .Select(ToView)
            .ToList();

        // Rule 4 - the roster.
        var participants = TabPermissions.MaySeeRoster(me.Status)
            ? tab.Participants
                .Where(p => p.Status != ParticipantStatus.Removed)
                .OrderBy(p => p.JoinedAtUtc)
                .Select(ToSummary)
                .ToList()
            : [ToSummary(me)];

        return new TabView(
            tab.TabId,
            tab.BranchId,
            tab.VenueName,
            tab.BranchName,
            tab.TableLabel,
            tab.TimeZoneId,
            tab.Status,
            tab.SettlementMode,
            SettlementModeLocked: tab.SettlementModeLockedAtUtc is not null,
            tab.HideTotalFromGuests,
            tab.HostParticipantId,

            // Not gated on the total being visible. The percentage is a property of the venue, and
            // a diner who can see their own items must be able to work out what they will be
            // charged on them.
            ServiceChargePercent: tab.ServiceChargePercentSnapshot,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            MaxSequence: tab.MaxEventSequence,
            Me: ToParticipantView(me, tab.Status, tab.AdmissionOrAdmitted.Status),
            Participants: participants,
            MyLines: myLines,
            MyItemsSubtotalAmd: myItemsSubtotalAmd,
            Adjustments: adjustments,
            TableTotalVisible: tableTotalVisible,
            TableTotal: tableTotal,
            TableLines: tableLines,
            TipsAmd: tipsAmd,
            Admission: tab.AdmissionOrAdmitted);
    }

    /// <summary>
    /// Whether <paramref name="line"/> is on <paramref name="participantId"/>'s own lines - itemised
    /// on their bill, price and all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line they ordered; one a waiter keyed in on their behalf (<c>/shares</c> charges it to them
    /// whole, and it used to be nobody's on the bill); and a shared item they were at the table for -
    /// a bottle somebody at the table chose to split with the people there.
    /// </para>
    /// <para>
    /// <b>A table-attributed line only with <paramref name="tableTotalVisible"/>.</b> A waiter's line
    /// for nobody in particular is charged to everybody who was at the table
    /// (<see cref="TabLineSnapshot.IsChargedTo"/>), and at a table where the waiter keys everything
    /// in unattributed it is the whole bill: itemising it for a guest the host hid the total from
    /// would hand them the total, line by line. They still pay their part, and still see it as
    /// money - <c>sharedItemsAmd</c> on <c>/shares</c> - without the lines it came from. A reader
    /// with the total sees those lines here and in the table's lines both.
    /// </para>
    /// <para>
    /// Everything that shows a participant "their" items reads this: the projection's
    /// <c>myLines</c>, the comps that follow them, and so the event stream's own lines.
    /// </para>
    /// </remarks>
    public static bool IsOwnLine(TabLineSnapshot line, Guid participantId, bool tableTotalVisible)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.PlacedByParticipantId == participantId)
        {
            return true;
        }

        return line.IsTableAttributed
            ? tableTotalVisible && line.IsChargedTo(participantId)
            : line.IsChargedTo(participantId);
    }

    /// <summary>
    /// The tab as staff see it. Staff are not participants and the host's flags do not apply to
    /// them; what they get is every phone on the table and the money, which is what "how many
    /// people are on table 7 and what do they owe" needs.
    /// </summary>
    public static TabStaffView ProjectForStaff(TabSnapshot tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var names = tab.Participants.ToDictionary(p => p.ParticipantId, p => p.DisplayName);

        return new TabStaffView(
            tab.TabId,
            tab.BranchId,
            tab.DiningTableId,
            tab.TableLabel,
            tab.TimeZoneId,
            tab.Status,
            tab.SettlementMode,
            SettlementModeLocked: tab.SettlementModeLockedAtUtc is not null,
            tab.HostParticipantId,
            ServiceChargePercent: tab.ServiceChargePercentSnapshot,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            MaxSequence: tab.MaxEventSequence,
            Participants: tab.Participants
                .Where(p => p.Status != ParticipantStatus.Removed)
                .OrderBy(p => p.JoinedAtUtc)
                .Select(p => ToParticipantView(p, tab.Status, tab.AdmissionOrAdmitted.Status))
                .ToList(),
            Lines: [.. tab.Lines.Select(l => ToView(l, names))],
            Adjustments: [.. tab.Adjustments.Select(ToView)],
            Totals: new TabTotalsView(
                tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd),
            TipsAmd: tab.TipsAmd,
            Admission: tab.AdmissionOrAdmitted);
    }

    /// <summary>
    /// One participant with their flags applied to the moment - and to the tab's admission: nobody on a
    /// tab still waiting for a waiter may order now, whatever their flags.
    /// </summary>
    public static TabParticipantView ToParticipantView(TabParticipantSnapshot p, TabStatus tabStatus, TabAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(p);

        return new TabParticipantView(
            p.ParticipantId,
            p.DisplayName,
            p.Role,
            p.Status,
            p.CanOrder,
            p.CanSeeTableTotal,
            p.CanPay,
            CanOrderNow: TabPermissions.MayOrder(p.Status, p.CanOrder, tabStatus)
                         && TabPermissions.TakesOrders(admission),
            p.JoinedAtUtc,
            p.Photo);
    }

    private static TabParticipantSummary ToSummary(TabParticipantSnapshot p) =>
        new(p.ParticipantId, p.DisplayName, p.Role, p.Status, p.Photo);

    private static TabLineView ToView(TabLineSnapshot l, IReadOnlyDictionary<Guid, string> names) =>
        new(
            l.LineId,
            l.OrderId,
            l.MenuItemId,
            l.PlacedByParticipantId,
            l.PlacedByParticipantId is { } by && names.TryGetValue(by, out var name) ? name : null,
            l.Name,
            l.UnitPriceAmd,
            l.Quantity,
            l.LineTotalAmd,
            l.Note,
            l.OrderStatus,
            l.IsShared,

            // The line's own snapshot, not the current roster. A shared line records who was
            // sitting there when it was ordered, and counting the people at the table now would
            // re-split the bottle every time somebody new scanned the code.
            SharedWithCount: l.SharedWithParticipantIds.Count,
            l.IsVoided,
            l.VoidedAtUtc,
            l.VoidReason,
            l.OnBehalfOfParticipantId,
            l.IsTableAttributed);

    private static TabAdjustmentView ToView(TabAdjustmentSnapshot a) =>
        new(
            a.AdjustmentId,
            a.TabOrderLineId,
            a.Kind,
            a.Percent,
            a.AmountAmd,
            a.ReductionAmd,
            a.Reason,
            a.CreatedAtUtc,
            a.IsVoided);
}
