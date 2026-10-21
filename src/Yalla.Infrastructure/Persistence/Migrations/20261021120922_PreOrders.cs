using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Pre-orders on a booking (2026-10-21): <c>PreOrders</c> and <c>PreOrderLines</c>, the branch's switch and minutes
    /// (off, 30), the dish's <c>AvailableForPreOrder</c> (true), the booking's <c>GuestConfirmedOnWayAtUtc</c>, and the
    /// <c>booking-on-your-way</c> feed kind. No backfill: a new feature starts empty and off.
    /// </summary>
    public partial class PreOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestConfirmedOnWayAtUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AvailableForPreOrder",
                table: "MenuItems",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptsPreOrders",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PreOrderMinutesBefore",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "PreOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    KitchenStatus = table.Column<int>(type: "int", nullable: true),
                    TabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotComingAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotComingByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreOrders_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreOrders_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreOrders_StaffMembers_NotComingByStaffId",
                        column: x => x.NotComingByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreOrders_StaffMembers_SentByStaffId",
                        column: x => x.SentByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PreOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitPriceAmdSnapshot = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreOrderLines", x => x.Id);
                    table.CheckConstraint("CK_PreOrderLines_Price", "[UnitPriceAmdSnapshot] >= 0");
                    table.CheckConstraint("CK_PreOrderLines_Quantity", "[Quantity] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_PreOrderLines_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreOrderLines_PreOrders_PreOrderId",
                        column: x => x.PreOrderId,
                        principalTable: "PreOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked', 'booking-on-your-way')");

            migrationBuilder.CreateIndex(
                name: "IX_PreOrderLines_MenuItemId",
                table: "PreOrderLines",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PreOrderLines_PreOrderId_Position",
                table: "PreOrderLines",
                columns: new[] { "PreOrderId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreOrders_BranchId_Held",
                table: "PreOrders",
                column: "BranchId",
                filter: "[SentAtUtc] IS NOT NULL AND [TabOrderId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PreOrders_BranchId_ReservationId",
                table: "PreOrders",
                columns: new[] { "BranchId", "ReservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PreOrders_NotComingByStaffId",
                table: "PreOrders",
                column: "NotComingByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_PreOrders_SentByStaffId",
                table: "PreOrders",
                column: "SentByStaffId");

            migrationBuilder.CreateIndex(
                name: "UX_PreOrders_ReservationId",
                table: "PreOrders",
                column: "ReservationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreOrderLines");

            migrationBuilder.DropTable(
                name: "PreOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The feed rows and queued pushes of a kind the older schema does not know.
            migrationBuilder.Sql("DELETE FROM dbo.DinerNotifications WHERE [Kind] = 'booking-on-your-way';");
            migrationBuilder.Sql("DELETE FROM dbo.OutboxMessages WHERE [Type] = 'reservation.on-your-way';");

            migrationBuilder.DropColumn(
                name: "GuestConfirmedOnWayAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "AvailableForPreOrder",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "AcceptsPreOrders",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "PreOrderMinutesBefore",
                table: "Branches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked')");
        }
    }
}
