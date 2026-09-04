namespace Yalla.Application.Auth;

/// <summary>
/// Identity type 1: someone at a table, with no account at all.
/// </summary>
/// <remarks>
/// <para>
/// This is the flow the product lives or dies on. Someone scans the QR on table 7 and orders a
/// coffee. If that asks them to register, they put the phone down - walk-ins are most of the
/// traffic in a cafe and a stranger will not create an account to order.
/// </para>
/// <para>
/// So there is no user row, no password, no phone number and nothing to remember. The server
/// issues a token scoped to one tab, and that token is structurally incapable of reading any
/// other tab, table or branch: the claims simply do not name one.
/// </para>
/// </remarks>
public interface ITabParticipantAuthService
{
    /// <summary>
    /// Joins the tab open at the table behind a QR code.
    /// </summary>
    /// <remarks>
    /// A device that already has a participant on that tab gets its existing one back with a
    /// fresh token, so re-scanning after a phone locks does not create a second person on the
    /// bill.
    /// </remarks>
    /// <param name="qrToken">The token printed on the table.</param>
    /// <param name="deviceId">A stable per-install identifier the app generates. Not an account.</param>
    /// <param name="displayName">Optional. What the host sees; defaults to a numbered guest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">No active table carries that QR token.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">
    /// Nobody is seated at the table yet, so there is no tab to join. A member of staff seats the
    /// party first - seating is a staff transition in this system, and this flow does not bypass
    /// the table state machine.
    /// </exception>
    Task<TabParticipantTokenResult> JoinByTableQrAsync(
        string qrToken,
        string deviceId,
        string? displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a <c>TabJoinToken</c> - the link a person already on the tab passes round the table.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The token is unknown, revoked or expired.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The tab is no longer open.</exception>
    Task<TabParticipantTokenResult> RedeemJoinTokenAsync(
        string joinToken,
        string deviceId,
        string? displayName,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a participant on their own tab.</summary>
    /// <returns>The participant's updated view of themselves.</returns>
    /// <exception cref="KeyNotFoundException">No such participant on that tab.</exception>
    Task<TabParticipantTokenResult> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default);
}
