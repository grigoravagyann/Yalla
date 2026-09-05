namespace Yalla.Application.Auth;

/// <summary>
/// Identity type 3: a tablet enrolled to a branch, plus a per-person PIN.
/// </summary>
/// <remarks>
/// <para>
/// The constraint this exists to satisfy: a waiter must never type an email address during a
/// Friday rush. The tablet signs in once, in the morning, and stays signed in; a person's
/// identity is four taps on top of that.
/// </para>
/// <para>
/// PINs are per person, never shared. A shared PIN is faster and venues will ask for one, but it
/// erases the only thing that answers "who gave away my reserved table" - which is the reason the
/// audit log exists at all.
/// </para>
/// </remarks>
public interface IStaffAuthService
{
    // Note for anyone reading this looking for an email sign-in for waiters: there is not one, and
    // there is not going to be. See docs/auth.md - the browser is the device, not an exception to
    // the device model.

    /// <summary>
    /// Generates a one-time enrolment code for a branch. Manager or owner only.
    /// </summary>
    /// <remarks>The code is returned once and stored only as a hash. Losing it means issuing another.</remarks>
    Task<DeviceEnrolmentCodeResult> CreateEnrolmentCodeAsync(
        Guid branchId,
        Guid createdByStaffMemberId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems an enrolment code once, creating a <c>StaffDevice</c> and returning its long-lived
    /// device token.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The code is unknown or expired.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The code has already been redeemed.</exception>
    /// <param name="code">The one-time code a manager read out.</param>
    /// <param name="clientDeviceId">
    /// The identifier the client generated for itself and keeps in its own storage. A browser is a
    /// device here; this is how it recognises itself on the next load.
    /// </param>
    /// <param name="deviceName">What a manager will see in the device list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeviceEnrolmentResult> RedeemEnrolmentCodeAsync(
        string code,
        string clientDeviceId,
        string deviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What this device token is bound to, for the PIN screen.
    /// </summary>
    /// <remarks>
    /// A tablet on a counter should say where it thinks it is. Without this a browser that has been
    /// enrolled for months shows a PIN pad with no indication of which venue it will act on, and the
    /// first anybody knows about a laptop bound to the wrong branch is an audit row.
    /// </remarks>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The device is unknown or revoked.</exception>
    Task<EnrolledDeviceView> GetEnrolledDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a device token and a PIN for a short-lived staff session.
    /// </summary>
    /// <param name="deviceId">The enrolled device, taken from the device token's claims.</param>
    /// <param name="staffMemberId">Whose PIN was tapped.</param>
    /// <param name="pin">The four digits. Never logged, never stored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">Wrong PIN, or the device is revoked.</exception>
    /// <exception cref="Domain.Identity.AccountLockedException">Too many wrong PINs; a manager must clear it.</exception>
    Task<StaffSessionResult> SignInWithPinAsync(
        Guid deviceId,
        Guid staffMemberId,
        string pin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a staff session, rotating its handle and pushing the inactivity window forward.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">
    /// The handle is unknown, the session went idle for thirty minutes, its shift-length cap ran
    /// out, or the device was revoked. In every case the answer is: tap the PIN again.
    /// </exception>
    Task<StaffSessionResult> RenewSessionAsync(
        string renewalToken,
        CancellationToken cancellationToken = default);

    /// <summary>Ends a session immediately - the tablet's sign-out button.</summary>
    Task SignOutSessionAsync(string renewalToken, CancellationToken cancellationToken = default);

    /// <summary>Every device enrolled to a branch, revoked ones included.</summary>
    Task<IReadOnlyList<StaffDeviceSummary>> ListDevicesAsync(
        Guid branchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kills a tablet. Its device token stops working on the next request, and every session open
    /// on it ends.
    /// </summary>
    /// <returns>False when there is no such device in this branch.</returns>
    Task<bool> RevokeDeviceAsync(
        Guid branchId,
        Guid deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a PIN lockout. The manager path that matters: a waiter locked out mid-service
    /// cannot be made to wait out a timer.
    /// </summary>
    /// <returns>False when there is no such staff member in this branch's venue.</returns>
    Task<bool> ClearPinLockoutAsync(
        Guid branchId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default);
}
