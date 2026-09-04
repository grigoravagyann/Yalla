using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabOrderLineConfiguration : EntityConfiguration<TabOrderLine>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabOrderLine> builder)
    {
        builder.ToTable("TabOrderLines");

        // Snapshotted from the menu item at the moment of ordering, so a later menu edit cannot
        // change what a closed bill says.
        builder.Property(l => l.NameSnapshot)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        builder.Property(l => l.UnitPriceAmdSnapshot)
            .IsRequired();

        builder.Property(l => l.Quantity).IsRequired();
        builder.Property(l => l.IsShared).IsRequired();
        builder.Property(l => l.VoidedAtUtc);

        builder.Property(l => l.VoidReason)
            .HasMaxLength(FieldLengths.Reason);

        builder.Ignore(l => l.IsVoided);
        builder.Ignore(l => l.LineTotalAmd);

        // Cascade: a line has no meaning apart from its order.
        builder.HasOne(l => l.TabOrder)
            .WithMany(o => o.Lines)
            .HasForeignKey(l => l.TabOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a menu item that appears on any bill cannot be deleted out from under it.
        builder.HasOne(l => l.MenuItem)
            .WithMany()
            .HasForeignKey(l => l.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.VoidedByStaff)
            .WithMany()
            .HasForeignKey(l => l.VoidedByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.TabOrderId);
    }
}
