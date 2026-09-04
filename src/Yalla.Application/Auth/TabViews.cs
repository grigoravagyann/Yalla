using Yalla.Domain.Enums;

namespace Yalla.Application.Auth;

/// <summary>
/// A tab as the person holding a participant token sees it.
/// </summary>
/// <remarks>
/// Deliberately thin. Orders, line items and payments belong to later work; what this carries is
/// enough for the app to show "you are on table 7's tab, here is what it comes to", and it exists
/// now because a tab-scoped token needs at least one tab-scoped endpoint to be scoped <i>to</i>.
/// </remarks>
/// <param name="TabId">The tab.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="TableLabel">The table as it is printed on the floor, e.g. 7 or T12.</param>
/// <param name="Status">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
/// <param name="SettlementMode">1 HostPaysEverything, 2 EveryonePaysOwnItems, 3 AnyonePaysAnyAmount.</param>
/// <param name="OpenedAtUtc">When the tab opened.</param>
/// <param name="ClosedAtUtc">When it closed, if it has.</param>
/// <param name="TotalAmd">
/// The table total in whole Armenian dram, or null when the host has hidden it from guests.
/// </param>
/// <param name="RemainingAmd">Still owed, in whole dram, or null when the total is hidden.</param>
/// <param name="Me">The caller's own participant row.</param>
/// <param name="Participants">Everyone on the tab, so the app can show who is at the table.</param>
public sealed record TabView(
    Guid TabId,
    Guid BranchId,
    string TableLabel,
    TabStatus Status,
    SettlementMode SettlementMode,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    long? TotalAmd,
    long? RemainingAmd,
    TabParticipantView Me,
    IReadOnlyList<TabParticipantView> Participants);

/// <summary>One person on a tab. Not an account - see <c>docs/auth.md</c>.</summary>
/// <param name="ParticipantId">The participant row.</param>
/// <param name="DisplayName">What the host sees. The participant can change their own.</param>
/// <param name="Role">1 Host, 2 Guest.</param>
/// <param name="Status">1 PendingApproval, 2 Approved, 3 Removed.</param>
/// <param name="CanOrder">Whether they may add items.</param>
/// <param name="CanPay">Whether they may settle against the tab.</param>
public sealed record TabParticipantView(
    Guid ParticipantId,
    string DisplayName,
    ParticipantRole Role,
    ParticipantStatus Status,
    bool CanOrder,
    bool CanPay);

/// <summary>Reads a tab for the participant looking at it.</summary>
public interface ITabQuery
{
    /// <summary>
    /// The tab as <paramref name="participantId"/> is allowed to see it, or null when there is no
    /// such tab.
    /// </summary>
    /// <remarks>
    /// Money is omitted when the tab hides the total from guests, which is a per-tab setting the
    /// host chooses - not something the client decides whether to render.
    /// </remarks>
    Task<TabView?> GetForParticipantAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default);
}
