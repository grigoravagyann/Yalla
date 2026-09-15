using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class BranchGalleryPhotoConfiguration : EntityConfiguration<BranchGalleryPhoto>
{
    protected override void ConfigureEntity(EntityTypeBuilder<BranchGalleryPhoto> builder)
    {
        builder.ToTable("BranchGalleryPhotos");

        builder.Property(g => g.Position).IsRequired();

        // A gallery entry is nothing without its branch.
        builder.HasOne(g => g.Branch)
            .WithMany()
            .HasForeignKey(g => g.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, like every other photo reference: a picture a gallery shows must not be
        // deletable out from under it.
        builder.HasOne(g => g.Photo)
            .WithMany()
            .HasForeignKey(g => g.PhotoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.BranchId, g.PhotoId }).IsUnique();

        builder.HasIndex(g => new { g.BranchId, g.Position });
    }
}
