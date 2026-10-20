using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestRecordReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<bool>(
                name: "LateCancellationCounts",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NoShowCountsTowardBlock",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "NoShowMarkedByStaffId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoShowUndoReason",
                table: "Reservations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoShowUndoneAtUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NoShowUndoneByStaffId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiftReason",
                table: "BookingBlocks",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LiftedBecause",
                table: "BookingBlocks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LiftedByStaffId",
                table: "BookingBlocks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked')");
            // History, judged by today's rules as far as the rows allow (review S2, B1a). A late cancellation counts
            // against the guest only for a booking confirmed (ConfirmedAtUtc kept through the cancel) and made before
            // its deadline; a no-show counts toward a block only when it was marked after the branch's grace.
            migrationBuilder.Sql(
                """
                UPDATE r SET LateCancellationCounts = 1
                FROM dbo.Reservations r JOIN dbo.Branches b ON b.Id = r.BranchId
                WHERE r.Status = 6 AND r.CancelledAfterDeadline = 1 AND r.ConfirmedAtUtc IS NOT NULL
                  AND r.CreatedAtUtc <= DATEADD(minute, -b.ReservationPolicy_CancellationDeadlineMinutes, r.StartUtc);
                """);

            migrationBuilder.Sql(
                """
                UPDATE r SET NoShowCountsTowardBlock = 1
                FROM dbo.Reservations r JOIN dbo.Branches b ON b.Id = r.BranchId
                WHERE r.Status = 8 AND r.CancelledAtUtc >= DATEADD(minute, b.ReservationPolicy_GraceMinutes, r.StartUtc);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The older code knows neither the two new feed kinds nor the two new pushes (review N5).
            migrationBuilder.Sql(
                "DELETE FROM dbo.DinerNotifications WHERE [Kind] IN ('booking-no-show', 'bookings-unblocked');");
            migrationBuilder.Sql(
                "DELETE FROM dbo.OutboxMessages WHERE [Type] IN ('reservation.no-show', 'diner.bookings-unblocked');");

            migrationBuilder.DropColumn(
                name: "LateCancellationCounts",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NoShowCountsTowardBlock",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NoShowMarkedByStaffId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NoShowUndoReason",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NoShowUndoneAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NoShowUndoneByStaffId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "LiftReason",
                table: "BookingBlocks");

            migrationBuilder.DropColumn(
                name: "LiftedBecause",
                table: "BookingBlocks");

            migrationBuilder.DropColumn(
                name: "LiftedByStaffId",
                table: "BookingBlocks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows')");
        }
    }
}
