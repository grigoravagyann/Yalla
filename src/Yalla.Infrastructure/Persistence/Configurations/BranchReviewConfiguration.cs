using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class BranchReviewConfiguration : EntityConfiguration<BranchReview>
{
    protected override void ConfigureEntity(EntityTypeBuilder<BranchReview> builder)
    {
        builder.ToTable("BranchReviews", table =>
            table.HasCheckConstraint(
                "CK_BranchReviews_Rating",
                $"[Rating] BETWEEN {BranchReview.MinRating} AND {BranchReview.MaxRating}"));

        builder.Property(r => r.Rating).IsRequired();

        builder.Property(r => r.Text).HasMaxLength(FieldLengths.ReviewText);

        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        // Restrict both ways: a review is a public statement somebody made, and neither the branch
        // nor the account row is ever hard-deleted from under it.
        builder.HasOne(r => r.Branch)
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.DinerUser)
            .WithMany()
            .HasForeignKey(r => r.DinerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The rule: one review per diner per branch. The service's check is the readable answer;
        // this is what holds under two simultaneous taps.
        builder.HasIndex(r => new { r.BranchId, r.DinerUserId })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.BranchReviewPerDiner);

        // The reviews page, newest revision first.
        builder.HasIndex(r => new { r.BranchId, r.UpdatedAtUtc });
    }
}
