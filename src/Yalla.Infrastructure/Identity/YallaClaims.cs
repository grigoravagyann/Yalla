namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The claim names this system issues and reads.
/// </summary>
/// <remarks>
/// <para>
/// Short names, not the long <c>http://schemas.xmlsoap.org/...</c> URIs. These tokens are held by
/// a phone on a cafe wifi, and the URI form adds roughly a hundred bytes per claim for nothing.
/// </para>
/// <para>
/// Every authorisation policy reads claims through these constants, so a rename is a compile
/// error rather than a policy that silently stops matching and starts letting everyone through.
/// </para>
/// </remarks>
public static class YallaClaims
{
    /// <summary>
    /// Which of the four identity types this token is, as the numeric <c>PrincipalType</c>.
    /// </summary>
    /// <remarks>
    /// Carried explicitly rather than inferred from which claims happen to be present. A handler
    /// that decides "this must be a staff token, it has a role claim" is one bug away from
    /// treating something else as staff.
    /// </remarks>
    public const string PrincipalType = "ytyp";

    /// <summary>The <c>TabParticipant</c> row. Present only on a tab participant token.</summary>
    public const string ParticipantId = "participantId";

    /// <summary>The one tab a participant token may touch.</summary>
    public const string TabId = "tabId";

    /// <summary>The branch a token is scoped to. Absent on a venue-user token, which is venue-wide.</summary>
    public const string BranchId = "branchId";

    /// <summary>The venue a staff or venue-user token belongs to.</summary>
    public const string VenueId = "venueId";

    /// <summary>The enrolled <c>StaffDevice</c>. Present on device and staff session tokens.</summary>
    public const string DeviceId = "deviceId";

    /// <summary>The acting person. This is the id that lands in the audit log.</summary>
    public const string StaffMemberId = "staffMemberId";

    /// <summary>The <c>StaffSession</c> row behind a staff session token.</summary>
    public const string SessionId = "sessionId";

    /// <summary>The <c>DinerUser</c> account behind a diner token.</summary>
    public const string DinerUserId = "dinerUserId";

    /// <summary>
    /// Staff role name - Owner, Manager, Waiter, Kitchen. Configured as the role claim type, so
    /// the framework's own role checks work against it.
    /// </summary>
    public const string Role = "role";
}
