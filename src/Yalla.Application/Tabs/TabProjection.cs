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
    /// Three rules, in order of what they protect:
    /// <list type="number">
    /// <item>
    /// <b>Own items, always.</b> The viewer's own lines and their subtotal are present whatever
    /// their status or flags. That is what settles "I didn't order that" at the table.
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
    /// endpoints, so a guest without the total can always work out what their own order costs.
    /// </remarks>
    public static TabView? Project(TabSnapshot tab, Guid viewerParticipantId)
    {
        var me = tab.Participants.FirstOrDefault(p => p.ParticipantId == viewerParticipantId);

        if (me is null)
        {
            return null;
        }

        var names = tab.Participants.ToDictionary(p => p.ParticipantId, p => p.DisplayName);
        var liveLines = tab.Lines.Where(l => !l.IsVoided).ToList();

        // Rule 1 - own items, regardless of every other flag. A line is "mine" if I placed it or
        // it is a shared item I was present for.
        var myLines = liveLines
            .Where(l => l.PlacedByParticipantId == me.ParticipantId
                        || (l.IsShared && l.SharedWithParticipantIds.Contains(me.ParticipantId)))
            .Select(l => ToView(l, names))
            .ToList();

        // Shared lines are apportioned by the splitting task; until then the honest own-subtotal
        // is the unshared lines, and the shared ones are listed with the flag set.
        var myItemsSubtotalAmd = myLines.Where(l => !l.IsShared).Sum(l => l.LineTotalAmd);

        // Rule 2 - the aggregate and everyone else's items.
        var tableTotalVisible = TabPermissions.MaySeeTableTotal(me.Status, me.CanSeeTableTotal);

        var tableTotal = tableTotalVisible
            ? new TabTotalsView(tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd)
            : null;

        var tableLines = tableTotalVisible
            ? liveLines.Select(l => ToView(l, names)).ToList()
            : null;

        // Rule 3 - the roster.
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
            tab.TableLabel,
            tab.Status,
            tab.SettlementMode,
            SettlementModeLocked: tab.SettlementModeLockedAtUtc is not null,
            tab.HideTotalFromGuests,
            tab.HostParticipantId,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            Me: ToParticipantView(me, tab.Status),
            Participants: participants,
            MyLines: myLines,
            MyItemsSubtotalAmd: myItemsSubtotalAmd,
            TableTotalVisible: tableTotalVisible,
            TableTotal: tableTotal,
            TableLines: tableLines);
    }

    /// <summary>
    /// The tab as staff see it. Staff are not participants and the host's flags do not apply to
    /// them; what they get is every phone on the table and the money, which is what "how many
    /// people are on table 7 and what do they owe" needs.
    /// </summary>
    public static TabStaffView ProjectForStaff(TabSnapshot tab) =>
        new(
            tab.TabId,
            tab.BranchId,
            tab.DiningTableId,
            tab.TableLabel,
            tab.Status,
            tab.SettlementMode,
            SettlementModeLocked: tab.SettlementModeLockedAtUtc is not null,
            tab.HostParticipantId,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            Participants: tab.Participants
                .Where(p => p.Status != ParticipantStatus.Removed)
                .OrderBy(p => p.JoinedAtUtc)
                .Select(p => ToParticipantView(p, tab.Status))
                .ToList(),
            Totals: new TabTotalsView(
                tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));

    /// <summary>One participant with their flags applied to the moment.</summary>
    public static TabParticipantView ToParticipantView(TabParticipantSnapshot p, TabStatus tabStatus) =>
        new(
            p.ParticipantId,
            p.DisplayName,
            p.Role,
            p.Status,
            p.CanOrder,
            p.CanSeeTableTotal,
            p.CanPay,
            CanOrderNow: TabPermissions.MayOrder(p.Status, p.CanOrder, tabStatus),
            p.JoinedAtUtc);

    private static TabParticipantSummary ToSummary(TabParticipantSnapshot p) =>
        new(p.ParticipantId, p.DisplayName, p.Role, p.Status);

    private static TabLineView ToView(TabLineSnapshot l, IReadOnlyDictionary<Guid, string> names) =>
        new(
            l.LineId,
            l.PlacedByParticipantId,
            l.PlacedByParticipantId is { } by && names.TryGetValue(by, out var name) ? name : null,
            l.Name,
            l.UnitPriceAmd,
            l.Quantity,
            l.LineTotalAmd,
            l.IsShared);
}
