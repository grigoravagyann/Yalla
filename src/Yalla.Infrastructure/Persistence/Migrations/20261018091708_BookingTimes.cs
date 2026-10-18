using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Booking times and table sequencing: bookable from ten minutes ahead with five minutes of clearing
    /// time, a promise to free the table on the booking, and the requests one booking makes of the one
    /// before it on the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two policy defaults move, only where nobody chose them.</b> A branch whose
    /// <c>ReservationPolicy_MinLeadMinutes</c> is still 30 - the old default - takes 10; one whose
    /// <c>ReservationPolicy_BufferMinutes</c> is still 15 takes 5. Each column on its own, so a branch that
    /// changed one keeps that one and has the other moved; every other value an owner set stays. Down moves
    /// the new defaults back the same way, which also catches a branch that chose 10 or 5 in between - a
    /// rollback, not an edit.
    /// </para>
    /// <para>
    /// <c>Reservations</c> gains <c>PromisedFreeByUtc</c> - the time its guest promised to have left the
    /// table by, accepting a "must free by" when booking or saying yes to a later guest - and
    /// <c>PreviousPromisedFreeByUtc</c> beside the other <c>Previous*</c> columns of a waiting change. Both
    /// nullable; no booking made before this has either.
    /// </para>
    /// <para>
    /// <c>TableShareRequests</c> is new: one row per question a later booking asks the earlier one -
    /// "will you have left by 19:40?" - with a filtered unique index allowing one open question per
    /// earlier booking and one per later booking. The feed's kind check takes the two new kinds,
    /// <c>table-request</c> and <c>table-request-decided</c>; Down deletes those entries first, so the old
    /// check can go back on.
    /// </para>
    /// <para>
    /// The new enum members - <c>ApprovalTrigger.EarlierGuestConsent</c> (5),
    /// <c>DeclineReason.EarlierGuestDeclined</c> (3) and <c>EarlierGuestNoAnswer</c> (4) - are numbers in
    /// columns that already exist and need nothing here.
    /// </para>
    /// </remarks>
    public partial class BookingTimes : Migration
    {
        /// <summary>The minimum lead time every branch shipped with before this migration.</summary>
        internal const int OldDefaultMinLeadMinutes = 30;

        /// <summary>The clearing time every branch shipped with before this migration.</summary>
        internal const int OldDefaultBufferMinutes = 15;

        /// <summary>The new minimum lead time - <c>ReservationPolicy.DefaultMinLeadMinutes</c>, fixed here as it was on the day.</summary>
        internal const int NewDefaultMinLeadMinutes = 10;

        /// <summary>The new clearing time - <c>ReservationPolicy.DefaultBufferMinutes</c>, fixed here as it was on the day.</summary>
        internal const int NewDefaultBufferMinutes = 5;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE [Branches] SET [ReservationPolicy_MinLeadMinutes] = {NewDefaultMinLeadMinutes} "
                + $"WHERE [ReservationPolicy_MinLeadMinutes] = {OldDefaultMinLeadMinutes};");

            migrationBuilder.Sql(
                $"UPDATE [Branches] SET [ReservationPolicy_BufferMinutes] = {NewDefaultBufferMinutes} "
                + $"WHERE [ReservationPolicy_BufferMinutes] = {OldDefaultBufferMinutes};");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousPromisedFreeByUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromisedFreeByUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TableShareRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EarlierReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaterReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FreeByUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AsksEarlierGuest = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableShareRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TableShareRequests_Reservations_EarlierReservationId",
                        column: x => x.EarlierReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableShareRequests_Reservations_LaterReservationId",
                        column: x => x.LaterReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided')");

            migrationBuilder.CreateIndex(
                name: "IX_TableShareRequests_EarlierReservationId_CreatedAtUtc",
                table: "TableShareRequests",
                columns: new[] { "EarlierReservationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TableShareRequests_LaterReservationId_CreatedAtUtc",
                table: "TableShareRequests",
                columns: new[] { "LaterReservationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TableShareRequests_EarlierReservationId_Pending",
                table: "TableShareRequests",
                column: "EarlierReservationId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_TableShareRequests_LaterReservationId_Pending",
                table: "TableShareRequests",
                column: "LaterReservationId",
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TableShareRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The old check knows nothing of these, so they go before it comes back.
            migrationBuilder.Sql(
                "DELETE FROM [DinerNotifications] WHERE [Kind] IN ('table-request', 'table-request-decided');");

            migrationBuilder.Sql(
                $"UPDATE [Branches] SET [ReservationPolicy_MinLeadMinutes] = {OldDefaultMinLeadMinutes} "
                + $"WHERE [ReservationPolicy_MinLeadMinutes] = {NewDefaultMinLeadMinutes};");

            migrationBuilder.Sql(
                $"UPDATE [Branches] SET [ReservationPolicy_BufferMinutes] = {OldDefaultBufferMinutes} "
                + $"WHERE [ReservationPolicy_BufferMinutes] = {NewDefaultBufferMinutes};");

            migrationBuilder.DropColumn(
                name: "PreviousPromisedFreeByUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "PromisedFreeByUtc",
                table: "Reservations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden')");
        }
    }
}
