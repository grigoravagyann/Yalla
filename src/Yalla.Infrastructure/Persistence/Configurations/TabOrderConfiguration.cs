using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabOrderConfiguration : EntityConfiguration<TabOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabOrder> builder)
    {
        builder.ToTable("TabOrders");

        builder.Property(o => o.PlacedAtUtc).IsRequired();
        builder.Property(o => o.Status).IsRequired();

        builder.HasOne(o => o.Tab)
            .WithMany(t => t.Orders)
            .HasForeignKey(o => o.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exactly one of these two is set - the entity enforces it. A diner ordered from their
        // phone, or a waiter keyed in what was spoken at the table.
        builder.HasOne(o => o.PlacedByParticipant)
            .WithMany()
            .HasForeignKey(o => o.PlacedByParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.PlacedByStaff)
            .WithMany()
            .HasForeignKey(o => o.PlacedByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        // The kitchen queue: everything outstanding on this tab, oldest first.
        builder.HasIndex(o => new { o.TabId, o.Status });
    }
}
