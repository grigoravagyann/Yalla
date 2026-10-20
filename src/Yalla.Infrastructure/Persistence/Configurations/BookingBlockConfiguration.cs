using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class BookingBlockConfiguration : EntityConfiguration<BookingBlock>
{
    protected override void ConfigureEntity(EntityTypeBuilder<BookingBlock> builder)
    {
        builder.ToTable("BookingBlocks");

        builder.Property(b => b.PhoneE164).HasMaxLength(FieldLengths.Phone);
        builder.Property(b => b.BlockedAtUtc).IsRequired();
        builder.Property(b => b.BlockedUntilUtc).IsRequired();
        builder.Property(b => b.Reason).IsRequired();
        builder.Property(b => b.LiftReason).HasMaxLength(FieldLengths.Reason);
        builder.Ignore(b => b.EndedAtUtc);

        // The account is a tombstone when deleted, never removed, so the block outlives it for its few days.
        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(b => b.DinerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Is this account, or this number, blocked now?" - asked before every booking.
        builder.HasIndex(b => new { b.DinerUserId, b.BlockedUntilUtc });

        builder.HasIndex(b => new { b.PhoneE164, b.BlockedUntilUtc })
            .HasFilter("[PhoneE164] IS NOT NULL");
    }
}
