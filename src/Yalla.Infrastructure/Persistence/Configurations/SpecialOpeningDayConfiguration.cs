using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class SpecialOpeningDayConfiguration : EntityConfiguration<SpecialOpeningDay>
{
    protected override void ConfigureEntity(EntityTypeBuilder<SpecialOpeningDay> builder)
    {
        builder.ToTable("SpecialOpeningDays");

        // The branch's local calendar date, as SQL Server date: never converted to anything.
        builder.Property(d => d.Date)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(d => d.Note)
            .HasMaxLength(SpecialOpeningDay.MaxNoteLength);

        // Like the weekly hours: nothing but configuration, dependent on its branch.
        builder.HasOne(d => d.Branch)
            .WithMany(b => b.SpecialOpeningDays)
            .HasForeignKey(d => d.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(d => d.Blocks)
            .WithOne()
            .HasForeignKey(b => b.SpecialOpeningDayId)
            .OnDelete(DeleteBehavior.Cascade);

        // One special day per date: two rows for New Year's Day would be two answers to "are you open".
        // It also serves every reader, which asks for a branch's dates in a window.
        builder.HasIndex(d => new { d.BranchId, d.Date }).IsUnique();
    }
}

internal sealed class SpecialOpeningBlockConfiguration : EntityConfiguration<SpecialOpeningBlock>
{
    protected override void ConfigureEntity(EntityTypeBuilder<SpecialOpeningBlock> builder)
    {
        builder.ToTable("SpecialOpeningBlocks");

        // Wall-clock values, as in OpeningHours.
        builder.Property(b => b.OpensAt)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(b => b.ClosesAt)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(b => b.ClosesNextDay)
            .IsRequired();

        builder.HasIndex(b => b.SpecialOpeningDayId);
    }
}
