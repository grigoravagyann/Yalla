using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicBranchProfileAndManageTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManageTokenHash",
                table: "Reservations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptsWebBookings",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PhoneE164",
                table: "Branches",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ManageTokenHash",
                table: "Reservations",
                column: "ManageTokenHash",
                unique: true,
                filter: "[ManageTokenHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_ManageTokenHash",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ManageTokenHash",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "AcceptsWebBookings",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "PhoneE164",
                table: "Branches");
        }
    }
}
