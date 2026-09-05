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
    /// Whether a branch belongs to a venue. Lets an owner or manager act on any branch of their
    /// own venue without holding a branch claim, while a staff member with a branch claim is
    /// still confined to it.
    /// </summary>
    Task<bool> BranchBelongsToVenueAsync(
        Guid branchId,
        Guid venueId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether an enrolled tablet is still live. Called for every request carrying a device or
    /// staff session token, which is what makes revocation immediate.
    /// </summary>
    Task<bool> IsStaffDeviceActiveAsync(Guid deviceId, CancellationToken cancellationToken = default);
}
