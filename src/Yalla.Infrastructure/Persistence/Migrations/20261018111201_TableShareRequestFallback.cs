using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>TableShareRequests.FallbackTrigger</c>: why the later booking would still wait without its
    /// question to the guest before it - null when it would be confirmed - so that when that guest gets out
    /// of the way the booking is decided again without the question. And the floor's questions on another
    /// day lose their two-hour clock: such a booking waits for the floor as any booking the floor decides.
    /// </summary>
    /// <remarks>
    /// Existing floor questions get no fallback - none is recorded, so the later booking is confirmed
    /// when its earlier guest goes - and their later bookings lose the two-hour deadline (short-notice ones,
    /// <c>ApprovalTrigger</c> 4, keep their fifteen minutes). Down drops the column; the deadlines are not
    /// put back.
    /// </remarks>
    public partial class TableShareRequestFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FallbackTrigger",
                table: "TableShareRequests",
                type: "int",
                nullable: true);

            // The floor's questions on another day have no clock: the later booking waits as any booking
            // the floor decides, and the question is open until its start.
            migrationBuilder.Sql(
                """
                UPDATE r SET r.ApprovalExpiresAtUtc = NULL
                FROM Reservations r
                JOIN TableShareRequests q ON q.LaterReservationId = r.Id
                WHERE q.Status = 1 AND q.AsksEarlierGuest = 0 AND r.ApprovalTrigger = 5 AND r.Status = 1;

                UPDATE q SET q.ExpiresAtUtc = r.StartUtc
                FROM TableShareRequests q
                JOIN Reservations r ON r.Id = q.LaterReservationId
                WHERE q.Status = 1 AND q.AsksEarlierGuest = 0 AND r.ApprovalTrigger = 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FallbackTrigger",
                table: "TableShareRequests");
        }
    }
}
