using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>
/// Tabs: opening one by scanning, inviting others, and the permission model between them.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of caller act here and the interface keeps them apart. The host and the other
/// participants act through the first block, identified by the participant id their token
/// carries; staff act through the last two methods, identified by <c>ICurrentActor</c>. Who may
/// do what is decided <i>inside</i> the service - the host check, the staff check - so it holds for
/// every caller and not only for the HTTP pipeline that sits in front.
/// </para>
/// <para>
/// Menu, ordering, totals and payments are the next tasks. Nothing here computes money or
/// accepts an order; the permission model is built now so that when ordering arrives it has a
/// rule to enforce rather than inventing one.
/// </para>
/// </remarks>
public interface ITabService
{
    /// <summary>
    /// The table QR code was scanned. Which of the four table-state cases applies is decided
    /// here - see <c>docs/tabs.md</c> - and the answer is always the one tab for that table.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active table carries that QR token.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">
    /// The table is out of service, or the tab there is closing and takes no new participants.
    /// </exception>
    Task<TabAccessResult> OpenAsync(OpenTabCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// An invitation token was presented. Puts the device on the tab as a pending participant and
    /// issues it a token scoped to that tab.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The token is unknown, revoked or expired.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The tab no longer accepts participants.</exception>
    Task<TabAccessResult> JoinAsync(JoinTabCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host asks for an invitation. Any earlier invitation still live is revoked, so there is
    /// one current token - the QR on the host's screen and the share link are the same thing.
    /// Calling again is how the host refreshes an expired one.
    /// </summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    Task<TabJoinTokenResult> CreateJoinTokenAsync(
        Guid tabId,
        Guid actingParticipantId,
        CancellationToken cancellationToken = default);

    /// <summary>The host lets a pending joiner on.</summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    Task<TabParticipantView> ApproveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>The host turns a pending joiner away.</summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    Task<TabParticipantView> RejectParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host takes someone off the tab. A status change, never a delete: their items and any
    /// payments survive.
    /// </summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    Task<TabParticipantView> RemoveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>The host sets one person's three flags.</summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    /// <exception cref="ArgumentException"><c>CanPay</c> true with <c>CanSeeTableTotal</c> false.</exception>
    Task<TabParticipantView> SetPermissionsAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        SetParticipantPermissionsCommand permissions,
        CancellationToken cancellationToken = default);

    /// <summary>A participant sets their own name. Not an account.</summary>
    Task<TabParticipantView> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host changes how the bill will be split. Allowed until the first payment lands, then
    /// locked - the lock is stamped the first time a change is attempted after a payment exists.
    /// </summary>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller is not the host.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The mode is locked.</exception>
    Task<TabView> SetSettlementModeAsync(
        Guid tabId,
        Guid actingParticipantId,
        SettlementMode settlementMode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Staff.</b> Moves the host role to another approved participant. The host left early or
    /// their phone died, and otherwise the tab is stuck.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">The caller is not staff.</exception>
    Task<TabStaffView> ReassignHostAsync(
        Guid tabId,
        Guid newHostParticipantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Staff.</b> The bill has been asked for. After this, no new participants and no new orders.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">The caller is not staff.</exception>
    Task<TabStaffView> BeginClosingAsync(Guid tabId, CancellationToken cancellationToken = default);
}
