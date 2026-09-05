using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class BranchConfiguration : EntityConfiguration<Branch>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");

        builder.Property(b => b.Name)
            .HasMaxLength(FieldLengths.Name)
            .IsRequired();

        builder.Property(b => b.Slug)
            .HasMaxLength(FieldLengths.Slug)
            .IsRequired();

        builder.Property(b => b.Address)
            .HasMaxLength(FieldLengths.Address)
            .IsRequired();

        builder.Property(b => b.TimeZoneId)
            .HasMaxLength(FieldLengths.TimeZoneId)
            .IsRequired();

        builder.Property(b => b.Latitude)
            .IsRequired();

        builder.Property(b => b.Longitude)
            .IsRequired();

        builder.Property(b => b.FloorWidth)
            .IsRequired();

        builder.Property(b => b.FloorHeight)
            .IsRequired();

        builder.Property(b => b.IsActive)
            .IsRequired();

        // Per branch, never per venue: a chain with four locations is four paying customers.
        //
        // The database default exists for the rows that predate the column. The sentinel is 0,
        // which is not a defined tier and which the entity never holds, so every insert writes the
        // tier explicitly and the default is used when, and only when, nothing was set.
        builder.Property(b => b.SubscriptionTier)
            .IsRequired()
            .HasDefaultValue(Yalla.Domain.Enums.SubscriptionTier.Free)
            .HasSentinel((Yalla.Domain.Enums.SubscriptionTier)0);

        // Configured explicitly so the navigation uses CoverPhotoId rather than EF inventing a
        // shadow foreign key beside it. Restrict, like every other photo reference: a picture a
        // venue card is using must not be deletable out from under it.
        builder.HasOne(b => b.CoverPhoto)
            .WithMany()
            .HasForeignKey(b => b.CoverPhotoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(b => b.IsPaid);

        // The reservation policy is owned: it lives in extra columns on this table rather than in
        // a table of its own, because a branch always has exactly one and it is never queried
        // apart from its branch.
        builder.OwnsOne(b => b.ReservationPolicy, policy =>
        {
            policy.Property(p => p.TurnTimeMinutes).IsRequired();
            policy.Property(p => p.BufferMinutes).IsRequired();
            policy.Property(p => p.GraceMinutes).IsRequired();
            policy.Property(p => p.LateNudgeAfterMinutes).IsRequired();
            policy.Property(p => p.GraceExtensionMinutes).IsRequired();
            policy.Property(p => p.MinLeadMinutes).IsRequired();
            policy.Property(p => p.BookingWindowDays).IsRequired();
            policy.Property(p => p.CancellationDeadlineMinutes).IsRequired();
            policy.Property(p => p.AutoConfirm).IsRequired();

            // Added in Prompt 7. Existing rows take the shipped default rather than 0, which would
            // mean "never warn" - the opposite of what the setting is for.
            policy.Property(p => p.WalkInHoldbackMinutes)
                .IsRequired()
                .HasDefaultValue(30);
            policy.Property(p => p.PricesIncludeVat).IsRequired();

            policy.Property(p => p.ServiceChargePercent)
                .HasPrecision(5, 2)
                .IsRequired();

            // Null means "no limit" / "never", so these two stay nullable.
            policy.Property(p => p.MaxSeatOverhang);
            policy.Property(p => p.ApprovalRequiredAbovePartySize);
        });

        builder.Navigation(b => b.ReservationPolicy)
            .IsRequired();

        builder.HasOne(b => b.Venue)
            .WithMany(v => v.Branches)
            .HasForeignKey(b => b.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.VenueId, b.Slug })
            .IsUnique();
    }
}
