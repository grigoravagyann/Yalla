using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Menus;

/// <summary>
/// One thing a guest can order, with everything a guest normally asks a waiter about it.
/// </summary>
/// <remarks>
/// <see cref="Ingredients"/>, <see cref="Allergens"/>, <see cref="PortionSize"/>,
/// <see cref="PrepMinutes"/> and <see cref="PhotoUrl"/> are <b>required, not optional</b>.
/// Nearly every question a diner puts to a waiter - what is in it, how big is it, how long will
/// it take, does it have nuts - is static data that belongs on the item. Making these nullable
/// guarantees they stay empty and the app stays unable to answer.
/// </remarks>
public sealed class MenuItem : Entity
{
    public Guid MenuCategoryId { get; private set; }

    public MenuCategory MenuCategory { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    /// <summary>
    /// Price in whole Armenian dram. The dram has no subunit in practice, so money is a
    /// <see cref="long"/> count of whole dram and never a decimal or a float.
    /// </summary>
    public long PriceAmd { get; private set; }

    public string PhotoUrl { get; private set; } = null!;

    /// <summary>What is in it, as shown to the diner.</summary>
    public string Ingredients { get; private set; } = null!;

    /// <summary>Allergens present, as shown to the diner.</summary>
    public string Allergens { get; private set; } = null!;

    /// <summary>How much arrives, e.g. 350 ml or 220 g.</summary>
    public string PortionSize { get; private set; } = null!;

    public SpiceLevel SpiceLevel { get; private set; }

    /// <summary>Rough time from order to service, so the app can set expectations.</summary>
    public int PrepMinutes { get; private set; }

    public bool IsAvailable { get; private set; }

    public int DisplayOrder { get; private set; }

    private MenuItem()
    {
    }

    public MenuItem(
        Guid menuCategoryId,
        string name,
        string description,
        long priceAmd,
        string photoUrl,
        string ingredients,
        string allergens,
        string portionSize,
        int prepMinutes,
        SpiceLevel spiceLevel = SpiceLevel.NotSpicy,
        int displayOrder = 0)
        : base(Guid.CreateVersion7())
    {
        MenuCategoryId = Guard.NotEmpty(menuCategoryId, nameof(menuCategoryId));
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        Description = Guard.NotBlank(description, nameof(description), FieldLengths.Description);
        PriceAmd = Guard.NotNegativeAmd(priceAmd, nameof(priceAmd));
        PhotoUrl = Guard.NotBlank(photoUrl, nameof(photoUrl), FieldLengths.Url);
        Ingredients = Guard.NotBlank(ingredients, nameof(ingredients), FieldLengths.Ingredients);
        Allergens = Guard.NotBlank(allergens, nameof(allergens), FieldLengths.Allergens);
        PortionSize = Guard.NotBlank(portionSize, nameof(portionSize), FieldLengths.PortionSize);
        PrepMinutes = Guard.Positive(prepMinutes, nameof(prepMinutes));
        SpiceLevel = Guard.Defined(spiceLevel, nameof(spiceLevel));
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
        IsAvailable = true;
    }

    public void SetPrice(long priceAmd) => PriceAmd = Guard.NotNegativeAmd(priceAmd, nameof(priceAmd));

    public void SetAvailable(bool isAvailable) => IsAvailable = isAvailable;

    public void SetDisplayOrder(int displayOrder) =>
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
}
