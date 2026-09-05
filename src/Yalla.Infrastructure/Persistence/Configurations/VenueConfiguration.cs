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

        // Both null for a live venue. Suspension clears; deletion never does.
        builder.Property(v => v.SuspendedAtUtc);
        builder.Property(v => v.DeletedAtUtc);

        builder.Ignore(v => v.IsSuspended);
        builder.Ignore(v => v.IsDeleted);
        builder.Ignore(v => v.IsBrowsable);

        // Named so the platform service can recognise the violation and answer "that slug is
        // taken" rather than a 500. Same name EF would have generated.
        builder.HasIndex(v => v.Slug)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.VenueSlug);
    }
}
