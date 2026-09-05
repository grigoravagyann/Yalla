using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabAdjustmentConfiguration : EntityConfiguration<TabAdjustment>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabAdjustment> builder)
    {
        builder.ToTable("TabAdjustments");

        builder.Property(a => a.Kind).IsRequired();

        // The only decimals in the money model, and they never leave this table without being
        // turned into whole dram by Money.PercentOf. 5,2 covers 100.00 with room for 12.50.
        builder.Property(a => a.Percent).HasPrecision(5, 2);

        builder.Property(a => a.AmountAmd);

        builder.Property(a => a.Reason)
            .HasMaxLength(FieldLengths.Reason)
            .IsRequired();

        builder.Property(a => a.VoidedAtUtc);

        builder.Ignore(a => a.IsActive);

        builder.HasOne(a => a.Tab)
            .WithMany()
            .HasForeignKey(a => a.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null for a whole-tab adjustment. Restrict, like every other financial record: a comp is
        // part of what happened and must not disappear because a line did.
        builder.HasOne(a => a.TabOrderLine)
            .WithMany()
            .HasForeignKey(a => a.TabOrderLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.CreatedByStaff)
            .WithMany()
            .HasForeignKey(a => a.CreatedByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        // Recomputing a tab reads every adjustment on it.
        builder.HasIndex(a => a.TabId);
    }
}

internal sealed class ServiceRequestConfiguration : EntityConfiguration<ServiceRequest>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ServiceRequest> builder)
    {
        builder.ToTable("ServiceRequests");

        builder.Property(r => r.Preset).IsRequired();

        builder.Property(r => r.Note).HasMaxLength(FieldLengths.ServiceNote);

        builder.Property(r => r.AcknowledgedAtUtc);

        builder.Ignore(r => r.IsOpen);

        builder.HasOne(r => r.Tab)
            .WithMany()
            .HasForeignKey(r => r.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Branch)
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.DiningTable)
            .WithMany()
            .HasForeignKey(r => r.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.RequestedByParticipant)
            .WithMany()
            .HasForeignKey(r => r.RequestedByParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.AcknowledgedByStaff)
            .WithMany()
            .HasForeignKey(r => r.AcknowledgedByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        // The floor screen's question, several times a minute: what is outstanding in this branch.
        // Filtered on the open ones, because that is the only set anybody ever asks for and the
        // acknowledged rows accumulate for the whole evening.
        builder.HasIndex(r => new { r.BranchId, r.CreatedAtUtc })
            .HasFilter("[AcknowledgedAtUtc] IS NULL")
            .HasDatabaseName("IX_ServiceRequests_Open");

        // The rate limit reads this: how many has this tab raised recently.
        builder.HasIndex(r => new { r.TabId, r.CreatedAtUtc });
    }
}

internal sealed class TabEventConfiguration : EntityConfiguration<TabEvent>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabEvent> builder)
    {
        builder.ToTable("TabEvents");

        // A database IDENTITY, not a value the application picks. Two events on the same tab in
        // the same millisecond are ordinary, and a client asking for "everything after 41" needs a
        // total order that no amount of clock precision can give it.
        builder.Property(e => e.Sequence)
            .ValueGeneratedOnAdd()
            .UseIdentityColumn();

        builder.Property(e => e.Type).IsRequired();

        // Unbounded: a payload is a few hundred bytes and truncating one would corrupt a client's
        // catch-up silently rather than loudly.
        builder.Property(e => e.PayloadJson)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(e => e.ActorType).IsRequired();
        builder.Property(e => e.ActorId);
        builder.Property(e => e.AtUtc).IsRequired();

        builder.HasOne(e => e.Tab)
            .WithMany()
            .HasForeignKey(e => e.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        // The catch-up query, and the only one this table serves.
        builder.HasIndex(e => new { e.TabId, e.Sequence })
            .HasDatabaseName("IX_TabEvents_TabId_Sequence");
    }
}
