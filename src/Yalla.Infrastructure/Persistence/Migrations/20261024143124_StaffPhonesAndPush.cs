using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffPhonesAndPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ForStaffMemberId",
                table: "StaffEnrolmentCodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                table: "StaffEnrolmentCodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BoundStaffMemberId",
                table: "StaffDevices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "StaffDevices",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "StaffDevices",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PushKinds",
                table: "StaffDevices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnlyDuringShift",
                table: "StaffDevices",
                type: "bit",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EndpointHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPushSubscriptions_StaffDevices_StaffDeviceId",
                        column: x => x.StaffDeviceId,
                        principalTable: "StaffDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffEnrolmentCodes_ForStaffMemberId",
                table: "StaffEnrolmentCodes",
                column: "ForStaffMemberId",
                filter: "[ForStaffMemberId] IS NOT NULL AND [RedeemedAtUtc] IS NULL AND [SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDevices_BoundStaffMemberId_RevokedAtUtc",
                table: "StaffDevices",
                columns: new[] { "BoundStaffMemberId", "RevokedAtUtc" },
                filter: "[BoundStaffMemberId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffDevices_KindBinding",
                table: "StaffDevices",
                sql: "([Kind] = 1 AND [BoundStaffMemberId] IS NULL) OR ([Kind] = 2 AND [BoundStaffMemberId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "UX_StaffPushSubscriptions_EndpointHash",
                table: "StaffPushSubscriptions",
                column: "EndpointHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_StaffPushSubscriptions_StaffDeviceId",
                table: "StaffPushSubscriptions",
                column: "StaffDeviceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffDevices_StaffMembers_BoundStaffMemberId",
                table: "StaffDevices",
                column: "BoundStaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StaffDevices_StaffMembers_BoundStaffMemberId",
                table: "StaffDevices");

            migrationBuilder.DropTable(
                name: "StaffPushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_StaffEnrolmentCodes_ForStaffMemberId",
                table: "StaffEnrolmentCodes");

            migrationBuilder.DropIndex(
                name: "IX_StaffDevices_BoundStaffMemberId_RevokedAtUtc",
                table: "StaffDevices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffDevices_KindBinding",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "ForStaffMemberId",
                table: "StaffEnrolmentCodes");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "StaffEnrolmentCodes");

            migrationBuilder.DropColumn(
                name: "BoundStaffMemberId",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "Locale",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "PushKinds",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "PushOnlyDuringShift",
                table: "StaffDevices");
        }
    }
}
