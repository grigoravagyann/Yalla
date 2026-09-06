using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class ReservationConfiguration : EntityConfiguration<Reservation>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations");

        builder.Property(r => r.GuestName)
            .HasMaxLength(FieldLengths.PersonName)
            .IsRequired();

        builder.Property(r => r.GuestPhone)
            .HasMaxLength(FieldLengths.Phone)
            .IsRequired();

        builder.Property(r => r.Code)
            .HasMaxLength(FieldLengths.ReservationCode)
            .IsRequired();

        builder.Property(r => r.CancellationReason)
            .HasMaxLength(FieldLengths.Reason);

        builder.Property(r => r.PartySize).IsRequired();
        builder.Property(r => r.Status).IsRequired();
        builder.Property(r => r.GraceExtensionsUsed).IsRequired();
        builder.Property(r => r.CancelledAfterDeadline).IsRequired();

        builder.Property(r => r.ClientCommandId).IsRequired();

        // The booked interval. Both ends are UTC instants (datetime2 by convention).
        builder.Property(r => r.StartUtc).IsRequired();
        builder.Property(r => r.EndUtc).IsRequired();

        // Wall-clock copies of what the diner sees on their confirmation, stored alongside the
        // instants rather than derived from them.
        builder.Property(r => r.LocalDate)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(r => r.LocalStartTime)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(r => r.RowVersion)
            .IsRowVersion();

        // No inverse collection on Branch or DiningTable, and Restrict on both: deactivating a
        // branch or retiring a table must never delete the bookings that happened on it.
        builder.HasOne(r => r.Branch)
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.DiningTable)
            .WithMany()
            .HasForeignKey(r => r.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        // The overlap index. Every "is this table free between X and Y?" check - which runs on
        // every booking attempt and every availability screen - reads exactly this.
        builder.HasIndex(r => new { r.DiningTableId, r.StartUtc, r.EndUtc });

        // The branch day view: the staff app's booking list for a service.
        builder.HasIndex(r => new { r.BranchId, r.StartUtc });

        // The code the diner quotes at the door.
        builder.HasIndex(r => r.Code)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.ReservationCode);

        // The idempotency guarantee for booking creation, and the same argument as the one on
        // TableStateChanges: a diner on a patchy connection taps Book twice, and only the database
        // can settle a race between two retries. A check-then-insert in the service lets both
        // through and the party ends up holding two tables.
        builder.HasIndex(r => r.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.ReservationClientCommand);

        // "What has this diner got booked?" - the /mine screen, and the rolling no-show count.
        builder.HasIndex(r => new { r.DinerUserId, r.StartUtc });

        // Reporting. Every reservation report is "this branch, this local date range, grouped by
        // outcome", and the existing (BranchId, StartUtc) index seeks the range but then looks up
        // the status for every row it finds. Including it makes the whole group-by a covering scan.
        builder.HasIndex(r => new { r.BranchId, r.StartUtc, r.Status })
            .HasDatabaseName("IX_Reservations_BranchId_StartUtc_Status");

        // Self-reported, defaulted for every row that predates the column - which is what Unknown
        // means and why it is the zero value.
        builder.Property(r => r.Channel)
            .IsRequired()
            .HasDefaultValue(Yalla.Domain.Enums.ReservationChannel.Unknown);
    }
}
