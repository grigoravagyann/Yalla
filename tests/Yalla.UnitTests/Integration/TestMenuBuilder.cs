using Yalla.Domain.Enums;
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
    public static async Task<TestMenu> CreateAsync(
        YallaDbContext db,
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var category = new MenuCategory(branchId, $"Everything {Guid.NewGuid().ToString("N")[..6]}", 0);

        var coffee = Item(category.Id, "Flat white", TestMenu.CoffeeAmd, prepMinutes: 4);
        var khachapuri = Item(category.Id, "Adjarian khachapuri", TestMenu.KhachapuriAmd, prepMinutes: 20);
        var wine = Item(category.Id, "Areni red, bottle", TestMenu.WineAmd, prepMinutes: 2);
        var soldOut = Item(category.Id, "Lamb kebab", 2_000L, prepMinutes: 25);

        soldOut.SetAvailable(false);

        db.MenuCategories.Add(category);
        db.MenuItems.AddRange(coffee, khachapuri, wine, soldOut);

        await db.SaveChangesAsync(cancellationToken);

        return new TestMenu(category.Id, coffee.Id, khachapuri.Id, wine.Id, soldOut.Id);
    }

    private static MenuItem Item(Guid categoryId, string name, long priceAmd, int prepMinutes) =>
        new(
            categoryId,
            name,
            $"{name}, as the kitchen makes it.",
            priceAmd,
            "https://cdn.example.test/dish.jpg",
            ingredients: "flour, water, salt",
            allergens: "gluten",
            portionSize: "one serving",
            prepMinutes: prepMinutes,
            spiceLevel: SpiceLevel.NotSpicy);
}
