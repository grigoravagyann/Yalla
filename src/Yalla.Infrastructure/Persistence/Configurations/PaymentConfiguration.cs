using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : EntityConfiguration<Payment>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        // Whole Armenian dram, so bigint.
        builder.Property(p => p.AmountAmd).IsRequired();

        builder.Property(p => p.Method).IsRequired();
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.CompletedAtUtc);

        builder.Property(p => p.ProviderReference)
            .HasMaxLength(FieldLengths.ProviderReference);

        builder.HasOne(p => p.Tab)
            .WithMany(t => t.Payments)
            .HasForeignKey(p => p.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null for a cash payment the waiter recorded against the table rather than a person.
        builder.HasOne(p => p.TabParticipant)
            .WithMany()
            .HasForeignKey(p => p.TabParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Summing the reserved and succeeded rows for one tab is how RemainingAmd is recomputed.
        builder.HasIndex(p => new { p.TabId, p.Status });

        // Reconciliation against a provider's own report.
        builder.HasIndex(p => p.ProviderReference);
    }
}
