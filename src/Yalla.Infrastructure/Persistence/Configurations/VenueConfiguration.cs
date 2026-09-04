using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class VenueConfiguration : EntityConfiguration<Venue>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("Venues");

        builder.Property(v => v.Name)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        builder.Property(v => v.Slug)
            .HasMaxLength(FieldLengths.Slug)
            .IsRequired();

        builder.Property(v => v.Type)
            .IsRequired();

        builder.Property(v => v.IsActive)
            .IsRequired();

        builder.HasIndex(v => v.Slug)
            .IsUnique();
    }
}
