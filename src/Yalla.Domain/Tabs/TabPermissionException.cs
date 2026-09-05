namespace Yalla.Domain.Tabs;

/// <summary>
/// The caller is on the tab, but is not allowed to do <i>this</i> to it: a guest approving a
/// joiner, a pending participant changing the split, a diner reassigning the host.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from a <c>TabParticipant</c> policy failure, which refuses someone who is not on the
/// tab at all before any handler runs. This is the layer below: the person is legitimately here
/// and the action is one only the host, or only staff, may take. It is thrown by the service, not
/// checked in an endpoint, so it holds for every caller and not only for HTTP.
/// </para>
/// <para>
/// Derives from <see cref="UnauthorizedAccessException"/> so the existing 403 mapping applies; the
/// message is written to be read and names the missing role.
/// </para>
/// </remarks>
public sealed class TabPermissionException(string operation, string requirement)
    : UnauthorizedAccessException($"{operation} is allowed only for {requirement}.")
{
    /// <summary>What was attempted, e.g. "Approving a participant".</summary>
    public string Operation { get; } = operation;

    /// <summary>Who may do it, e.g. "the host of this tab".</summary>
    public string Requirement { get; } = requirement;
}
