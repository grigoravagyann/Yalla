using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What booking needs on top of the reservation schema: an idempotency key, a record of late
    /// cancellations, and the index the diner's own bookings are read by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ClientCommandId</c> and its unique index are the same mechanism, and the same argument,
    /// as the one already on <c>TableStateChanges</c>: a diner on a patchy mobile connection taps
    /// Book, sees nothing, and taps again. Only the database can settle a race between two
    /// retries - a check-then-insert in the service lets both through, and the party ends up
    /// holding two tables while the venue loses a cover it could have sold.
    /// </para>
    /// <para>
    /// <c>CancelledAfterDeadline</c> is stored rather than derived because the deadline is a
    /// per-branch setting. Deriving it would mean an owner who shortens the deadline next month
    /// retroactively reclassifies last month's cancellations as late.
    /// </para>
    /// </remarks>
    public partial class ReservationsAndAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelledAfterDeadline",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientCommandId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Existing bookings all took the empty-Guid default, which the unique index below
            // would reject the moment there is more than one of them. Backfill distinct values
            // first so this migration is safe on a database that already has bookings in it, not
            // only on an empty one.
            migrationBuilder.Sql(
                """
                UPDATE [Reservations]
                SET [ClientCommandId] = NEWID()
                WHERE [ClientCommandId] = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ClientCommandId",
                table: "Reservations",
                column: "ClientCommandId",
                unique: true);

            // "What have I got booked?" - the diner's own list - and the rolling no-show count
            // behind the approval rule, which reads the same two columns.
            migrationBuilder.CreateIndex(
                name: "IX_Reservations_DinerUserId_StartUtc",
                table: "Reservations",
                columns: new[] { "DinerUserId", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_ClientCommandId",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_DinerUserId_StartUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CancelledAfterDeadline",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ClientCommandId",
                table: "Reservations");
        }
    }
}
