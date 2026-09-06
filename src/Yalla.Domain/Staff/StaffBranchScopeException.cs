namespace Yalla.Domain.Staff;

/// <summary>
/// Thrown when a staff member acts on something belonging to a branch that is not theirs.
/// </summary>
/// <remarks>
/// <para>
/// Its own type, and not <see cref="DomainStateException"/>, because the two answer differently and
/// the difference is the whole point. A branch mismatch used to be raised as a domain-state
/// exception, which the API maps to <b>409 Conflict</b> - and 409 is wrong here twice over. It
/// tells the caller the request clashed with the current state of something, when in fact the
/// caller is simply not entitled to it; and it contradicts every endpoint on this path, all of
/// which declare <b>403</b> with "Not staff at this branch." The same refusal already arrives as a
/// 403 when the route-level <c>BranchScoped</c> policy is the thing that catches it, so the two
/// halves of one boundary were answering with two different statuses depending on which layer
/// happened to notice.
/// </para>
/// <para>
/// Nor <see cref="StaffPermissionException"/>, which builds its message out of roles: "requires
/// the Waiter role; the caller is a Manager" is actively misleading for somebody who holds exactly
/// the right role at the wrong branch.
/// </para>
/// <para>
/// The message names no ids. Confirming that a given tab or request exists somewhere else on the
/// platform is more than the caller is owed.
/// </para>
/// </remarks>
public sealed class StaffBranchScopeException : Exception
{
    /// <param name="subject">
    /// What was addressed, as a bare noun for the message - "tab", "request", "order".
    /// </param>
    public StaffBranchScopeException(string subject)
        : base($"That {subject} belongs to another branch. Staff act on the branch they are "
               + "enrolled at, and an owner or manager on the branches of their own venue.") =>
        Subject = subject;

    /// <summary>What was addressed. Carried so the error envelope can name it.</summary>
    public string Subject { get; }
}
