using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// The platform has not allowed this feature at this branch (<see cref="Branch.AllowedFeatures"/>). Not a 403: the
/// caller is allowed to be here; the branch does not have what they asked for.
/// </summary>
/// <remarks>
/// For new actions only. Work already in flight when a feature is blocked - an open tab, a placed takeaway, a sent
/// pre-order, an existing booking's cancellation - finishes normally.
/// </remarks>
public sealed class FeatureNotEnabledException(BranchFeature feature, Guid branchId)
    : DomainStateException(
        $"{feature} is not enabled for this branch. The platform switches it on; ask SHURJ.")
{
    /// <summary>What was asked for.</summary>
    public BranchFeature Feature { get; } = feature;

    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// The branch exists but is not open for business: its venue is suspended or deleted, or the
/// branch itself is switched off.
/// </summary>
/// <remarks>
/// <para>
/// Suspension is what happens when a venue stops paying, and it has to mean more than
/// disappearing from search. A diner who cached a branch id, or who scans a QR sticker that is
/// still on the table, must not be able to start new business there - otherwise the lever does
/// not work and, worse, a booking made after a soft delete breaks the very invariant the delete
/// just checked.
/// </para>
/// <para>
/// It deliberately blocks only the <i>entry points</i>: a new booking, a new tab, a new person on
/// a tab. Parties already seated keep reading and settling their bill, because taking payment away
/// from a venue mid-service would strand real money on a real table.
/// </para>
/// </remarks>
public sealed class BranchUnavailableException(Guid branchId, string reason)
    : DomainStateException(
        $"This branch is not currently open for bookings or orders: {reason} "
        + "Anyone already seated can still see and settle their bill.")
{
    public Guid BranchId { get; } = branchId;

    /// <summary>Why, in a sentence a diner can read: the venue is suspended, deleted, or the branch is closed.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The venue cannot be deleted while people are still eating there or still booked to.
/// </summary>
/// <remarks>
/// Names the blockers, because "cannot delete" with no reason sends the admin to the database
/// to find out why. The fix is to let the tabs close and the bookings pass, or to suspend the
/// venue instead, which needs neither.
/// </remarks>
public sealed class VenueDeletionBlockedException(
    Guid venueId,
    IReadOnlyList<string> openTabs,
    IReadOnlyList<string> futureReservations)
    : DomainStateException(Describe(openTabs, futureReservations))
{
    public Guid VenueId { get; } = venueId;

    /// <summary>Table labels with an open or closing tab.</summary>
    public IReadOnlyList<string> OpenTabs { get; } = openTabs;

    /// <summary>Codes of confirmed or pending bookings that have not yet started.</summary>
    public IReadOnlyList<string> FutureReservations { get; } = futureReservations;

    private static string Describe(IReadOnlyList<string> openTabs, IReadOnlyList<string> futureReservations)
    {
        var parts = new List<string>();

        if (openTabs.Count > 0)
        {
            parts.Add($"{openTabs.Count} open tab(s) on table(s) {string.Join(", ", openTabs)}");
        }

        if (futureReservations.Count > 0)
        {
            parts.Add($"{futureReservations.Count} future confirmed reservation(s) ({string.Join(", ", futureReservations)})");
        }

        return "This venue cannot be deleted: it has " + string.Join(" and ", parts)
               + ". Suspend it instead, or wait for them to close.";
    }
}

/// <summary>
/// <see cref="BranchFeature.TableService"/> cannot be turned on at the branch yet: its menu is not
/// finished. (Until 2026-10-27 this guarded switching the branch to Paid; the rule kept its intent.)
/// </summary>
/// <remarks>
/// <para>
/// This is where the rule Prompt 6 wrote into the create endpoint actually lives now. An item may
/// be saved without a photo, allergens, ingredients, a portion size or a prep time - that is how a
/// menu gets typed in, in one sitting, in a cafe - but a branch whose menu still has holes in it
/// must not start taking diners, because the diner is the person the missing allergen list is
/// dangerous to.
/// </para>
/// <para>
/// A conflict, not a permission failure. The caller is a platform admin and is entitled to do
/// this; the branch is not ready for it. The count is on the exception so the console can say
/// "eleven dishes still need a photo" rather than "cannot upgrade".
/// </para>
/// </remarks>
public sealed class BranchNotReadyForDinersException(Guid branchId, int incompleteMenuItemCount)
    : DomainStateException(
        $"This branch has {incompleteMenuItemCount} menu item(s) that are not ready to show a diner - "
        + "each is missing a photo, a description, ingredients, allergens, a portion size or a prep time. "
        + "Finish them before turning table service on; the branch readiness endpoint lists what is left.")
{
    public Guid BranchId { get; } = branchId;

    /// <summary>How many items are still incomplete. The number the refusal exists to carry.</summary>
    public int IncompleteMenuItemCount { get; } = incompleteMenuItemCount;
}

/// <summary>
/// The branch does not take bookings from its public page.
/// </summary>
/// <remarks>
/// <para>
/// A conflict, not a permission failure and not a 404. The caller is entitled to be here and the
/// branch is real and published - it has simply not agreed to take bookings from strangers on the
/// internet, which is a venue's own decision about its own page and false until somebody switches
/// it on.
/// </para>
/// <para>
/// In practice the public page reads <c>acceptsWebBookings</c> and never offers the button, so this
/// fires for a page somebody left open while the branch was switched off - which is exactly when a
/// clear refusal beats a silent failure.
/// </para>
/// </remarks>
public sealed class WebBookingsNotAcceptedException(Guid branchId, string branchName)
    : DomainStateException(
        $"{branchName} does not take bookings from its public page. "
        + "Contact the venue directly to book a table.")
{
    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// The branch does not take bookings from the diner app yet (K9).
/// </summary>
/// <remarks>
/// <para>
/// The app channel needs two things the web channel needs only one of: the venue switched online
/// bookings on (<c>AcceptsWebBookings</c>), <b>and</b> somebody saved its reservation policy
/// (<c>ReservationPolicyReviewedAtUtc</c>). A branch ships with a default policy - a turn time and a
/// booking window nobody at the venue chose - and an app that lists every branch in the city would
/// otherwise take bookings against those guesses the day the branch is created.
/// </para>
/// <para>
/// A conflict, like <see cref="WebBookingsNotAcceptedException"/>: the diner is welcome and the
/// branch is real, it simply is not taking app bookings. The app reads
/// <c>acceptsAppBookings</c> and hides the button, so in practice this fires for a screen that was
/// open while the branch was switched off.
/// </para>
/// </remarks>
public sealed class AppBookingsNotAcceptedException(Guid branchId, string branchName)
    : DomainStateException(
        $"{branchName} is not taking bookings from the app yet. "
        + "Contact the venue directly to book a table.")
{
    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// A first review of a branch from a diner who has not been there (K8).
/// </summary>
/// <remarks>
/// A visit is a seated or completed booking at the branch, or a place on one of its tabs, within
/// <c>BranchReview.VisitWindowDays</c>. A permission refusal rather than a conflict: nothing about the
/// review is wrong, the account has not earned the right to write one here. Revising a review the
/// diner already has is never refused for this.
/// </remarks>
public sealed class ReviewNeedsVisitException(Guid branchId, int windowDays)
    : Exception(
        $"Reviews are for people who have been. Book a table or join one here, and you can review it "
        + $"for {windowDays} days after.")
{
    public Guid BranchId { get; } = branchId;

    /// <summary>How far back a visit counts.</summary>
    public int WindowDays { get; } = windowDays;
}

/// <summary>
/// A floor plan that cannot be applied, with the offending tables named.
/// </summary>
public sealed class FloorPlanInvalidException(
    IReadOnlyList<string> errors,
    IReadOnlyList<string> duplicateLabels)
    : ArgumentException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    /// <summary>Labels used more than once in the plan.</summary>
    public IReadOnlyList<string> DuplicateLabels { get; } = duplicateLabels;
}

/// <summary>
/// <see cref="BranchFeature.TableService"/> cannot be turned off at the branch while tabs are open or closing there:
/// blocking it would hide those bills from the people who owe them. 409 <c>conflicting-state</c>, with the branch and how
/// many tabs, so the platform console can say which branch of a venue held the change up.
/// </summary>
public sealed class TableServiceHasOpenTabsException(Guid branchId, string branchName, IReadOnlyList<string> tableLabels)
    : DomainStateException(
        $"{branchName} has {tableLabels.Count} open tab(s) on table(s) {string.Join(", ", tableLabels)}. "
        + "Turning table service off would hide those bills from the people who owe them. "
        + "Wait until they are settled.")
{
    public Guid BranchId { get; } = branchId;

    /// <summary>Tabs open or closing at the branch.</summary>
    public int OpenTabCount { get; } = tableLabels.Count;
}
