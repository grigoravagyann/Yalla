using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DinerSessionGenerationAndDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_DinerUsers_PhoneE164",
                table: "DinerUsers");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneE164",
                table: "DinerUsers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "DinerUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionGeneration",
                table: "DinerUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UX_DinerUsers_PhoneE164",
                table: "DinerUsers",
                column: "PhoneE164",
                unique: true,
                filter: "[PhoneE164] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A deleted account keeps its row with no number (a tombstone), and the column put back
            // below is NOT NULL under an unfiltered unique index: with tombstones present the alter
            // fails outright, and a '' default would collide on the second one. Each is given a
            // placeholder no E.164 number can equal, numbered past any an earlier rollback left, and
            // kept inactive - so the rows other tables still point at survive the rollback.
            migrationBuilder.Sql(
                """
                DECLARE @offset bigint = (
                    SELECT ISNULL(MAX(TRY_CAST(SUBSTRING([PhoneE164], 9, 12) AS bigint)), 0)
                    FROM [DinerUsers]
                    WHERE [PhoneE164] LIKE N'deleted:%');

                WITH [Tombstones] AS (
                    SELECT [PhoneE164], [IsActive], ROW_NUMBER() OVER (ORDER BY [Id]) AS [RowNumber]
                    FROM [DinerUsers]
                    WHERE [PhoneE164] IS NULL)
                UPDATE [Tombstones]
                SET [PhoneE164] = CONCAT(N'deleted:', @offset + [RowNumber]),
                    [IsActive] = CAST(0 AS bit);
                """);

            migrationBuilder.DropIndex(
                name: "UX_DinerUsers_PhoneE164",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "SessionGeneration",
                table: "DinerUsers");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneE164",
                table: "DinerUsers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_DinerUsers_PhoneE164",
                table: "DinerUsers",
                column: "PhoneE164",
                unique: true);
        }
    }
}
