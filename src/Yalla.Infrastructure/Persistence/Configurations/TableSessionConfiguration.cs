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

        // Reporting. The occupancy reports are all "this branch, seated in this range" - by hour,
        // by weekday, turn time, walk-in versus booking - and without this every one of them scans
        // the branch's whole history. Source and ClosedAtUtc are included because turn time is
        // ClosedAtUtc - SeatedAtUtc and the walk-in split is a group by Source, so the range scan
        // answers both without a lookup per row.
        builder.HasIndex(s => new { s.BranchId, s.SeatedAtUtc })
            .IncludeProperties(s => new { s.ClosedAtUtc, s.Source, s.PartySize })
            .HasDatabaseName("IX_TableSessions_BranchId_SeatedAtUtc_Reporting");

        // At most one open session per table, enforced by the database rather than by service
        // logic alone. This is the backstop for the double-seat: if the state machine is
        // bypassed, or two writers get past the table's RowVersion somehow, SQL Server still
        // refuses to have two parties sitting at table 7.
        //
        // Filtered, so it costs almost nothing: the set of currently open sessions is tiny next
        // to years of closed history, and it is the set every floor refresh and QR scan reads.
        builder.HasIndex(s => s.DiningTableId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.OpenSessionPerTable)
            .HasFilter("[ClosedAtUtc] IS NULL");

        // Branch-wide "who is sitting down right now", for the floor query.
        builder.HasIndex(s => s.BranchId)
            .HasDatabaseName("IX_TableSessions_OpenByBranch")
            .HasFilter("[ClosedAtUtc] IS NULL");
    }
}
