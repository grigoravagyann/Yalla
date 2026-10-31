using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffTakenBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OnWayRecordedByStaffId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TakenByStaffId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_TakenByStaffId_StartUtc",
                table: "Reservations",
                columns: new[] { "TakenByStaffId", "StartUtc" },
                filter: "[TakenByStaffId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_TakenByStaffId_StartUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "OnWayRecordedByStaffId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "TakenByStaffId",
                table: "Reservations");
        }
    }
}
