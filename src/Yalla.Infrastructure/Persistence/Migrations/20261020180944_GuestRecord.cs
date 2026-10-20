using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_BookerDeviceHash_Active",
                table: "Reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.DropColumn(
                name: "BookerDeviceHash",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_UnverifiedMaxPartySize",
                table: "Branches");

            // Not a rename (EF's guess): the old flag defaulted to ON, and carrying it over would switch
            // "auto-confirm guests with a good record" on for every branch. Dropped, and the new setting starts
            // OFF everywhere. The rows booked without proof while Yalla#54 was live are left exactly as they are -
            // see docs/reservations.md, "Booking needs a proved number".
            migrationBuilder.DropColumn(
                name: "ReservationPolicy_UnverifiedBookingsNeedApproval",
                table: "Branches");

            migrationBuilder.AddColumn<bool>(
                name: "ReservationPolicy_AutoConfirmGuestsWithGoodRecord",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CancelledBecause",
                table: "Reservations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneE164 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BlockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BlockedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    LiftedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingBlocks_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows')");

            migrationBuilder.CreateIndex(
                name: "IX_BookingBlocks_DinerUserId_BlockedUntilUtc",
                table: "BookingBlocks",
                columns: new[] { "DinerUserId", "BlockedUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingBlocks_PhoneE164_BlockedUntilUtc",
                table: "BookingBlocks",
                columns: new[] { "PhoneE164", "BlockedUntilUtc" },
                filter: "[PhoneE164] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingBlocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The older check knows no block notices; they go before it comes back.
            migrationBuilder.Sql("DELETE FROM dbo.DinerNotifications WHERE [Kind] = 'bookings-blocked-no-shows';");

            // Nor does it have a handler for the block's push (review N5).
            migrationBuilder.Sql("DELETE FROM dbo.OutboxMessages WHERE [Type] = 'diner.bookings-blocked';");

            migrationBuilder.DropColumn(
                name: "CancelledBecause",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_AutoConfirmGuestsWithGoodRecord",
                table: "Branches");

            // Yalla#54's defaults: unverified bookings wait for the venue, a party of six at most.
            migrationBuilder.AddColumn<bool>(
                name: "ReservationPolicy_UnverifiedBookingsNeedApproval",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "BookerDeviceHash",
                table: "Reservations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationPolicy_UnverifiedMaxPartySize",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BookerDeviceHash_Active",
                table: "Reservations",
                columns: new[] { "BookerDeviceHash", "EndUtc" },
                filter: "[BookerDeviceHash] IS NOT NULL AND [Status] IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link')");
        }
    }
}
