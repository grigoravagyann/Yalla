using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Menus;

/// <summary>
/// One thing a guest can order, with everything a guest normally asks a waiter about it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Description"/>, <see cref="Ingredients"/>, <see cref="Allergens"/>,
/// <see cref="PortionSize"/>, <see cref="PrepMinutes"/> and <see cref="PhotoId"/> are
/// <b>required to go live, and optional to save</b>. Nearly every question a diner puts to a
/// waiter - what is in it, how big is it, how long will it take, does it have nuts - is static
/// data that belongs on the item, so an item missing any of it is not fit to show a diner.
/// </para>
/// <para>
/// That rule used to be enforced on create, and it made an eighty-dish menu unenterable: the whole
/// menu had to be photographed before a single name and price could be typed, which is not the
/// order that work happens in. The rule did not move because it was wrong - it moved because the
/// enforcement point was. <see cref="MenuItemCompleteness"/> is now the definition, the branch
/// readiness projection counts what is missing, going Paid is refused while anything is missing,
/// and the diner-facing menu drops incomplete items entirely. See <c>docs/menu-completeness.md</c>.
/// </para>
/// <para>
/// <see cref="Name"/>, <see cref="PriceAmd"/> and <see cref="MenuCategoryId"/> stay required.
/// Something without a name, a price and a place on the menu is not a partially entered item; it
/// is not an item.
/// </para>
/// </remarks>
public sealed class MenuItem : Entity
{
    public Guid MenuCategoryId { get; private set; }

    public MenuCategory MenuCategory { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>How the dish is described on the menu. Null until somebody writes it.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Price in whole Armenian dram. The dram has no subunit in practice, so money is a
    /// <see cref="long"/> count of whole dram and never a decimal or a float.
    /// </summary>
    public long PriceAmd { get; private set; }

    /// <summary>
    /// The uploaded photo. A foreign key rather than a string since Prompt 9, and nullable since
    /// Prompt 10.
    /// </summary>
    /// <remarks>
    /// It was a <c>PhotoUrl</c>, which is the reason no venue could be onboarded: the field was
    /// required and nothing in the product could produce a value for it. A row instead of a string
    /// also means the image can be swept when nothing references it, and that moving storage is a
    /// change to one implementation rather than a rewrite of every URL in the database. It is null
    /// while the dish has been typed in and not yet photographed - a real state, and the one the
    /// team is in for the first week of every venue.
    /// </remarks>
    public Guid? PhotoId { get; private set; }

    public Media.Photo? Photo { get; private set; }

    /// <summary>What is in it, as shown to the diner.</summary>
    public string? Ingredients { get; private set; }

    /// <summary>Allergens present, as shown to the diner.</summary>
    public string? Allergens { get; private set; }

    /// <summary>How much arrives, e.g. 350 ml or 220 g.</summary>
    public string? PortionSize { get; private set; }

    public SpiceLevel SpiceLevel { get; private set; }

    /// <summary>Rough time from order to service, so the app can set expectations.</summary>
    public int? PrepMinutes { get; private set; }

    public bool IsAvailable { get; private set; }

    public int DisplayOrder { get; private set; }

    /// <summary>
    /// Whether this item may be shown to a diner. Computed, never stored.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="MenuItemCompleteness"/> rather than restating the test, because the
    /// readiness projection and the diner menu filter apply the same rule in SQL and all three have
    /// to mean the same thing.
    /// </remarks>
    public bool IsComplete => MenuItemCompleteness.IsComplete(this);

    private MenuItem()
    {
    }

    public MenuItem(
        Guid menuCategoryId,
        string name,
        string? description,
        long priceAmd,
        Guid? photoId,
        string? ingredients,
        string? allergens,
        string? portionSize,
        int? prepMinutes,
        SpiceLevel spiceLevel = SpiceLevel.NotSpicy,
        int displayOrder = 0)
        : base(Guid.CreateVersion7())
    {
        MenuCategoryId = Guard.NotEmpty(menuCategoryId, nameof(menuCategoryId));
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        PriceAmd = Guard.NotNegativeAmd(priceAmd, nameof(priceAmd));
        SpiceLevel = Guard.Defined(spiceLevel, nameof(spiceLevel));
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
        IsAvailable = true;

        Describe(description, photoId, ingredients, allergens, portionSize, prepMinutes);
    }

    /// <summary>
    /// Changes the price. Order lines already placed snapshotted the price they were ordered at
    /// and are untouched by this - a price rise tomorrow cannot change a bill presented today.
    /// </summary>
    public void SetPrice(long priceAmd) => PriceAmd = Guard.NotNegativeAmd(priceAmd, nameof(priceAmd));

    /// <summary>
    /// Moves the item to another category.
    /// </summary>
    /// <remarks>
    /// The caller checks the category is on the same branch; this only records the move. Existing
    /// order lines are unaffected - they snapshot the name and price and never reference a
    /// category - so a dish can be reorganised on the menu without touching a bill.
    /// </remarks>
    public void MoveToCategory(Guid menuCategoryId, int displayOrder)
    {
        MenuCategoryId = Guard.NotEmpty(menuCategoryId, nameof(menuCategoryId));
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
    }

    /// <summary>
    /// Edits the descriptive fields together.
    /// </summary>
    /// <remarks>
    /// Every one of them may be null, exactly as on create. A caller patching one field passes the
    /// item's current values for the rest, so "leave it as it was" and "it was never filled in"
    /// stay the same thing - and an item that is half entered can be saved again without the edit
    /// insisting on the half that is missing.
    /// </remarks>
    public void UpdateDetails(
        string name,
        string? description,
        Guid? photoId,
        string? ingredients,
        string? allergens,
        string? portionSize,
        int? prepMinutes,
        SpiceLevel spiceLevel)
    {
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        SpiceLevel = Guard.Defined(spiceLevel, nameof(spiceLevel));

        Describe(description, photoId, ingredients, allergens, portionSize, prepMinutes);
    }

    public void SetAvailable(bool isAvailable) => IsAvailable = isAvailable;

    public void SetDisplayOrder(int displayOrder) =>
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));

    /// <summary>
    /// Applies the six fields completeness is measured on, with the checks that still hold.
    /// </summary>
    /// <remarks>
    /// Optional is not the same as unchecked. A supplied photo id must be a real id rather than
    /// <see cref="Guid.Empty"/>, a supplied prep time must be positive, and supplied text is
    /// trimmed and length-checked - <c>Guard.OptionalText</c> returns null for whitespace, which is
    /// what lets <see cref="MenuItemCompleteness"/> test for null alone.
    /// </remarks>
    private void Describe(
        string? description,
        Guid? photoId,
        string? ingredients,
        string? allergens,
        string? portionSize,
        int? prepMinutes)
    {
        Description = Guard.OptionalText(description, nameof(description), FieldLengths.Description);
        PhotoId = photoId is { } id ? Guard.NotEmpty(id, nameof(photoId)) : null;
        Ingredients = Guard.OptionalText(ingredients, nameof(ingredients), FieldLengths.Ingredients);
        Allergens = Guard.OptionalText(allergens, nameof(allergens), FieldLengths.Allergens);
        PortionSize = Guard.OptionalText(portionSize, nameof(portionSize), FieldLengths.PortionSize);
        PrepMinutes = prepMinutes is { } minutes ? Guard.Positive(minutes, nameof(prepMinutes)) : null;
    }
}
