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
