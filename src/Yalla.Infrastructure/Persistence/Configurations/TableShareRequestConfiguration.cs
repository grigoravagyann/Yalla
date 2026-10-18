using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TableShareRequestConfiguration : EntityConfiguration<TableShareRequest>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TableShareRequest> builder)
    {
        builder.ToTable("TableShareRequests");

        builder.Property(r => r.Status).IsRequired();
        builder.Property(r => r.AsksEarlierGuest).IsRequired();
        builder.Property(r => r.RequestedStartUtc).IsRequired();
        builder.Property(r => r.FreeByUtc).IsRequired();
        builder.Property(r => r.ExpiresAtUtc).IsRequired();

        builder.Property(r => r.RowVersion).IsRowVersion();

        // Both ends are bookings, and Restrict on both: a request is the record of a question asked
        // about two bookings, and neither is ever deleted - they are cancelled.
        builder.HasOne<Reservation>()
            .WithMany()
            .HasForeignKey(r => r.EarlierReservationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Reservation>()
            .WithMany()
            .HasForeignKey(r => r.LaterReservationId)
            .OnDelete(DeleteBehavior.Restrict);

        // One open question per earlier booking - a guest is not asked by two people at once - and per
        // later booking. Filtered on Pending, so the history of answered ones is unconstrained. Every
        // booker of the table holds its lock when it asks, so the writer's own check settles this
        // first; the index is what holds the rule for any writer that did not.
        builder.HasIndex(r => r.EarlierReservationId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName(DatabaseIndexNames.OpenTableShareRequestPerEarlierBooking);

        builder.HasIndex(r => r.LaterReservationId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName(DatabaseIndexNames.OpenTableShareRequestPerLaterBooking);

        // The history of either booking: "what was asked of this booking", "what did this one ask".
        builder.HasIndex(r => new { r.EarlierReservationId, r.CreatedAtUtc })
            .HasDatabaseName("IX_TableShareRequests_EarlierReservationId_CreatedAtUtc");

        builder.HasIndex(r => new { r.LaterReservationId, r.CreatedAtUtc })
            .HasDatabaseName("IX_TableShareRequests_LaterReservationId_CreatedAtUtc");
    }
}
