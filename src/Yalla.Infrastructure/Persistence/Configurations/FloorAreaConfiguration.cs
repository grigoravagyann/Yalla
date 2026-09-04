using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class FloorAreaConfiguration : EntityConfiguration<FloorArea>
{
    protected override void ConfigureEntity(EntityTypeBuilder<FloorArea> builder)
    {
        builder.ToTable("FloorAreas");

        builder.Property(a => a.Name)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        builder.Property(a => a.DisplayOrder)
            .IsRequired();

        // Restrict: tables point at areas, and deleting an area must not take the floor plan
        // with it.
        builder.HasOne(a => a.Branch)
            .WithMany(b => b.FloorAreas)
            .HasForeignKey(a => a.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.BranchId, a.DisplayOrder });
    }
}
