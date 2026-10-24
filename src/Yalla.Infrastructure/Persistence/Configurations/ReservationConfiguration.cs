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

        // The diner's note to the venue (K9). Nullable: most bookings carry none.
        builder.Property(r => r.Note)
            .HasMaxLength(FieldLengths.Reason);

        // How the booking was made: without a proved number (only while Yalla#54 allowed it, 2026-10-20).
        // Existing rows were all made by an account whose number was proved, or by nobody at all, so false.
        builder.Property(r => r.PhoneUnverified)
            .IsRequired()
            .HasDefaultValue(false);

        // The per-number rules' key. See the entity.
        builder.Property(r => r.BookerPhoneE164)
            .HasMaxLength(FieldLengths.Phone);

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

        // The branch's bookings still live - pending, confirmed or seated - by when they end: the guest-message threads
        // the tablet keeps in view (GuestThreads.RelevantAt, on every floor poll) seek it from two hours ago, so the read
        // costs what is live now rather than everything the branch was ever booked for.
        builder.HasIndex(r => new { r.BranchId, r.EndUtc })
            .HasFilter("[Status] IN (1, 2, 4)")
            .HasDatabaseName("IX_Reservations_BranchId_EndUtc_Live");

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

        // Only the hash is stored - see Reservation.ManageTokenHash. Nullable for every booking
        // made before the column existed.
        builder.Property(r => r.ManageTokenHash)
            .HasMaxLength(FieldLengths.TokenHash);

        // The manage link's only lookup: one row, by hash. Unique so two bookings cannot share a
        // token, and filtered because most rows have none and a null is not a collision.
        builder.HasIndex(r => r.ManageTokenHash)
            .IsUnique()
            .HasFilter("[ManageTokenHash] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.ReservationManageToken);

        // Derived from EndUtc and a constant; nothing to store and nothing to keep in sync.
        builder.Ignore(r => r.ManageTokenExpiresAtUtc);

        // Why the booking waited, and how a decline came about - both stored as their numbers, both
        // nullable: most bookings never wait, and rows from before the columns have neither.
        builder.Property(r => r.ApprovalTrigger);
        builder.Property(r => r.DeclineReason);

        // Set only when the platform cancelled the booking (a guest's no-show block); null otherwise.
        builder.Property(r => r.CancelledBecause);

        // The review round (2026-10-20): whether a late cancel counts against the guest, and who marked - or
        // withdrew - a no-show, and whether it counts toward a block.
        builder.Property(r => r.LateCancellationCounts).IsRequired();
        builder.Property(r => r.NoShowCountsTowardBlock).IsRequired();
        builder.Property(r => r.NoShowUndoReason).HasMaxLength(FieldLengths.Reason);

        // The confirmed slot a waiting change can fall back to, all null when none is waiting. Wall
        // clock beside the instants, as for the booking's own slot. No foreign key on the table: it is
        // the table the booking was on, kept to go back to, and the booking's own key covers the one it
        // is on now.
        builder.Ignore(r => r.HasConfirmedSlotToReturnTo);
        builder.Property(r => r.PreviousLocalDate).HasColumnType("date");
        builder.Property(r => r.PreviousLocalStartTime).HasColumnType("time");

        // The approval sweep's only read: "which requests have run out of time?". Filtered, because
        // the column is cleared the moment a booking stops waiting, so the index holds the handful
        // of short-notice requests open right now and nothing else.
        builder.HasIndex(r => r.ApprovalExpiresAtUtc)
            .HasFilter("[ApprovalExpiresAtUtc] IS NOT NULL")
            .HasDatabaseName("IX_Reservations_ApprovalExpiresAtUtc");

        // "How many active bookings does this number hold?" - asked inside every new booking's locks.
        // Filtered to the two statuses that can be active, so it holds the city's live bookings and
        // nothing of their history. Not unique: "active" also means "not over yet", which an index
        // cannot express (a confirmed booking nobody seated would hold the number for ever) - the
        // application lock in ReservationWriter is what makes the check safe.
        builder.HasIndex(r => new { r.BookerPhoneE164, r.EndUtc })
            .HasFilter("[BookerPhoneE164] IS NOT NULL AND [Status] IN (1, 2)")
            .HasDatabaseName("IX_Reservations_BookerPhoneE164_Active");
    }
}
