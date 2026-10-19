using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Staff;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class StaffShiftConfiguration : EntityConfiguration<StaffShift>
{
    protected override void ConfigureEntity(EntityTypeBuilder<StaffShift> builder)
    {
        builder.ToTable("StaffShifts", table =>
        {
            // Both times or neither - a day off has none - and EndsNextDay only with times. The entity refuses
            // anything else; this keeps a hand-written row from meaning two things.
            table.HasCheckConstraint(
                "CK_StaffShifts_Times",
                "([StartsAt] IS NULL AND [EndsAt] IS NULL AND [EndsNextDay] = 0) "
                + "OR ([StartsAt] IS NOT NULL AND [EndsAt] IS NOT NULL)");
        });

        // The branch's local calendar date and wall-clock times, as in the opening hours: never converted.
        builder.Property(s => s.Date)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(s => s.StartsAt)
            .HasColumnType("time");

        builder.Property(s => s.EndsAt)
            .HasColumnType("time");

        builder.Property(s => s.EndsNextDay)
            .IsRequired();

        // Configuration of the branch, like its hours: it goes with the branch.
        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not cascade: staff are deactivated, never deleted, and a second cascade path from the venue
        // through the person is one SQL Server refuses.
        builder.HasOne(s => s.StaffMember)
            .WithMany()
            .HasForeignKey(s => s.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        // The Staff screen reads a branch's dates in a window; the overlap check reads one person's dates around
        // one day, at every branch.
        builder.HasIndex(s => new { s.BranchId, s.Date });
        builder.HasIndex(s => new { s.StaffMemberId, s.Date });

        builder.Ignore(s => s.IsDayOff);
        builder.Ignore(s => s.LocalStart);
        builder.Ignore(s => s.LocalEnd);
    }
}
