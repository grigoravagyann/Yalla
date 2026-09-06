using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabConfiguration : EntityConfiguration<Tab>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Tab> builder)
    {
        builder.ToTable("Tabs");

        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.OpenedAtUtc).IsRequired();
        builder.Property(t => t.ClosedAtUtc);
        builder.Property(t => t.SettlementMode).IsRequired();
        builder.Property(t => t.SettlementModeLockedAtUtc);
        builder.Property(t => t.HideTotalFromGuests).IsRequired();

        // Not a foreign key: TabParticipant.TabId is the real key in the other direction.
        builder.Property(t => t.HostParticipantId);

        builder.Property(t => t.ClientCommandId).IsRequired();

        builder.Property(t => t.ServiceChargePercentSnapshot)
            .HasPrecision(5, 2)
            .IsRequired();

        // Money is a whole count of Armenian dram, so bigint - never decimal, never float.
        // These five are server-computed; the client only ever displays them.
        builder.Property(t => t.SubtotalAmd).IsRequired();
        builder.Property(t => t.ServiceChargeAmd).IsRequired();
        builder.Property(t => t.TotalAmd).IsRequired();
        builder.Property(t => t.PaidAmd).IsRequired();
        builder.Property(t => t.RemainingAmd).IsRequired();

        // What makes concurrent payments safe: a reservation against RemainingAmd is taken under
        // this token, so two people paying at once cannot both claim the same dram.
        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        builder.Ignore(t => t.AcceptsNewParticipants);
        builder.Ignore(t => t.IsActive);

        builder.HasOne(t => t.Branch)
            .WithMany()
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.DiningTable)
            .WithMany()
            .HasForeignKey(t => t.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        // Required: a bill without a seating is meaningless. Restrict, because financial records
        // must outlive any tidying up of occupancy history.
        builder.HasOne(t => t.TableSession)
            .WithMany()
            .HasForeignKey(t => t.TableSessionId)
            .OnDelete(DeleteBehavior.Restrict);

        // The staff app's live list: open tabs at this branch.
        builder.HasIndex(t => new { t.BranchId, t.Status });

        // Reporting. Revenue is counted on tabs that closed inside the range, so this is the index
        // the money reports seek on. Filtered to closed tabs: an open tab has no revenue to report
        // and there is no point carrying every live tab in the branch through this index.
        builder.HasIndex(t => new { t.BranchId, t.ClosedAtUtc })
            .HasFilter("[ClosedAtUtc] IS NOT NULL")
            .HasDatabaseName("IX_Tabs_BranchId_ClosedAtUtc");

        // One tab per seating, enforced by the database. Two phones at a table that has a session
        // but no tab yet both try to attach one; the first wins and the second is caught and
        // turned into "join the existing tab". Without this a service-level check would let both
        // through, and the party would have two bills.
        builder.HasIndex(t => t.TableSessionId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TabPerSession);

        // The idempotency guarantee for opening a tab: a double scan or a retry on flaky wifi
        // returns the tab the first attempt opened. Same mechanism, and the same argument, as on
        // TableStateChanges and Reservations.
        builder.HasIndex(t => t.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TabClientCommand);
    }
}
