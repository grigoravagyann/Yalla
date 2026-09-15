using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class BranchReviewReportConfiguration : EntityConfiguration<BranchReviewReport>
{
    protected override void ConfigureEntity(EntityTypeBuilder<BranchReviewReport> builder)
    {
        // The reason is a slug the app sends, held to the five it knows by the database as well as
        // the domain, so a row written around the service still means something.
        builder.ToTable("BranchReviewReports", table =>
            table.HasCheckConstraint(
                "CK_BranchReviewReports_Reason",
                $"[Reason] IN ({string.Join(", ", ReviewReportReasons.All.Select(r => $"'{r}'"))})"));

        builder.Property(r => r.Reason)
            .HasMaxLength(ReviewReportReasons.MaxLength)
            .IsRequired();

        builder.Property(r => r.Note).HasMaxLength(FieldLengths.Reason);

        // Cascade from the review: a report is about that review and means nothing without it, and
        // deleting an account deletes its reviews in one statement. The only cascade into this table,
        // so SQL Server has one path and no cycle.
        builder.HasOne(r => r.Review)
            .WithMany()
            .HasForeignKey(r => r.ReviewId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict from the reporter: the account row is a tombstone rather than gone, and account
        // deletion removes the reports it filed explicitly.
        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(r => r.DinerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One report per diner per review, and the review's report count reads this index.
        builder.HasIndex(r => new { r.ReviewId, r.DinerUserId })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.BranchReviewReportPerDiner);

        // Account deletion finds the reports a diner filed.
        builder.HasIndex(r => r.DinerUserId);
    }
}
