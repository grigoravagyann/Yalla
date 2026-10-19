using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Special opening days: <c>SpecialOpeningDays</c> (one row per branch and date, unique, with an optional
    /// note a diner is shown) and their <c>SpecialOpeningBlocks</c> - none for closed all day. A special day
    /// replaces its date's weekly blocks for every reader of opening hours.
    /// </summary>
    /// <remarks>Two new tables and nothing else; existing branches have no special days. Down drops both.</remarks>
    public partial class SpecialOpeningDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpecialOpeningDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialOpeningDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecialOpeningDays_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpecialOpeningBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpecialOpeningDayId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    ClosesNextDay = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialOpeningBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecialOpeningBlocks_SpecialOpeningDays_SpecialOpeningDayId",
                        column: x => x.SpecialOpeningDayId,
                        principalTable: "SpecialOpeningDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpecialOpeningBlocks_SpecialOpeningDayId",
                table: "SpecialOpeningBlocks",
                column: "SpecialOpeningDayId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecialOpeningDays_BranchId_Date",
                table: "SpecialOpeningDays",
                columns: new[] { "BranchId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpecialOpeningBlocks");

            migrationBuilder.DropTable(
                name: "SpecialOpeningDays");
        }
    }
}
