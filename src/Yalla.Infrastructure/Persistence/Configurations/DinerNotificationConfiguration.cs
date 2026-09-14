using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Identity;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class DinerNotificationConfiguration : EntityConfiguration<DinerNotification>
{
    protected override void ConfigureEntity(EntityTypeBuilder<DinerNotification> builder)
    {
        // The kind is a slug the app keys its text on, held to the six it knows by the database as well
        // as the domain, so a row written around the domain still means something to the app.
        builder.ToTable("DinerNotifications", table =>
            table.HasCheckConstraint(
                "CK_DinerNotifications_Kind",
                $"[Kind] IN ({string.Join(", ", DinerNotificationKinds.All.Select(k => $"'{k}'"))})"));

        builder.Property(n => n.Kind)
            .HasMaxLength(DinerNotificationKinds.MaxLength)
            .IsRequired();

        builder.Property(n => n.ParamsJson)
            .HasMaxLength(DinerNotification.ParamsMaxLength)
            .IsRequired();

        // The feed's tie-breaker: two entries with the same instant still have an order, so a cursor
        // never repeats or skips one. Assigned by the database, never written by the application.
        builder.Property(n => n.Sequence).UseIdentityColumn();

        builder.Ignore(n => n.IsRead);

        // Cascade from the account (K12): the feed is the person's. Account deletion also removes it
        // explicitly, because the account row is a tombstone rather than gone. No foreign keys on the
        // branch, booking, tab or order: they are pointers the app follows, and the rows they point at
        // are kept or detached on their own terms.
        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(n => n.DinerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The feed, newest first, and the unread count - filtered to the unread, which is what the
        // badge on every app open reads.
        builder.HasIndex(n => new { n.DinerUserId, n.CreatedAtUtc }, "IX_DinerNotifications_DinerUserId_CreatedAtUtc");

        builder.HasIndex(n => new { n.DinerUserId, n.CreatedAtUtc }, "IX_DinerNotifications_DinerUserId_Unread")
            .HasFilter("[ReadAtUtc] IS NULL");

        // The retention sweep deletes by age.
        builder.HasIndex(n => n.CreatedAtUtc);

        // Cancelling a booking deletes its reminder that has not appeared yet.
        builder.HasIndex(n => n.ReservationId)
            .HasFilter("[ReservationId] IS NOT NULL");
    }
}
