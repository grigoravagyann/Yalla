using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TableSessionConfiguration : EntityConfiguration<TableSession>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TableSession> builder)
    {
        builder.ToTable("TableSessions");

        builder.Property(s => s.Source).IsRequired();
        builder.Property(s => s.PartySize).IsRequired();
        builder.Property(s => s.SeatedAtUtc).IsRequired();
        builder.Property(s => s.ClosedAtUtc);

        // A plain column, not a foreign key: Tab.TableSessionId is the real key in the other
        // direction, and an opposing key here would make the pair circular and uninsertable.
        builder.Property(s => s.TabId);

        builder.Ignore(s => s.Duration);
        builder.Ignore(s => s.IsOpen);

        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.DiningTable)
            .WithMany()
            .HasForeignKey(s => s.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null for every walk-in, which is most rows in a cafe.
        builder.HasOne(s => s.Reservation)
            .WithMany()
            .HasForeignKey(s => s.ReservationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.SeatedByStaff)
            .WithMany()
            .HasForeignKey(s => s.SeatedByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        // Occupancy history for one table, newest last: the turnover reporting reads this.
        builder.HasIndex(s => new { s.DiningTableId, s.SeatedAtUtc });

        // Filtered on the open sessions only. The set of currently occupied tables is tiny next
        // to the full history, and it is queried constantly - every floor plan refresh, every
        // QR scan - so it gets its own narrow index instead of scanning years of closed rows.
        builder.HasIndex(s => new { s.BranchId, s.DiningTableId })
            .HasDatabaseName("IX_TableSessions_Open")
            .HasFilter("[ClosedAtUtc] IS NULL");
    }
}
