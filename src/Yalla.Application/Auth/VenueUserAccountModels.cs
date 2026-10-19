using Yalla.Domain.Enums;

namespace Yalla.Application.Auth;

/// <summary>The signed-in owner's, general manager's or branch manager's own account.</summary>
/// <param name="StaffMemberId">Their staff row - the one the staff routes, such as the PIN reset, address them by.</param>
/// <param name="FullName">The name the console and the activity feed show. Theirs to change.</param>
/// <param name="Email">
/// The sign-in address. Not theirs to change: whoever outranks them gives them a new sign-in link for a
/// new address (<c>issueStaffSignIn</c>), and an owner's is changed by the platform.
/// </param>
/// <param name="Role">1 Owner, 6 GeneralManager, 2 Manager (a branch manager).</param>
/// <param name="VenueId">The venue they work for.</param>
/// <param name="BranchId">
/// A branch manager's home branch. Absent on the wire (null) for an owner or general manager, who run every
/// branch - an owner's row may name a branch, and it is still not answered here.
/// </param>
public sealed record VenueUserProfile(
    Guid StaffMemberId,
    string FullName,
    string Email,
    StaffRole Role,
    Guid VenueId,
    Guid? BranchId);

/// <summary>Rename the signed-in venue user.</summary>
/// <param name="FullName">Required; 1-200 characters after trimming.</param>
public sealed record UpdateVenueUserProfileCommand(string? FullName);

/// <summary>Change the signed-in venue user's password.</summary>
/// <param name="CurrentPassword">Required, and must be right.</param>
/// <param name="NewPassword">At least <c>Auth:MinimumPasswordLength</c> characters (12) - the one rule every console password follows.</param>
public sealed record ChangeVenueUserPasswordCommand(
    string? CurrentPassword,
    string? NewPassword);

/// <summary>
/// One admin-panel sign-in that can still carry on: a refresh chain with a token that is neither
/// revoked, rotated nor expired.
/// </summary>
/// <remarks>
/// Nothing records the browser, the device or where it was, so none of that is here. What the chain
/// does know is when it started and when it last renewed its access token - which a console in use
/// does about every fifteen minutes.
/// </remarks>
/// <param name="Id">The sign-in (its refresh chain).</param>
/// <param name="SignedInAtUtc">When the password was typed.</param>
/// <param name="LastActiveAtUtc">When it last renewed its token; the sign-in itself until it first does.</param>
/// <param name="IsCurrent">The sign-in the request's own token belongs to.</param>
public sealed record VenueUserSession(
    Guid Id,
    DateTime SignedInAtUtc,
    DateTime LastActiveAtUtc,
    bool IsCurrent);

/// <summary>Where the venue user is signed in: the current sign-in first, then the most recently active.</summary>
public sealed record VenueUserSessions(IReadOnlyList<VenueUserSession> Sessions);

/// <summary>What signing the other sign-ins out ended.</summary>
/// <param name="EndedSessions">How many other sign-ins could still carry on and now cannot. Zero is an answer.</param>
public sealed record SignedOutElsewhere(int EndedSessions);

/// <summary>
/// The venue console's own account: an owner, general manager or branch manager reading and renaming
/// themself, changing their password, and seeing and ending their sign-ins. Only their own row, read from
/// the token; the platform admin has <c>IPlatformAdminAccountService</c> instead.
/// </summary>
public interface IVenueUserAccountService
{
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active owner, general manager or branch manager with a sign-in.</exception>
    Task<VenueUserProfile> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames them. Audited as <c>account.rename</c>; a name that does not change writes nothing.</summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">The name is missing or too long.</exception>
    Task<VenueUserProfile> UpdateAsync(UpdateVenueUserProfileCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the password after checking the current one and ends every session the account has, then
    /// answers a fresh pair for the caller to carry on with - as the platform admin's change does. Audited as
    /// <c>account.password-change</c>, a wrong current password as <c>account.password-change-failed</c>.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">The current password is missing or wrong, or the new one is missing or too short - all at once.</exception>
    /// <exception cref="Yalla.Domain.Identity.TooManyAttemptsException">Too many wrong current passwords inside the window.</exception>
    Task<RefreshResult> ChangePasswordAsync(ChangeVenueUserPasswordCommand command, CancellationToken cancellationToken = default);

    /// <summary>Every sign-in that can still carry on.</summary>
    /// <param name="currentChainId">The refresh chain the request's token names, marked current; null marks none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VenueUserSessions> ListSessionsAsync(Guid? currentChainId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every other sign-in now: their refresh chains are revoked and the session generation moves, so
    /// their access tokens stop at once. The caller's access token stops too; the caller's refresh chain is
    /// kept, so it refreshes and carries on. Audited as <c>account.sign-out-others</c>.
    /// </summary>
    /// <param name="currentChainId">The chain to keep - the request's own sign-in.</param>
    /// <exception cref="Yalla.Domain.Identity.SignInUnknownException">
    /// <paramref name="currentChainId"/> is null: the token names no sign-in, so there is none to keep. Nothing changes.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignedOutElsewhere> SignOutOthersAsync(Guid? currentChainId, CancellationToken cancellationToken = default);
}
