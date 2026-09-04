using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class OpeningHoursConfiguration : EntityConfiguration<OpeningHours>
{
    protected override void ConfigureEntity(EntityTypeBuilder<OpeningHours> builder)
    {
        builder.ToTable("OpeningHours");

        builder.Property(h => h.Day)
            .IsRequired();

        // Wall-clock values, mapped to SQL Server time. Never converted to UTC: "we open at
        // nine" must stay nine o'clock across a daylight-saving change.
        builder.Property(h => h.OpensAt)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(h => h.ClosesAt)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(h => h.ClosesNextDay)
            .IsRequired();

        builder.Ignore(h => h.Duration);

        // Opening hours are genuinely dependent on their branch and carry no history worth
        // keeping, so this is one of the few relationships that cascades.
        builder.HasOne(h => h.Branch)
            .WithMany(b => b.OpeningHours)
            .HasForeignKey(h => h.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        // Not unique: a branch may close for the afternoon and reopen, which is two rows for
        // the same day.
        builder.HasIndex(h => new { h.BranchId, h.Day });
    }
}
