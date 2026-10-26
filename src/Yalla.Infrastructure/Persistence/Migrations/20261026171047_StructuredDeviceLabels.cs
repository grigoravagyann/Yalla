using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StructuredDeviceLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable and not backfilled, on purpose: a phone paired before this keeps its English Name as all there is,
            // and an activity entry already written keeps the DeviceLabel it was written with.
            migrationBuilder.AddColumn<int>(
                name: "Browser",
                table: "StaffDevices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Platform",
                table: "StaffDevices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeviceKind",
                table: "ActivityEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceOwnerName",
                table: "ActivityEntries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Browser",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "DeviceKind",
                table: "ActivityEntries");

            migrationBuilder.DropColumn(
                name: "DeviceOwnerName",
                table: "ActivityEntries");
        }
    }
}
