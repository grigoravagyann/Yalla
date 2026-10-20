using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManageLinkNoticeKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The older constraint does not know these kinds, so the entries go first or it cannot be added.
            // They are notices about done things; the bookings themselves are untouched.
            migrationBuilder.Sql(
                "DELETE FROM [DinerNotifications] WHERE [Kind] IN ('booking-changed-by-link', 'booking-cancelled-by-link');");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied')");
        }
    }
}
