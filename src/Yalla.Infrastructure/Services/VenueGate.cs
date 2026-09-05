using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// One answer to "is this branch open for business right now", used by every entry point that
/// starts something new at a venue.
/// </summary>
/// <remarks>
/// <para>
/// Suspending a venue is what happens when it stops paying, and hiding it from search is not
/// enough on its own: a diner's app caches branch ids, and the QR sticker stays on the table long
/// after the relationship ends. Without this the lever leaks - and a booking made after a soft
/// delete would break the very invariant the delete had just checked.
/// </para>
/// <para>
/// Deliberately called from the <i>entry points</i> only - a new booking, a scan that opens a tab,
/// a new person joining one. Reading and settling an existing tab is never gated, because taking
/// payment away from a party mid-meal strands real money on a real table and punishes the wrong
/// people for the venue's unpaid invoice.
/// </para>
/// <para>
/// It needs the branch's <c>Venue</c> loaded. Every caller includes it; a null navigation would be
/// a silent pass, so it is treated as unavailable instead.
/// </para>
/// </remarks>
internal static class VenueGate
{
    /// <exception cref="BranchUnavailableException">The venue is suspended or deleted, or the branch is switched off.</exception>
    public static void RequireOpenForBusiness(Branch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        if (!branch.IsActive)
        {
            throw new BranchUnavailableException(branch.Id, "this location is closed.");
        }

        var venue = branch.Venue
                    ?? throw new BranchUnavailableException(branch.Id, "its venue could not be read.");

        if (venue.IsDeleted)
        {
            throw new BranchUnavailableException(branch.Id, "the venue is no longer on Yalla.");
        }

        if (venue.IsSuspended)
        {
            throw new BranchUnavailableException(branch.Id, "the venue is suspended.");
        }

        if (!venue.IsActive)
        {
            throw new BranchUnavailableException(branch.Id, "the venue is not active.");
        }
    }
}
