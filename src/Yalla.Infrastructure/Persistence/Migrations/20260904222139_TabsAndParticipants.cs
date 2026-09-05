using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TabsAndParticipants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tabs_TableSessionId",
                table: "Tabs");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientCommandId",
                table: "Tabs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "UX_Tabs_ClientCommandId",
                table: "Tabs",
                column: "ClientCommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs",
                column: "TableSessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Tabs_ClientCommandId",
                table: "Tabs");

            migrationBuilder.DropIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs");

            migrationBuilder.DropColumn(
                name: "ClientCommandId",
                table: "Tabs");

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_TableSessionId",
                table: "Tabs",
                column: "TableSessionId");
        }
    }
}
