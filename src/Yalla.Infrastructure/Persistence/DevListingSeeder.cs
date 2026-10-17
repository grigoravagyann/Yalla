using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Makes the seeded demo branch look like a real listing in the diner app: cuisine, about, price
/// level, website, a cover and gallery, a photo for each room with its tables pinned on it, bookings
/// open, and a handful of reviews.
/// </summary>
/// <remarks>
/// <para>
/// Development only. It is called by <see cref="DevDataSeeder"/>, which is registered only when the
/// host is Development and <c>DevSeed:Enabled</c> is true - so it inherits exactly that gate.
/// <c>DevActor:Enabled</c> plays no part: the demo listing is there with the actor stub off.
/// </para>
/// <para>
/// Idempotent and conservative: it only ever touches the branch the seeder owns (passed in, found by
/// slug), fills listing fields only when all of them - amenities included - are still empty, adds the
/// pictures only while the branch has no cover, opens bookings only while nobody has saved the
/// reservation policy, gives the rooms their photos and pins the tables only while no room has a
/// photo yet, and adds a review only for a dev reviewer who has none. A person's edits to the demo
/// branch survive every restart; what it cannot tell from "never set" is a person emptying every
/// listing field, clearing the cover, or removing every room photo, and those it fills again.
/// </para>
/// </remarks>
internal sealed class DevListingSeeder(
    YallaDbContext db,
    IClock clock,
    IPhotoStorage storage,
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

    /// <summary>The embedded cover picture - see <c>DevSeed/README.md</c> for how it was made.</summary>
    internal const string CoverResource = "Yalla.DevSeed.cover.jpg";

    /// <summary>The embedded gallery pictures, in gallery order.</summary>
    internal static readonly string[] GalleryResources = ["Yalla.DevSeed.gallery-1.jpg", "Yalla.DevSeed.gallery-2.jpg"];

    /// <summary>
    /// Each seeded room's photo, and where on it each of the room's tables is pinned. The positions
    /// are where <c>generate-images.cs</c> draws the tables, so the pins land on them.
    /// </summary>
    /// <remarks>
    /// The room photo for Windows is the same picture as the cover: one photo row, which the cover and
    /// the room both point at - the seed reuses a row by content hash exactly as an upload does. On the
    /// terrace picture three tables are drawn; table 8, the long one, is pinned in the open space
    /// between them so the demo still offers a table that needs approval.
    /// </remarks>
    internal static readonly (string Room, string Resource, (string Label, double X, double Y)[] Pins)[] RoomPhotos =
    [
        ("Windows", CoverResource, [("1", 0.200, 0.366), ("2", 0.312, 0.366), ("3", 0.424, 0.366), ("4", 0.536, 0.366)]),
        ("Terrace", "Yalla.DevSeed.gallery-2.jpg", [("5", 0.173, 0.790), ("6", 0.500, 0.790), ("7", 0.827, 0.790), ("8", 0.500, 0.560)]),
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
        await EnsurePicturesAsync(branch, cancellationToken);
        OpenForBookings(branch);
        await PinTablesInRoomsAsync(branch, cancellationToken);
        await EnsureReviewsAsync(branch, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private void FillListing(Branch branch)
    {
        // Every listing field, amenities and district included: the seed writes all of them at once,
        // so a person who set only one would otherwise lose it to the demo text on the next restart.
        if (branch.Cuisine is not null || branch.About is not null
            || branch.PriceLevel is not null || branch.WebsiteUrl is not null
            || branch.AmenityKeys is not null || branch.District is not null)
        {
            return;
        }

        branch.UpdateListing(
            cuisine: "Armenian & Mediterranean",
            about: "A bright corner cafe on Abovyan Street with window seats, a shaded terrace and "
                   + "an all-day menu of Armenian breakfasts, grills and fresh pastries.",
            priceLevel: 2,
            websiteUrl: "https://example.com/yalla-demo",
            amenities: null,
            district: "Kentron");

        logger.LogInformation("Seeding development listing for branch {BranchId}.", branch.Id);
    }

    /// <summary>
    /// Gives a branch with no cover the embedded cover, and a gallery when it has none either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through <see cref="IPhotoStorage"/>, exactly as an upload is stored: decoded, stripped and
    /// written as the three variants, so every URL the diner app is handed answers. A row pointing at
    /// files that were never written - which is what a seed that inserted rows would produce - is a
    /// broken image rather than a picture. Not through <c>IPhotoService.UploadAsync</c>, which needs a
    /// signed-in manager at the branch and there is nobody signed in at startup; the row it would write
    /// is written here the same way, reusing one with the same content hash.
    /// </para>
    /// <para>
    /// Nothing at all while a cover exists, so a manager's own cover and gallery are never touched.
    /// </para>
    /// </remarks>
    private async Task EnsurePicturesAsync(Branch branch, CancellationToken cancellationToken)
    {
        if (branch.CoverPhotoId is not null)
        {
            return;
        }

        var cover = await StoreAsync(branch.Id, CoverResource, cancellationToken);

        var hasGallery = await db.BranchGalleryPhotos.AnyAsync(g => g.BranchId == branch.Id, cancellationToken);
        var gallery = new List<Photo>();

        if (!hasGallery)
        {
            foreach (var resource in GalleryResources)
            {
                gallery.Add(await StoreAsync(branch.Id, resource, cancellationToken));
            }
        }

        // The photo rows first. A branch points at its cover and the cover points back at its
        // branch, and one SaveChanges holding both the insert and the pointer is a cycle to order.
        await db.SaveChangesAsync(cancellationToken);

        branch.SetCoverPhoto(cover.Id);

        for (var position = 0; position < gallery.Count; position++)
        {
            db.BranchGalleryPhotos.Add(new BranchGalleryPhoto(branch.Id, gallery[position].Id, position));
        }

        logger.LogInformation(
            "Seeded a development cover and {GalleryCount} gallery picture(s) for branch {BranchId}.",
            gallery.Count, branch.Id);
    }

    /// <summary>Stores one embedded picture for the branch and returns its row, new or reused.</summary>
    private async Task<Photo> StoreAsync(Guid branchId, string resource, CancellationToken cancellationToken)
    {
        await using var content = typeof(DevListingSeeder).Assembly.GetManifestResourceStream(resource)
                                  ?? throw new InvalidOperationException(
                                      $"The development seed picture '{resource}' is not embedded in "
                                      + "Yalla.Infrastructure. See DevSeed/README.md.");

        var stored = await storage.SaveAsync(
            PhotoRules.OwnerKeyForBranch(branchId), content, "image/jpeg", cancellationToken);

        // The same rule as an upload: identical bytes for this branch are one row, never two rows over
        // one set of files.
        var existing = db.Photos.Local.FirstOrDefault(p => p.BranchId == branchId && p.ContentHash == stored.ContentHash)
                       ?? await db.Photos.FirstOrDefaultAsync(
                           p => p.BranchId == branchId && p.ContentHash == stored.ContentHash, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var photo = new Photo(
            branchId,
            stored.ContentHash,
            stored.ThumbnailPath,
            stored.CardPath,
            stored.FullPath,
            stored.Width,
            stored.Height,
            stored.BytesStored,
            clock.UtcNow);

        db.Photos.Add(photo);

        return photo;
    }

    /// <summary>
    /// Opens the demo branch to bookings from the app and the web page: online bookings on, and the
    /// reservation policy marked reviewed (K9's <c>acceptsAppBookings</c> needs both).
    /// </summary>
    /// <remarks>
    /// Only while nobody has saved the policy. Saving it is what a manager does on the settings
    /// screen, and the booking switch sits beside it - so once somebody has, whatever they chose,
    /// bookings on or off, is theirs.
    /// </remarks>
    private void OpenForBookings(Branch branch)
    {
        if (branch.ReservationPolicyReviewedAtUtc is not null)
        {
            return;
        }

        branch.UpdateReservationPolicy(branch.ReservationPolicy, clock.UtcNow);
        branch.SetAcceptsWebBookings(true);

        logger.LogInformation("Opened development branch {BranchId} to bookings.", branch.Id);
    }

    /// <summary>
    /// Gives each seeded room its photo and pins the room's tables on it, so the diner app has rooms
    /// to swipe and pins to tap.
    /// </summary>
    /// <remarks>
    /// Once per branch: any room photo at all means somebody - this seed or a person - has already laid
    /// the rooms out, and a manager's choices are never redone. A room or a table the seed does not
    /// find by name or label, or one that is inactive, is skipped rather than recreated.
    /// </remarks>
    private async Task PinTablesInRoomsAsync(Branch branch, CancellationToken cancellationToken)
    {
        if (await db.FloorAreaPhotos.AnyAsync(p => p.FloorArea.BranchId == branch.Id, cancellationToken))
        {
            return;
        }

        var rooms = await db.FloorAreas.Where(a => a.BranchId == branch.Id).ToListAsync(cancellationToken);
        var tables = await db.DiningTables.Where(t => t.BranchId == branch.Id && t.IsActive).ToListAsync(cancellationToken);

        var plan = new List<(FloorArea Room, Photo Photo, (string Label, double X, double Y)[] Pins)>();

        foreach (var (roomName, resource, pins) in RoomPhotos)
        {
            var room = rooms.FirstOrDefault(r => r.Name == roomName);

            if (room is not null)
            {
                plan.Add((room, await StoreAsync(branch.Id, resource, cancellationToken), pins));
            }
        }

        if (plan.Count == 0)
        {
            return;
        }

        // The photo rows first, for the same reason as the cover: one SaveChanges inserting a photo
        // and the rows that point at it is an order EF has to be told.
        await db.SaveChangesAsync(cancellationToken);

        var pinned = 0;

        foreach (var (room, photo, pins) in plan)
        {
            db.FloorAreaPhotos.Add(new FloorAreaPhoto(room.Id, photo.Id, position: 0));

            foreach (var (label, x, y) in pins)
            {
                var table = tables.FirstOrDefault(t => t.Label == label && t.FloorAreaId == room.Id);

                if (table is not null)
                {
                    table.PlaceOnPhoto(photo.Id, x, y);
                    pinned++;
                }
            }
        }

        logger.LogInformation(
            "Gave {RoomCount} development room(s) a photo and pinned {TableCount} table(s) on them.",
            plan.Count, pinned);
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
