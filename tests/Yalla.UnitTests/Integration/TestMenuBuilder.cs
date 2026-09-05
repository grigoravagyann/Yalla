using Yalla.Domain.Enums;
using Yalla.Domain.Media;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>A branch's menu, as the ordering tests need it.</summary>
/// <param name="CategoryId">The one category everything is in.</param>
/// <param name="Coffee">1,200 AMD, 4 minutes.</param>
/// <param name="Khachapuri">3,200 AMD, 20 minutes - the longest prep, so it sets the estimate.</param>
/// <param name="Wine">9,500 AMD, 2 minutes. The thing a table shares.</param>
/// <param name="SoldOut">2,000 AMD and unavailable.</param>
internal sealed record TestMenu(
    Guid CategoryId,
    Guid Coffee,
    Guid Khachapuri,
    Guid Wine,
    Guid SoldOut)
{
    public const long CoffeeAmd = 1_200L;

    public const long KhachapuriAmd = 3_200L;

    public const long WineAmd = 9_500L;

    public const int LongestPrepMinutes = 20;
}

/// <summary>
/// Seeds a small, fixed menu so the arithmetic in a test is readable at a glance.
/// </summary>
/// <remarks>
/// Prices are round and distinct, so a wrong total in a failure message says which item went astray
/// without anyone having to decompose it. One item is deliberately unavailable: sold-out handling is
/// a rule several tests need and it must be seeded, never simulated.
/// </remarks>
internal static class TestMenuBuilder
{
    /// <summary>
    /// A photo row a menu item can point at, without going through an upload.
    /// </summary>
    /// <remarks>
    /// Externally hosted, so it needs no bytes on disk and no storage root: these tests are about
    /// menus and ordering, not about image processing, and the photo pipeline has its own suite.
    /// </remarks>
    public static async Task<Guid> AddPhotoAsync(
        YallaDbContext db,
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var unique = Guid.NewGuid().ToString("N");

        var photo = Photo.ExternallyHosted(
            branchId,
            $"https://cdn.example.test/{unique}.jpg",
            unique + unique,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        db.Photos.Add(photo);
        await db.SaveChangesAsync(cancellationToken);

        return photo.Id;
    }

    public static async Task<TestMenu> CreateAsync(
        YallaDbContext db,
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var category = new MenuCategory(branchId, $"Everything {Guid.NewGuid().ToString("N")[..6]}", 0);
        var photoId = await AddPhotoAsync(db, branchId, cancellationToken);

        var coffee = Item(category.Id, "Flat white", TestMenu.CoffeeAmd, 4, photoId);
        var khachapuri = Item(category.Id, "Adjarian khachapuri", TestMenu.KhachapuriAmd, 20, photoId);
        var wine = Item(category.Id, "Areni red, bottle", TestMenu.WineAmd, 2, photoId);
        var soldOut = Item(category.Id, "Lamb kebab", 2_000L, 25, photoId);

        soldOut.SetAvailable(false);

        db.MenuCategories.Add(category);
        db.MenuItems.AddRange(coffee, khachapuri, wine, soldOut);

        await db.SaveChangesAsync(cancellationToken);

        return new TestMenu(category.Id, coffee.Id, khachapuri.Id, wine.Id, soldOut.Id);
    }

    private static MenuItem Item(Guid categoryId, string name, long priceAmd, int prepMinutes, Guid photoId) =>
        new(
            categoryId,
            name,
            $"{name}, as the kitchen makes it.",
            priceAmd,
            photoId,
            ingredients: "flour, water, salt",
            allergens: "gluten",
            portionSize: "one serving",
            prepMinutes: prepMinutes,
            spiceLevel: SpiceLevel.NotSpicy);
}
