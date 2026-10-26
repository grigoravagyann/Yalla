using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The branch's change stream carries takeaway (signal 8, <c>TableChangeSignal.TakeawayChanged</c>): a row about the
    /// counter, with no table. <c>TableStateChanges.DiningTableId</c> becomes nullable; every existing row names a table
    /// and keeps it, so <c>Up</c> moves no data.
    /// </summary>
    public partial class TakeawaySignalRowsHaveNoTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "DiningTableId",
                table: "TableStateChanges",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the takeaway signal rows have no table. They told the counter to re-read; nothing replays them, and
            // the column cannot go back to NOT NULL with them in it.
            migrationBuilder.Sql("DELETE FROM [TableStateChanges] WHERE [DiningTableId] IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "DiningTableId",
                table: "TableStateChanges",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
