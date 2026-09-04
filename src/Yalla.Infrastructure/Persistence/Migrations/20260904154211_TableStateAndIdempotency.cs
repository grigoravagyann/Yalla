using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The three corrections that make table state derivable rather than stored, plus the
    /// idempotency key the offline staff app needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retiring <c>TableStatus.Reserved</c> and <c>ReservationStatus.Late</c> changes no column
    /// type - enums persist as int - so without the check constraints below this migration would
    /// alter nothing at the database level and a stray <c>3</c> would still be insertable by any
    /// writer that bypassed the application. The constraints are what actually retire the values.
    /// </para>
    /// <para>
    /// The vacated numbers are deliberately left out of the allowed lists rather than renumbering
    /// the remaining members, so no existing row can change meaning.
    /// </para>
    /// </remarks>
    public partial class TableStateAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replaced below by a unique index on DiningTableId alone, which is the invariant
            // that matters: at most one open session per table.
            migrationBuilder.DropIndex(
                name: "IX_TableSessions_Open",
                table: "TableSessions");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientCommandId",
                table: "TableStateChanges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TableSessionId",
                table: "TableStateChanges",
                type: "uniqueidentifier",
                nullable: true);

            // Existing rows all took the empty-Guid default, which the unique index below would
            // reject the moment there is more than one of them. Backfill distinct values first so
            // this migration is safe on a database that already has audit history, not only on an
            // empty one.
            migrationBuilder.Sql(
                """
                UPDATE [TableStateChanges]
                SET [ClientCommandId] = NEWID()
                WHERE [ClientCommandId] = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TableStateChanges_ClientCommandId",
                table: "TableStateChanges",
                column: "ClientCommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_OpenByBranch",
                table: "TableSessions",
                column: "BranchId",
                filter: "[ClosedAtUtc] IS NULL");

            // At most one open session per table, enforced by the database. This is the backstop
            // for the double-seat: even if the state machine is bypassed, SQL Server refuses to
            // have two parties sitting at table 7.
            migrationBuilder.CreateIndex(
                name: "UX_TableSessions_OpenPerTable",
                table: "TableSessions",
                column: "DiningTableId",
                unique: true,
                filter: "[ClosedAtUtc] IS NULL");

            // TableStatus: Free = 1, Held = 2, Occupied = 4, OutOfService = 5.
            // 3 was Reserved and is now derived at read time - permanently vacant.
            migrationBuilder.Sql(
                """
                ALTER TABLE [DiningTables] WITH CHECK
                ADD CONSTRAINT [CK_DiningTables_Status]
                CHECK ([Status] IN (1, 2, 4, 5));
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE [TableStateChanges] WITH CHECK
                ADD CONSTRAINT [CK_TableStateChanges_FromStatus]
                CHECK ([FromStatus] IN (1, 2, 4, 5));
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE [TableStateChanges] WITH CHECK
                ADD CONSTRAINT [CK_TableStateChanges_ToStatus]
                CHECK ([ToStatus] IN (1, 2, 4, 5));
                """);

            // ReservationStatus: PendingApproval = 1, Confirmed = 2, Seated = 4, Completed = 5,
            // CancelledByDiner = 6, CancelledByVenue = 7, NoShow = 8.
            // 3 was Late and is now derived from the clock - permanently vacant.
            migrationBuilder.Sql(
                """
                ALTER TABLE [Reservations] WITH CHECK
                ADD CONSTRAINT [CK_Reservations_Status]
                CHECK ([Status] IN (1, 2, 4, 5, 6, 7, 8));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE [Reservations] DROP CONSTRAINT [CK_Reservations_Status];");
            migrationBuilder.Sql("ALTER TABLE [TableStateChanges] DROP CONSTRAINT [CK_TableStateChanges_ToStatus];");
            migrationBuilder.Sql("ALTER TABLE [TableStateChanges] DROP CONSTRAINT [CK_TableStateChanges_FromStatus];");
            migrationBuilder.Sql("ALTER TABLE [DiningTables] DROP CONSTRAINT [CK_DiningTables_Status];");

            migrationBuilder.DropIndex(
                name: "IX_TableStateChanges_ClientCommandId",
                table: "TableStateChanges");

            migrationBuilder.DropIndex(
                name: "IX_TableSessions_OpenByBranch",
                table: "TableSessions");

            migrationBuilder.DropIndex(
                name: "UX_TableSessions_OpenPerTable",
                table: "TableSessions");

            migrationBuilder.DropColumn(
                name: "ClientCommandId",
                table: "TableStateChanges");

            migrationBuilder.DropColumn(
                name: "TableSessionId",
                table: "TableStateChanges");

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_Open",
                table: "TableSessions",
                columns: new[] { "BranchId", "DiningTableId" },
                filter: "[ClosedAtUtc] IS NULL");
        }
    }
}
