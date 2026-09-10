using Yalla.Domain.Enums;

namespace Yalla.Application.Staff;

/// <summary>A new staff member. A PIN is required; email and password are for owners and managers.</summary>
public sealed record CreateStaffCommand(
    string FullName,
    string Phone,
    StaffRole Role,
    string Pin,
    Guid? BranchId = null,
    string? Email = null,
    string? Password = null);

/// <summary>
/// Patch a staff member. <paramref name="BranchId"/> is applied only when <paramref name="SetBranch"/>
/// is true, so that "make them venue-wide" (null) can be told apart from "leave it".
/// </summary>
public sealed record UpdateStaffCommand(
    string? FullName = null,
    string? Phone = null,
    StaffRole? Role = null,
    bool SetBranch = false,
    Guid? BranchId = null,
    bool? IsActive = null);

/// <summary>Give an owner or manager their admin-panel sign-in: the address, and a link to choose a password by.</summary>
public sealed record IssueSignInCommand(string Email);

public sealed record StaffMemberView(
    Guid Id,
    Guid? VenueId,
    Guid? BranchId,
    string FullName,
    string Phone,
    StaffRole Role,
    bool IsActive,
    string? Email,
    bool HasPasswordSignIn,
    bool IsPinLocked);

/// <summary>
/// A freshly issued sign-in link. <paramref name="ResetLink"/> exists only here: the server keeps
/// the token's hash and no read ever returns it, so this is the one copy.
/// </summary>
/// <param name="StaffMemberId">Whose sign-in it is.</param>
/// <param name="Email">The address, as stored.</param>
/// <param name="ResetLink">The link to hand over. Opening it is how they choose their password.</param>
/// <param name="ExpiresAtUtc">When the link stops working unused.</param>
/// <param name="ReplacedExistingSignIn">
/// True when this person already had a password. It keeps working until the link is used, at
/// which point it is replaced and every session they had open is ended.
/// </param>
public sealed record StaffSignInLink(
    Guid StaffMemberId,
    string Email,
    string ResetLink,
    DateTime ExpiresAtUtc,
    bool ReplacedExistingSignIn);

/// <summary>
/// Staff accounts within a venue, with the role-escalation guards.
/// </summary>
/// <remarks>
/// Who may do what is decided inside the service from the acting staff member's <i>stored</i> role,
/// not only from the token: a manager may not create a role above their own and may not change
/// their own role, whatever the request says.
/// </remarks>
public interface IStaffManagementService
{
    Task<IReadOnlyList<StaffMemberView>> ListAsync(Guid venueId, CancellationToken cancellationToken = default);

    /// <exception cref="Domain.Staff.StaffPermissionException">The role asked for is above what the caller may assign.</exception>
    Task<StaffMemberView> CreateAsync(Guid venueId, CreateStaffCommand command, CancellationToken cancellationToken = default);

    /// <exception cref="Domain.Staff.StaffPermissionException">
    /// The caller is changing their own role, or touching someone they could not have created.
    /// </exception>
    Task<StaffMemberView> UpdateAsync(Guid venueId, Guid staffMemberId, UpdateStaffCommand command, CancellationToken cancellationToken = default);

    /// <summary>Sets or resets a PIN. Clears any lockout with it.</summary>
    Task<StaffMemberView> SetPinAsync(Guid venueId, Guid staffMemberId, string pin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the address and mints a single-use link the person sets their own password through.
    /// Any earlier unused link for them stops working.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">
    /// The caller is issuing their own sign-in, or one for somebody they do not outrank.
    /// </exception>
    /// <exception cref="Domain.DomainStateException">
    /// The person signs in with a PIN, is deactivated, or the address already has an account.
    /// </exception>
    Task<StaffSignInLink> IssueSignInAsync(Guid venueId, Guid staffMemberId, IssueSignInCommand command, CancellationToken cancellationToken = default);
}
