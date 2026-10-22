using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;
using Yalla.Domain.Menus;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TakeawayOrderConfiguration : EntityConfiguration<TakeawayOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TakeawayOrder> builder)
    {
        builder.ToTable("TakeawayOrders", t =>
        {
            t.HasCheckConstraint("CK_TakeawayOrders_Status", "[Status] BETWEEN 1 AND 7");
            t.HasCheckConstraint("CK_TakeawayOrders_PaidAmount", "[PaidAmountAmd] IS NULL OR [PaidAmountAmd] >= 0");
        });

        builder.Property(o => o.Code).IsRequired().HasMaxLength(TakeawayRules.CodeLength);
        builder.Property(o => o.GuestName).IsRequired().HasMaxLength(FieldLengths.PersonName);
        builder.Property(o => o.GuestPhone).IsRequired().HasMaxLength(FieldLengths.PhoneE164);
        builder.Property(o => o.Note).HasMaxLength(TakeawayRules.MaxNoteLength);
        builder.Property(o => o.DeclineReason).HasMaxLength(TakeawayRules.MaxDeclineReasonLength);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Ignore(o => o.TotalAmd);
        builder.Ignore(o => o.NoShowFromUtc);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(o => o.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(o => o.DinerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        foreach (var staff in new[]
                 {
                     nameof(TakeawayOrder.AcceptedByStaffId), nameof(TakeawayOrder.MarkedReadyByStaffId),
                     nameof(TakeawayOrder.CollectedByStaffId), nameof(TakeawayOrder.DeclinedByStaffId),
                     nameof(TakeawayOrder.NoShowByStaffId),
                 })
        {
            builder.HasOne<StaffMember>()
                .WithMany()
                .HasForeignKey(staff)
                .OnDelete(DeleteBehavior.Restrict);
        }

        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.TakeawayOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The code the guest says at the counter: one per branch per service day. Taken under the branch's lock; this is
        // the backstop.
        builder.HasIndex(o => new { o.BranchId, o.LocalDate, o.Code })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TakeawayCodePerBranchDay);

        // A retried send answers the order the first one made.
        builder.HasIndex(o => o.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TakeawayClientCommand);

        // The counter's read and the kitchen's: a branch's orders by status.
        builder.HasIndex(o => new { o.BranchId, o.Status })
            .HasDatabaseName("IX_TakeawayOrders_BranchId_Status");

        // The Orders tab: one diner's, newest first.
        builder.HasIndex(o => new { o.DinerUserId, o.CreatedAtUtc })
            .HasDatabaseName("IX_TakeawayOrders_DinerUserId_CreatedAtUtc");

        // The sweep: the orders still waiting for an answer, by deadline. Small - the column is cleared on any answer.
        builder.HasIndex(o => o.AcceptDeadlineUtc)
            .HasFilter("[AcceptDeadlineUtc] IS NOT NULL")
            .HasDatabaseName("IX_TakeawayOrders_AcceptDeadlineUtc");
    }
}

internal sealed class TakeawayOrderLineConfiguration : EntityConfiguration<TakeawayOrderLine>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TakeawayOrderLine> builder)
    {
        builder.ToTable("TakeawayOrderLines", t =>
        {
            t.HasCheckConstraint(
                "CK_TakeawayOrderLines_Quantity",
                $"[Quantity] BETWEEN {TakeawayRules.MinQuantity} AND {TakeawayRules.MaxQuantity}");
            t.HasCheckConstraint("CK_TakeawayOrderLines_Price", "[UnitPriceAmdSnapshot] >= 0");
        });

        builder.Property(l => l.NameSnapshot).IsRequired().HasMaxLength(FieldLengths.Name);
        builder.Ignore(l => l.LineTotalAmd);

        builder.HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(l => l.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.TakeawayOrderId, l.Position }).IsUnique();
    }
}
