using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Menus;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class MenuCategoryConfiguration : EntityConfiguration<MenuCategory>
{
    protected override void ConfigureEntity(EntityTypeBuilder<MenuCategory> builder)
    {
        builder.ToTable("MenuCategories");

        builder.Property(c => c.Name)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        builder.Property(c => c.DisplayOrder).IsRequired();

        // Restrict: items under a category are referenced by order lines, so a branch cannot be
        // wiped out from the top down.
        builder.HasOne(c => c.Branch)
            .WithMany(b => b.MenuCategories)
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.BranchId, c.DisplayOrder });
    }
}
