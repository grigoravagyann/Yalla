using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Domain.Identity;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Makes the seeded demo branch look like a real listing in the diner app: cuisine, about, price
/// level, website, tables placed on the cover photo, and a handful of reviews.
/// </summary>
/// <remarks>
/// <para>
/// Development only. It is called by <see cref="DevDataSeeder"/>, which is registered only when the
/// host is Development and <c>DevActor:Enabled</c> is true - so it inherits exactly that gate.
/// </para>
/// <para>
/// Idempotent and conservative: it only ever touches the branch the seeder owns (passed in, found by
/// slug), fills listing fields only when all of them - amenities included - are still empty, places
/// active tables on the cover only while no table has a photo position yet, and adds a review only
/// for a dev reviewer who has none. A person's edits to the demo branch survive every restart; the
/// one thing it cannot tell from "never set" is a person emptying every listing field, or taking
/// every table off the photo, and those it fills again.
/// </para>
/// </remarks>
internal sealed class DevListingSeeder(
    YallaDbContext db,
    IClock clock,
    ILogger<DevListingSeeder> logger)
{
    /// <summary>The dev reviewers' numbers: the +374 99 000 0xx test range, never a real subscriber.</summary>
    internal static readonly string[] ReviewerPhones =
    [
        "+37499000051",
        "+37499000052",
        "+37499000053",
        "+37499000054",
        "+37499000055",
    ];

    private static readonly (int Rating, string Text)[] Reviews =
    [
        (5, "Lovely window seats and the coffee is excellent. Staff remembered our order."),
        (4, "Good khachapuri, a bit busy on Friday evening but worth the wait."),
        (5, "Our go-to place in the centre. Terrace is perfect in the evening."),
        (3, "Food was nice, service was slow when it got crowded."),
        (4, "Great for a group - the big table on the terrace fit all ten of us."),
    ];

    public async Task SeedAsync(Branch branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branch);

        FillListing(branch);
        await PlaceTablesOnCoverAsync(branch, cancellationToken);
        await EnsureReviewsAsync(branch, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private void FillListing(Branch branch)
    {
        // Every listing field, amenities included: the seed writes all of them at once, so a person
        // who set only amenities would otherwise lose them to the demo text on the next restart.
        if (branch.Cuisine is not null || branch.About is not null
            || branch.PriceLevel is not null || branch.WebsiteUrl is not null
            || branch.AmenityKeys is not null)
        {
            return;
        }

        branch.UpdateListing(
            cuisine: "Armenian & Mediterranean",
            about: "A bright corner cafe on Abovyan Street with window seats, a shaded terrace and "
                   + "an all-day menu of Armenian breakfasts, grills and fresh pastries.",
            priceLevel: 2,
            websiteUrl: "https://example.com/yalla-demo",
            amenities: null);

        logger.LogInformation("Seeding development listing for branch {BranchId}.", branch.Id);
    }

    /// <summary>
    /// Maps each table's floor-plan position into the middle of the photo, so the pins keep the
    /// room's layout without crowding the edges.
    /// </summary>
    private async Task PlaceTablesOnCoverAsync(Branch branch, CancellationToken cancellationToken)
    {
        if (branch.CoverPhotoId is null)
        {
            return;
        }

        // Once per branch, not once per table. A table without a position is also what a manager
        // leaves behind by taking it off the photo, so "place every unplaced table" would put it
        // back on every restart. Any pin at all means somebody - this seed or a person - has
        // already laid the photo out.
        if (await db.DiningTables.AnyAsync(
                t => t.BranchId == branch.Id && t.IsActive && t.PhotoX != null, cancellationToken))
        {
            return;
        }

        var tables = await db.DiningTables
            .Where(t => t.BranchId == branch.Id && t.IsActive && t.PhotoX == null)
            .ToListAsync(cancellationToken);

        foreach (var table in tables)
        {
            var centreX = (table.X + (table.Width / 2d)) / branch.FloorWidth;
            var centreY = (table.Y + (table.Height / 2d)) / branch.FloorHeight;

            table.PlaceOnPhoto(
                Math.Round(0.1 + (0.8 * Math.Clamp(centreX, 0d, 1d)), 3),
                Math.Round(0.25 + (0.6 * Math.Clamp(centreY, 0d, 1d)), 3));
        }

        if (tables.Count > 0)
        {
            logger.LogInformation("Placed {Count} development tables on the cover photo.", tables.Count);
        }
    }

    private async Task EnsureReviewsAsync(Branch branch, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var diners = await db.DinerUsers
            .Where(d => ReviewerPhones.Contains(d.PhoneE164))
            .ToListAsync(cancellationToken);

        var reviewed = await db.BranchReviews
            .Where(r => r.BranchId == branch.Id)
            .Select(r => r.DinerUserId)
            .ToListAsync(cancellationToken);

        for (var i = 0; i < ReviewerPhones.Length; i++)
        {
            var diner = diners.FirstOrDefault(d => d.PhoneE164 == ReviewerPhones[i]);

            if (diner is null)
            {
                diner = new DinerUser(ReviewerPhones[i], "en", $"Dev Reviewer {i + 1}");
                diner.MarkPhoneVerified(nowUtc);
                db.DinerUsers.Add(diner);
            }

            if (reviewed.Contains(diner.Id))
            {
                continue;
            }

            var (rating, text) = Reviews[i];
            db.BranchReviews.Add(new BranchReview(branch.Id, diner.Id, rating, text, nowUtc.AddDays(-(i * 6) - 1)));
        }
    }
}
