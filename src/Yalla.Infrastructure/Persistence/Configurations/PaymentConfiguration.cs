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

        // Outside the balance on purpose - see Payment.TipAmd. Stored here because the drawer has
        // to reconcile against it, not because the bill knows about it.
        builder.Property(p => p.TipAmd).IsRequired();

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

        // What makes a double-tap on "take cash" safe. The index is the guarantee, not a check in
        // the service: two taps can race, and a check-then-insert would let both through.
        builder.Property(p => p.ClientCommandId).IsRequired();

        builder.HasIndex(p => p.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.PaymentClientCommand);
    }
}
