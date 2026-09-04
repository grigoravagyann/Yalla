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

        builder.Property(i => i.Description)
            .HasMaxLength(FieldLengths.Description)
            .IsRequired();

        // Whole Armenian dram, so bigint.
        builder.Property(i => i.PriceAmd).IsRequired();

        // Required, not optional: these five are what a diner would otherwise have to ask a
        // waiter, and nullable columns here would simply stay empty.
        builder.Property(i => i.PhotoUrl)
            .HasMaxLength(FieldLengths.Url)
            .IsRequired();

        builder.Property(i => i.Ingredients)
            .HasMaxLength(FieldLengths.Ingredients)
            .IsRequired();

        builder.Property(i => i.Allergens)
            .HasMaxLength(FieldLengths.Allergens)
            .IsRequired();

        builder.Property(i => i.PortionSize)
            .HasMaxLength(FieldLengths.PortionSize)
            .IsRequired();

        builder.Property(i => i.PrepMinutes).IsRequired();

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
