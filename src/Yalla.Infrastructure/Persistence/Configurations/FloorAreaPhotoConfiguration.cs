using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class FloorAreaPhotoConfiguration : EntityConfiguration<FloorAreaPhoto>
{
    protected override void ConfigureEntity(EntityTypeBuilder<FloorAreaPhoto> builder)
    {
        builder.ToTable("FloorAreaPhotos");

        builder.Property(p => p.Position).IsRequired();

        // A room's picture is nothing without its room: deleting the room takes its photo rows with
        // it. The tables pinned on them are taken off first, by the service that deletes the room.
        builder.HasOne(p => p.FloorArea)
            .WithMany(a => a.Photos)
            .HasForeignKey(p => p.FloorAreaId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, like every other photo reference: a picture a room shows must not be deletable
        // out from under it.
        builder.HasOne(p => p.Photo)
            .WithMany()
            .HasForeignKey(p => p.PhotoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.FloorAreaId, p.PhotoId }).IsUnique();

        builder.HasIndex(p => new { p.FloorAreaId, p.Position });
    }
}
