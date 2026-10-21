using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Menus;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class PreOrderConfiguration : EntityConfiguration<PreOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PreOrder> builder)
    {
        builder.ToTable("PreOrders");

        builder.Property(p => p.Note).HasMaxLength(PreOrderRules.MaxNoteLength);
        builder.Property(p => p.UpdatedAtUtc).IsRequired();
        builder.Property(p => p.RowVersion).IsRowVersion();

        // Plain column: the order it became has the pre-order's own id, and a key the other way would be circular.
        builder.Property(p => p.TabOrderId);

        builder.Ignore(p => p.IsSent);
        builder.Ignore(p => p.IsHeld);
        builder.Ignore(p => p.TotalAmd);

        // One per booking, enforced here: two first saves racing each other cannot both insert.
        builder.HasOne(p => p.Reservation)
            .WithMany()
            .HasForeignKey(p => p.ReservationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.ReservationId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.PreOrderPerReservation);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(p => p.SentByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(p => p.NotComingByStaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Lines)
            .WithOne()
            .HasForeignKey(l => l.PreOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The kitchen's held tickets: sent, not yet on a tab. Small - the ones waiting for a party to sit.
        builder.HasIndex(p => p.BranchId)
            .HasFilter("[SentAtUtc] IS NOT NULL AND [TabOrderId] IS NULL")
            .HasDatabaseName("IX_PreOrders_BranchId_Held");

        // The rail's reads - the due list and the next card's time - and a settings save's: a branch's pre-orders, each
        // joined to its booking, without driving from the bookings.
        builder.HasIndex(p => new { p.BranchId, p.ReservationId })
            .HasDatabaseName(DatabaseIndexNames.PreOrdersByBranch);
    }
}

internal sealed class PreOrderLineConfiguration : EntityConfiguration<PreOrderLine>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PreOrderLine> builder)
    {
        builder.ToTable("PreOrderLines", t =>
        {
            t.HasCheckConstraint(
                "CK_PreOrderLines_Quantity",
                $"[Quantity] BETWEEN {PreOrderRules.MinQuantity} AND {PreOrderRules.MaxQuantity}");
            t.HasCheckConstraint("CK_PreOrderLines_Price", "[UnitPriceAmdSnapshot] >= 0");
        });

        builder.Property(l => l.NameSnapshot).IsRequired().HasMaxLength(Domain.Common.FieldLengths.Name);
        builder.Property(l => l.Note).HasMaxLength(PreOrderRules.MaxLineNoteLength);
        builder.Ignore(l => l.LineTotalAmd);

        builder.HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(l => l.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.PreOrderId, l.Position }).IsUnique();
    }
}
