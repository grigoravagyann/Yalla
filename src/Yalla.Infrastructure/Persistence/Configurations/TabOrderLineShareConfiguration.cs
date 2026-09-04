using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabOrderLineShareConfiguration : EntityConfiguration<TabOrderLineShare>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabOrderLineShare> builder)
    {
        builder.ToTable("TabOrderLineShares");

        // Cascade, so that deleting an order can actually delete its lines: the line-to-order
        // cascade required by the spec cannot complete while these rows restrict it.
        builder.HasOne(s => s.TabOrderLine)
            .WithMany(l => l.Shares)
            .HasForeignKey(s => s.TabOrderLineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: who owed what has to stay answerable.
        builder.HasOne(s => s.TabParticipant)
            .WithMany()
            .HasForeignKey(s => s.TabParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        // One person shares a given line at most once.
        builder.HasIndex(s => new { s.TabOrderLineId, s.TabParticipantId })
            .IsUnique();

        builder.HasIndex(s => s.TabParticipantId);
    }
}
