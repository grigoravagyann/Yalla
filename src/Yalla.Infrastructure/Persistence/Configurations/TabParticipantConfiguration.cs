using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabParticipantConfiguration : EntityConfiguration<TabParticipant>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabParticipant> builder)
    {
        builder.ToTable("TabParticipants");

        builder.Property(p => p.DisplayName)
            .HasMaxLength(FieldLengths.DisplayName)
            .IsRequired();

        builder.Property(p => p.DeviceId)
            .HasMaxLength(FieldLengths.DeviceId)
            .IsRequired();

        builder.Property(p => p.Role).IsRequired();
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.JoinedAtUtc).IsRequired();
        builder.Property(p => p.ApprovedAtUtc);
        builder.Property(p => p.RemovedAtUtc);

        // The permission triple. CanPay implies CanSeeTableTotal, enforced in the entity: it is a
        // domain invariant, not a UI rule, because three different clients consume this API.
        builder.Property(p => p.CanOrder)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(p => p.CanSeeTableTotal)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(p => p.CanPay)
            .IsRequired()
            .HasDefaultValue(false);

        // Restrict: an order line's shares point at participants, and who owed what on a settled
        // bill has to stay answerable.
        builder.HasOne(p => p.Tab)
            .WithMany(t => t.Participants)
            .HasForeignKey(p => p.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.TabId, p.Status });

        // Recognises a returning anonymous guest on the same phone.
        builder.HasIndex(p => new { p.TabId, p.DeviceId });
    }
}
