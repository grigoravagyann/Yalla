using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Media;
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

        // Assigned by TabLedger, not by the database. An IDENTITY column numbers rows in the order
        // EF happens to insert them, which is not guaranteed within one SaveChanges - and a payment
        // that settles a bill writes two events in one. See TabEvent's remarks.
        builder.Property(e => e.Sequence)
            .ValueGeneratedNever();

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

        // The catch-up query, and the guarantee. Unique, so two writers racing on one tab cannot
        // both claim position 42 - the loser is renumbered rather than silently reordering the
        // stream a client is replaying.
        builder.HasIndex(e => new { e.TabId, e.Sequence })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TabEventSequence);
    }
}

internal sealed class PhotoConfiguration : EntityConfiguration<Photo>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Photo> builder)
    {
        builder.ToTable("Photos");

        builder.Property(p => p.ContentHash)
            .HasMaxLength(FieldLengths.ContentHash)
            .IsRequired();

        builder.Property(p => p.ThumbnailPath).HasMaxLength(FieldLengths.Url).IsRequired();
        builder.Property(p => p.CardPath).HasMaxLength(FieldLengths.Url).IsRequired();
        builder.Property(p => p.FullPath).HasMaxLength(FieldLengths.Url).IsRequired();

        builder.Property(p => p.Width);
        builder.Property(p => p.Height);
        builder.Property(p => p.BytesStored).IsRequired();
        builder.Property(p => p.IsExternallyHosted).IsRequired();
        builder.Property(p => p.UploadedAtUtc).IsRequired();

        builder.HasOne(p => p.Branch)
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // Uploading the same bytes to the same branch twice reuses the row rather than writing a
        // second one pointing at identical files.
        builder.HasIndex(p => new { p.BranchId, p.ContentHash })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.PhotoPerBranchContent);

        // The orphan sweep's question: what was uploaded before this instant.
        builder.HasIndex(p => p.UploadedAtUtc);
    }
}
