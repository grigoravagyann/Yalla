using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Short-notice bookings wait for the floor: <c>Reservations</c> gains <c>ApprovalTrigger</c> (why a
    /// booking went to approval, stored rather than worked out again), <c>ApprovalExpiresAtUtc</c> (when
    /// the venue's fifteen minutes to answer run out) and <c>DeclineReason</c> (1 a member of staff said
    /// no, 2 nobody answered in time).
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three are nullable and every existing row keeps them null. A pending booking from before
    /// has its reason worked out from the branch's policy as it always was; one from before was never
    /// at short notice, so it has no deadline; and a decline from before says nothing about how it
    /// came about, which is the truth - nothing recorded it.
    /// </para>
    /// <para>
    /// <c>IX_Reservations_ApprovalExpiresAtUtc</c> is filtered to the rows with a deadline. The column
    /// is cleared the moment a booking stops waiting, so the index holds only the requests open right
    /// now - the approval sweep's whole read. Down drops the index and the columns; nothing else reads
    /// them.
    /// </para>
    /// </remarks>
    public partial class ShortNoticeApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalExpiresAtUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalTrigger",
                table: "Reservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeclineReason",
                table: "Reservations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ApprovalExpiresAtUtc",
                table: "Reservations",
                column: "ApprovalExpiresAtUtc",
                filter: "[ApprovalExpiresAtUtc] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_ApprovalExpiresAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ApprovalExpiresAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ApprovalTrigger",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "DeclineReason",
                table: "Reservations");
        }
    }
}
