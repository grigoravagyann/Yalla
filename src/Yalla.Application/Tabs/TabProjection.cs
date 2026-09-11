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
    /// <summary>
    /// The tab as <paramref name="viewerParticipantId"/> may see it, or null when they are not on
    /// this tab at all.
    /// </summary>
    /// <remarks>
    /// Four rules, in order of what they protect:
    /// <list type="number">
    /// <item>
    /// <b>Own items, always.</b> The viewer's own lines and their subtotal are present whatever
    /// their status or flags. That is what settles "I didn't order that" at the table.
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

        // Rule 1 - own items, regardless of every other flag. A line is "mine" if I placed it or
        // it is a shared item I was present for.
        //
        // Rule 2 - voided lines are among them. They used to be filtered out of both arrays here,
        // which meant a diner never saw them in any form and the endpoint's own documentation said
        // the opposite. LineTotalAmd is already zero once voided, so including them changes no
        // arithmetic; it changes what the person watching the bill is told.
        var myLines = tab.Lines
            .Where(l => l.PlacedByParticipantId == me.ParticipantId
                        || (l.IsShared && l.SharedWithParticipantIds.Contains(me.ParticipantId)))
            .Select(l => ToView(l, names))
            .ToList();

        // Shared lines are apportioned by the splitting task; until then the honest own-subtotal
        // is the unshared, unvoided lines, and the rest are listed with their flags set.
        var myItemsSubtotalAmd = myLines.Where(l => !l.IsShared && !l.IsVoided).Sum(l => l.LineTotalAmd);

        // Rule 3 - the aggregate and everyone else's items.
        var tableTotalVisible = TabPermissions.MaySeeTableTotal(me.Status, me.CanSeeTableTotal);

        var tableTotal = tableTotalVisible
            ? new TabTotalsView(tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd)
            : null;

        var tableLines = tableTotalVisible
            ? tab.Lines.Select(l => ToView(l, names)).ToList()
            : null;

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
            Me: ToParticipantView(me, tab.Status),
            Participants: participants,
            MyLines: myLines,
            MyItemsSubtotalAmd: myItemsSubtotalAmd,
            Adjustments: [.. tab.Adjustments.Select(ToView)],
            TableTotalVisible: tableTotalVisible,
            TableTotal: tableTotal,
            TableLines: tableLines);
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
                .Select(p => ToParticipantView(p, tab.Status))
                .ToList(),
            Lines: [.. tab.Lines.Select(l => ToView(l, names))],
            Adjustments: [.. tab.Adjustments.Select(ToView)],
            Totals: new TabTotalsView(
                tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));
    }

    /// <summary>One participant with their flags applied to the moment.</summary>
    public static TabParticipantView ToParticipantView(TabParticipantSnapshot p, TabStatus tabStatus)
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
            CanOrderNow: TabPermissions.MayOrder(p.Status, p.CanOrder, tabStatus),
            p.JoinedAtUtc);
    }

    private static TabParticipantSummary ToSummary(TabParticipantSnapshot p) =>
        new(p.ParticipantId, p.DisplayName, p.Role, p.Status);

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
            l.VoidReason);

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
