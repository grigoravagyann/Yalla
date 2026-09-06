using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Menus;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class MenuItemConfiguration : EntityConfiguration<MenuItem>
{
    protected override void ConfigureEntity(EntityTypeBuilder<MenuItem> builder)
    {
        builder.ToTable("MenuItems");

        builder.Property(i => i.Name)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        // Nullable, all six of them, since Prompt 10. They are what a diner would otherwise have to
        // ask a waiter and they are still required - but to go live, not to save. Requiring them
        // here meant an eighty-dish menu could not be typed in without eighty photographs first.
        // MenuItemCompleteness is the rule; the readiness projection, the going-live gate and the
        // diner-facing menu are where it is enforced.
        builder.Property(i => i.Description)
            .HasMaxLength(FieldLengths.Description);

        // Whole Armenian dram, so bigint.
        builder.Property(i => i.PriceAmd).IsRequired();

        builder.HasOne(i => i.Photo)
            .WithMany()
            .HasForeignKey(i => i.PhotoId)
            .IsRequired(false)

            // Restrict: a photo a menu references cannot be deleted out from under it, which is also
            // what stops the orphan sweep from taking one that is in use.
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(i => i.Ingredients)
            .HasMaxLength(FieldLengths.Ingredients);

        builder.Property(i => i.Allergens)
            .HasMaxLength(FieldLengths.Allergens);

        builder.Property(i => i.PortionSize)
            .HasMaxLength(FieldLengths.PortionSize);

        builder.Property(i => i.SpiceLevel).IsRequired();
        builder.Property(i => i.IsAvailable).IsRequired();
        builder.Property(i => i.DisplayOrder).IsRequired();

        // Cascade: an item has no meaning apart from its category. Note that an item already
        // referenced by an order line still cannot be deleted - that FK restricts.
        builder.HasOne(i => i.MenuCategory)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.MenuCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.MenuCategoryId, i.DisplayOrder });
    }
}
