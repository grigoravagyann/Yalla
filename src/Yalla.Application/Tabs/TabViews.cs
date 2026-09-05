using Yalla.Application.Auth;
using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>Which of the four table-state cases a scan landed in. See <c>docs/tabs.md</c>.</summary>
public enum TabOpenOutcome
{
    /// <summary>The table was free: a session and a tab were opened and the scanner is the host.</summary>
    OpenedNewSession = 1,

    /// <summary>
    /// A party was already seated - by a waiter, or from a booking - but had no tab yet. A tab
    /// was attached to their session and the scanner is the host.
    /// </summary>
    OpenedOnExistingSession = 2,

    /// <summary>
    /// The table already had an open tab. No second tab; the scanner is on the existing one as a
    /// pending participant until the host approves them.
    /// </summary>
    JoinedExistingTab = 3,
}

/// <summary>
/// What a scan or a join hands back: the device's tab-scoped token, the tab as this person is
/// allowed to see it, and which case they landed in.
/// </summary>
/// <param name="Token">The participant token. No account behind it - see <c>docs/auth.md</c>.</param>
/// <param name="Tab">The tab, projected through this participant's own permissions.</param>
/// <param name="Outcome">Which table-state case applied.</param>
/// <param name="WasReplay">
/// True when the <c>clientCommandId</c> had already been applied and this is the original answer
/// again. Treat exactly like a fresh success.
/// </param>
public sealed record TabAccessResult(
    TabParticipantTokenResult Token,
    TabView Tab,
    TabOpenOutcome Outcome,
    bool WasReplay);

/// <summary>
/// An invitation to a tab. One token, two ways to hand it over.
/// </summary>
/// <param name="Token">The opaque value. Render it as a QR on the host's screen.</param>
/// <param name="ShareUrl">The same token as a link, for WhatsApp or Telegram and the friend who is late.</param>
/// <param name="ExpiresAtUtc">Thirty minutes from issue. The host refreshes by asking for another.</param>
/// <param name="TabId">The tab it joins.</param>
public sealed record TabJoinTokenResult(
    string Token,
    string ShareUrl,
    DateTime ExpiresAtUtc,
    Guid TabId);

/// <summary>
/// A tab as one participant is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// Built by <see cref="TabProjection"/> and nowhere else, so every visibility rule is applied in
/// one place. Three things are always present whatever the flags: the caller's own row, the
/// caller's own lines, and the caller's own subtotal - "I didn't order that" is settled by the
/// person being able to see what they did order.
/// </para>
/// <para>
/// <b>The table aggregate is omitted, not zeroed.</b> When <see cref="TableTotalVisible"/> is false
/// there is no <see cref="TableTotal"/> and no <see cref="TableLines"/> member on the wire at all.
/// A zero would read as "nothing owed" and a null as "free"; an absent member with an explicit
/// flag beside it can only be read as "not shown to you".
/// </para>
/// </remarks>
/// <param name="TabId">The tab.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="TableLabel">The table as printed on the floor, e.g. 7 or T12.</param>
/// <param name="Status">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
/// <param name="SettlementMode">1 HostPaysEverything, 2 EveryonePaysOwnItems, 3 AnyonePaysAnyAmount.</param>
/// <param name="SettlementModeLocked">True once a payment has landed; the split can no longer change.</param>
/// <param name="HideTotalFromGuests">The table default for a joiner's <c>CanSeeTableTotal</c>.</param>
/// <param name="HostParticipantId">Who hosts. Null only on a tab built by hand with no host.</param>
/// <param name="OpenedAtUtc">When the tab opened.</param>
/// <param name="ClosedAtUtc">When it closed, if it has.</param>
/// <param name="Me">The caller's own participant row, with every flag and whether they may order right now.</param>
/// <param name="Participants">
/// Who is at the table. Everyone still on the tab for an approved participant; only the caller
/// themself while they are pending, because a pending joiner may see their own state and nothing else.
/// </param>
/// <param name="MyLines">The caller's own items - placed by them, or shared with them. Always present.</param>
/// <param name="MyItemsSubtotalAmd">
/// What the caller's own unshared lines come to, in whole dram. Always present. Shared lines are
/// listed in <paramref name="MyLines"/> with <c>isShared</c> set and are apportioned by the
/// splitting task, not here.
/// </param>
/// <param name="TableTotalVisible">Whether the two members below are present.</param>
/// <param name="TableTotal">The table aggregate. <b>Absent</b> when not visible - never zero, never null-as-free.</param>
/// <param name="TableLines">Every live line on the tab with who placed it. Absent when the total is hidden.</param>
public sealed record TabView(
    Guid TabId,
    Guid BranchId,
    string TableLabel,
    TabStatus Status,
    SettlementMode SettlementMode,
    bool SettlementModeLocked,
    bool HideTotalFromGuests,
    Guid? HostParticipantId,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    TabParticipantView Me,
    IReadOnlyList<TabParticipantSummary> Participants,
    IReadOnlyList<TabLineView> MyLines,
    long MyItemsSubtotalAmd,
    bool TableTotalVisible,
    TabTotalsView? TableTotal,
    IReadOnlyList<TabLineView>? TableLines);

/// <summary>One person on a tab, with their flags. Not an account - see <c>docs/auth.md</c>.</summary>
/// <param name="ParticipantId">The participant row.</param>
/// <param name="DisplayName">What the host sees. The participant can change their own.</param>
/// <param name="Role">1 Host, 2 Guest.</param>
/// <param name="Status">1 PendingApproval, 2 Approved, 3 Removed.</param>
/// <param name="CanOrder">The stored flag: whether the host allows them to add items.</param>
/// <param name="CanSeeTableTotal">Whether they may see the table aggregate and other people's items.</param>
/// <param name="CanPay">Whether they may settle against the tab. Implies <paramref name="CanSeeTableTotal"/>.</param>
/// <param name="CanOrderNow">
/// The flag applied to the moment: approved, allowed to order, and the tab still open. This is
/// what the ordering endpoints will enforce, computed by the same rule.
/// </param>
/// <param name="JoinedAtUtc">When they scanned or joined.</param>
public sealed record TabParticipantView(
    Guid ParticipantId,
    string DisplayName,
    ParticipantRole Role,
    ParticipantStatus Status,
    bool CanOrder,
    bool CanSeeTableTotal,
    bool CanPay,
    bool CanOrderNow,
    DateTime JoinedAtUtc);

/// <summary>Who is at the table, as the other diners see them: a name and whether they are in yet.</summary>
/// <param name="ParticipantId">The participant row.</param>
/// <param name="DisplayName">Their name on the tab.</param>
/// <param name="Role">1 Host, 2 Guest.</param>
/// <param name="Status">1 PendingApproval, 2 Approved.</param>
public sealed record TabParticipantSummary(
    Guid ParticipantId,
    string DisplayName,
    ParticipantRole Role,
    ParticipantStatus Status);

/// <summary>One item on the tab, as snapshotted when it was ordered.</summary>
/// <param name="LineId">The order line.</param>
/// <param name="PlacedByParticipantId">Who ordered it from their phone. Null when a waiter keyed it in.</param>
/// <param name="PlacedByDisplayName">Their name at the time of reading.</param>
/// <param name="Name">The item name as it read on the menu when ordered.</param>
/// <param name="UnitPriceAmd">Unit price in whole dram as it stood when ordered.</param>
/// <param name="Quantity">How many.</param>
/// <param name="LineTotalAmd">Unit price times quantity, in whole dram.</param>
/// <param name="IsShared">True when the item belongs to the table and is split across those present.</param>
public sealed record TabLineView(
    Guid LineId,
    Guid? PlacedByParticipantId,
    string? PlacedByDisplayName,
    string Name,
    long UnitPriceAmd,
    int Quantity,
    long LineTotalAmd,
    bool IsShared);

/// <summary>The table aggregate, in whole dram. Server-computed; the client only displays it.</summary>
public sealed record TabTotalsView(
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long RemainingAmd);

/// <summary>
/// A tab as staff see it: every phone on the table, with roles and flags, and the money. Staff are
/// not participants and are not subject to the host's visibility flags.
/// </summary>
public sealed record TabStaffView(
    Guid TabId,
    Guid BranchId,
    Guid DiningTableId,
    string TableLabel,
    TabStatus Status,
    SettlementMode SettlementMode,
    bool SettlementModeLocked,
    Guid? HostParticipantId,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    IReadOnlyList<TabParticipantView> Participants,
    TabTotalsView Totals);

/// <summary>Reads a tab for whoever is looking at it.</summary>
public interface ITabQuery
{
    /// <summary>
    /// The tab as <paramref name="participantId"/> is allowed to see it, or null when there is no
    /// such tab or they are not on it. Every visibility rule is applied by <see cref="TabProjection"/>.
    /// </summary>
    Task<TabView?> GetForParticipantAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>The tab as staff see it, or null when there is no such tab.</summary>
    Task<TabStaffView?> GetForStaffAsync(Guid tabId, CancellationToken cancellationToken = default);
}
