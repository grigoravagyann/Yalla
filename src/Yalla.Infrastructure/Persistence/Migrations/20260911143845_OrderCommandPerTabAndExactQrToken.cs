using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderCommandPerTabAndExactQrToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every token was generated lower-case, but the column never said so and the default
            // collation ignored case. Lower-case any that are not before it starts comparing
            // exactly, so a sticker already printed keeps opening its table. Only rows that change
            // are touched: an update moves a table's row version, and a tablet holding the old one
            // would see its next queued command refused as stale.
            migrationBuilder.Sql(
                """
                UPDATE [DiningTables]
                SET [QrToken] = LOWER([QrToken])
                WHERE [QrToken] COLLATE Latin1_General_100_BIN2 <> LOWER([QrToken]) COLLATE Latin1_General_100_BIN2;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "QrToken",
                table: "DiningTables",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            // A retry that raced its original may already have left two orders under one command id
            // on a tab - the race this index closes. Both are real orders the kitchen may have
            // cooked, so neither is deleted: every one after the first gets a fresh command id that
            // no client holds, and the index can then be built.
            migrationBuilder.Sql(
                """
                WITH [Ranked] AS (
                    SELECT [ClientCommandId], ROW_NUMBER() OVER (
                        PARTITION BY [TabId], [ClientCommandId]
                        ORDER BY [PlacedAtUtc], [Id]) AS [Position]
                    FROM [TabOrders])
                UPDATE [Ranked] SET [ClientCommandId] = NEWID() WHERE [Position] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_TabOrders_TabId_ClientCommandId",
                table: "TabOrders",
                columns: new[] { "TabId", "ClientCommandId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TabOrders_TabId_ClientCommandId",
                table: "TabOrders");

            migrationBuilder.AlterColumn<string>(
                name: "QrToken",
                table: "DiningTables",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
