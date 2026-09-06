using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// The branch is on a tier that does not include this feature. Not a 403: the caller is allowed
/// to be here; the branch has not paid for what they asked for.
/// </summary>
public sealed class FeatureNotEnabledException(string feature, Guid branchId, SubscriptionTier currentTier)
    : DomainStateException(
        $"{feature} is not enabled for this branch. It is on the {currentTier} tier; "
        + "upgrade the branch to Paid to turn it on.")
{
    /// <summary>What was asked for, e.g. "Tabs and ordering".</summary>
    public string Feature { get; } = feature;

    public Guid BranchId { get; } = branchId;

    public SubscriptionTier CurrentTier { get; } = currentTier;
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
/// The branch cannot be switched to <see cref="SubscriptionTier.Paid"/> yet: its menu is not
/// finished.
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
        + "Finish them before putting the branch on Paid; the branch readiness endpoint lists what is left.")
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
/// A floor plan that cannot be applied, with the offending tables named.
/// </summary>
public sealed class FloorPlanInvalidException(
    IReadOnlyList<string> errors,
    IReadOnlyList<string> tablesOutsideCanvas,
    IReadOnlyList<string> duplicateLabels)
    : ArgumentException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    /// <summary>Labels of tables that do not sit inside the canvas.</summary>
    public IReadOnlyList<string> TablesOutsideCanvas { get; } = tablesOutsideCanvas;

    /// <summary>Labels used more than once in the plan.</summary>
    public IReadOnlyList<string> DuplicateLabels { get; } = duplicateLabels;
}
