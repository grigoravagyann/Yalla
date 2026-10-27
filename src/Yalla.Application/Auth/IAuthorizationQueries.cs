using Yalla.Domain.Enums;

namespace Yalla.Application.Auth;

/// <summary>
/// What the <c>TabParticipant</c> policies need to know about a participant, in one read.
/// </summary>
/// <param name="TabId">The tab.</param>
/// <param name="BranchId">The branch it belongs to.</param>
/// <param name="TabStatus">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
/// <param name="TabClosedAtUtc">When the tab closed, if it has.</param>
/// <param name="ParticipantStatus">1 PendingApproval, 2 Approved, 3 Removed.</param>
/// <param name="Role">1 Host, 2 Guest.</param>
/// <param name="CanOrder">Whether the host allows this participant to add items.</param>
/// <param name="CanSeeTableTotal">Whether they may see the table aggregate.</param>
/// <param name="CanPay">Whether they may settle against the tab.</param>
public sealed record TabParticipantAccess(
    Guid TabId,
    Guid BranchId,
    TabStatus TabStatus,
    DateTime? TabClosedAtUtc,
    ParticipantStatus ParticipantStatus,
    ParticipantRole Role,
    bool CanOrder,
    bool CanSeeTableTotal,
    bool CanPay);

/// <summary>
/// A live staff device as every request with its token needs it: what kind it is, where, and which
/// <see cref="BranchFeature"/>s the platform allows there (<c>Branches.AllowedFeatures</c>, one bit each).
/// </summary>
/// <param name="Kind">A branch tablet, or one person's paired phone.</param>
/// <param name="BranchId">The branch it signs in at.</param>
/// <param name="AllowedFeatureBits">The branch's allowed features, as stored.</param>
public sealed record StaffDeviceStanding(StaffDeviceKind Kind, Guid BranchId, int AllowedFeatureBits);

/// <summary>
/// The reads the authorisation policies do, kept behind an interface so the policy handlers do
/// not take a <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// Yes, some of the policies touch the database. That is deliberate: a revoked device, a
/// removed participant and a closed tab all have to stop working <i>now</i>, and a claim in a
/// token cannot express "now". The alternative - trusting the token until it expires - is how a
/// tablet left in a taxi keeps taking orders for the rest of the day.
/// </para>
/// <para>
/// Each of these is a single indexed lookup on the primary key, and they run only on the
/// endpoints whose policy needs them.
/// </para>
/// </remarks>
public interface IAuthorizationQueries
{
    /// <summary>
    /// The admin-panel session generation of one staff member, or null when there is no such row.
    /// </summary>
    Task<VenueUserSessionState?> GetVenueUserSessionStateAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything the tab policies need about one participant. Null when the participant does not
    /// exist, or does not belong to that tab - which is the case a stolen or swapped token hits.
    /// </summary>
    Task<TabParticipantAccess?> GetTabParticipantAccessAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The branch a tab belongs to, or null when there is no such tab. Lets <c>BranchScoped</c>
    /// guard a staff route addressed by tab id - <c>/api/tabs/{tabId}/closing</c> - by resolving
    /// the branch the route implies rather than failing closed for want of a <c>branchId</c>.
    /// </summary>
    Task<Guid?> GetTabBranchIdAsync(Guid tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The branch a table belongs to, or null when there is no such table. Lets <c>BranchScoped</c>
    /// guard a route addressed by table id - <c>/api/tables/{tableId}/regenerate-qr</c>.
    /// </summary>
    Task<Guid?> GetTableBranchIdAsync(Guid tableId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a branch belongs to a venue. Lets an owner or manager act on any branch of their
    /// own venue without holding a branch claim, while a staff member with a branch claim is
    /// still confined to it.
    /// </summary>
    Task<bool> BranchBelongsToVenueAsync(
        Guid branchId,
        Guid venueId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// An enrolled tablet or paired phone while it is live - with its kind and what the platform allows at its branch -
    /// or null once it is revoked, or when there is no such device. Called for every request carrying a device or staff
    /// session token, which is what makes revocation, and a blocked feature, immediate.
    /// </summary>
    Task<StaffDeviceStanding?> GetLiveStaffDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a diner account is active, deleted, and which session generation it is on. Null when
    /// there is no such account. Called for every request carrying a diner token, through the
    /// five-second cache in <see cref="ITokenAuthorityCheck.CheckDinerSessionAsync"/>.
    /// </summary>
    Task<DinerSessionState?> GetDinerSessionStateAsync(Guid dinerUserId, CancellationToken cancellationToken = default);
}
