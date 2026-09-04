using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TableStateChangeConfiguration : EntityConfiguration<TableStateChange>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TableStateChange> builder)
    {
        builder.ToTable("TableStateChanges");

        builder.Property(c => c.FromStatus).IsRequired();
        builder.Property(c => c.ToStatus).IsRequired();

        builder.Property(c => c.Reason)
            .HasMaxLength(FieldLengths.Reason)
            .IsRequired();

        builder.Property(c => c.ActorType).IsRequired();
        builder.Property(c => c.AtUtc).IsRequired();

        // Opaque references, not foreign keys: the actor may be a diner (identity is a later
        // module) or the system itself, and the reservation or tab is recorded for context.
        builder.Property(c => c.ActorId);
        builder.Property(c => c.ReservationId);
        builder.Property(c => c.TabId);

        builder.HasOne(c => c.Branch)
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.DiningTable)
            .WithMany()
            .HasForeignKey(c => c.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        // "What happened to table 7 last night", and the raw feed for turnover reporting.
        builder.HasIndex(c => new { c.DiningTableId, c.AtUtc });

        builder.HasIndex(c => new { c.BranchId, c.AtUtc });
    }
}
