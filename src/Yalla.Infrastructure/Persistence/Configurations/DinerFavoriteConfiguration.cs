using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Identity;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class DinerFavoriteConfiguration : EntityConfiguration<DinerFavorite>
{
    protected override void ConfigureEntity(EntityTypeBuilder<DinerFavorite> builder)
    {
        builder.ToTable("DinerFavorites");

        // Cascade from the account (K11): a favourite is the person's and means nothing without them.
        // Account deletion tombstones the row rather than removing it, so it also deletes these
        // explicitly - this is the rule for any path that ever removes the row itself.
        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(f => f.DinerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // No foreign key to the branch. A heart is checked against a published branch when it is added,
        // and the list reads it back through the published-branch rule, so a place that closes or is
        // never found again simply drops out; one that reopens comes back with its hearts.

        // One heart per diner per place. It leads with DinerUserId, so it is also the index the list,
        // the count against the limit and account deletion read.
        builder.HasIndex(f => new { f.DinerUserId, f.BranchId })
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.DinerFavoritePerDiner);
    }
}
