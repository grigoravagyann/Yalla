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

        // Only ever set alongside PlacedByStaffId: the waiter said who the spoken order was for.
        builder.HasOne(o => o.OnBehalfOfParticipant)
            .WithMany()
            .HasForeignKey(o => o.OnBehalfOfParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(o => o.EstimatedReadyAtUtc);

        builder.Property(o => o.ClientCommandId).IsRequired();

        // One order per command per tab. The service's replay check reads before it writes, and two
        // requests carrying one command id - a retry sent while the first is still in flight - both
        // pass that read; this is what makes the second one lose instead of sending the kitchen the
        // order twice. Per tab rather than global: the id is the phone's own, and another tab
        // presenting it is a different command, not a replay of this one.
        builder.HasIndex(o => new { o.TabId, o.ClientCommandId })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TabOrderClientCommand);

        builder.Ignore(o => o.OwningParticipantId);

        // The kitchen queue: everything outstanding on this tab, oldest first.
        builder.HasIndex(o => new { o.TabId, o.Status });

        // Reporting. Orders are the bridge between a date range and the lines it contains, so every
        // menu and revenue-by-hour report seeks orders placed inside the range first.
        builder.HasIndex(o => o.PlacedAtUtc)
            .HasDatabaseName("IX_TabOrders_PlacedAtUtc");
    }
}
