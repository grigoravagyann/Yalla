using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportingIndexesAndReservationChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TabOrderLines_MenuItemId",
                table: "TabOrderLines");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Reservations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_BranchId_ClosedAtUtc",
                table: "Tabs",
                columns: new[] { "BranchId", "ClosedAtUtc" },
                filter: "[ClosedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_PlacedAtUtc",
                table: "TabOrders",
                column: "PlacedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_MenuItemId_TabOrderId_Reporting",
                table: "TabOrderLines",
                columns: new[] { "MenuItemId", "TabOrderId" })
                .Annotation("SqlServer:Include", new[] { "Quantity", "UnitPriceAmdSnapshot", "VoidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_BranchId_SeatedAtUtc_Reporting",
                table: "TableSessions",
                columns: new[] { "BranchId", "SeatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "ClosedAtUtc", "Source", "PartySize" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BranchId_StartUtc_Status",
                table: "Reservations",
                columns: new[] { "BranchId", "StartUtc", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tabs_BranchId_ClosedAtUtc",
                table: "Tabs");

            migrationBuilder.DropIndex(
                name: "IX_TabOrders_PlacedAtUtc",
                table: "TabOrders");

            migrationBuilder.DropIndex(
                name: "IX_TabOrderLines_MenuItemId_TabOrderId_Reporting",
                table: "TabOrderLines");

            migrationBuilder.DropIndex(
                name: "IX_TableSessions_BranchId_SeatedAtUtc_Reporting",
                table: "TableSessions");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_BranchId_StartUtc_Status",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Reservations");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_MenuItemId",
                table: "TabOrderLines",
                column: "MenuItemId");
        }
    }
}
