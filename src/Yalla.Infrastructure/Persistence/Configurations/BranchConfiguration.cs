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

        // Nullable: a branch is usable before anybody types its number in, and one created through
        // the platform API has no way to supply one.
        builder.Property(b => b.PhoneE164)
            .HasMaxLength(FieldLengths.PhoneE164);

        // False for every existing row, which is the whole point of the flag - see Branch. The
        // database default covers the rows that predate the column; bool has no spare sentinel, so
        // unlike SubscriptionTier this cannot distinguish "not set" from "set to false", and does
        // not need to: false is the answer in both cases.
        builder.Property(b => b.AcceptsWebBookings)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Ignore(b => b.IsPaid);

        // Null until somebody saves the policy form. Not a flag: the timestamp answers "when did
        // anyone last look at this?", which is the question a venue that was onboarded eight months
        // ago actually raises.
        builder.Property(b => b.ReservationPolicyReviewedAtUtc);

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
