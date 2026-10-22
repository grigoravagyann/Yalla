using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Takeaway and "I'm coming" (2026-10-22): <c>TakeawayOrders</c> and <c>TakeawayOrderLines</c>; the branch's takeaway
    /// settings (off, manual accept, 20 min prep, last order 30 min before closing, 5 in progress); the dish's
    /// <c>AvailableForTakeaway</c> (true); the booking's <c>GuestRunningLateMinutes</c>, <c>GuestRunningLateAtUtc</c> and
    /// <c>StaffAskedOnMyWayAtUtc</c>; and the <c>takeaway-accepted</c>, <c>takeaway-ready</c>, <c>takeaway-declined</c> feed
    /// kinds. No backfill: a new feature starts empty and off.
    /// </summary>
    public partial class TakeawayAndRunningLate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestRunningLateAtUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GuestRunningLateMinutes",
                table: "Reservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StaffAskedOnMyWayAtUtc",
                table: "Reservations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AvailableForTakeaway",
                table: "MenuItems",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "TakeawayAutoAccept",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TakeawayEnabled",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TakeawayLastOrderMinutes",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "TakeawayMaxInProgress",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "TakeawayPrepMinutes",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.CreateTable(
                name: "TakeawayOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    GuestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GuestPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PickupAsap = table.Column<bool>(type: "bit", nullable: false),
                    RequestedPickupUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestedPickupLocalTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AcceptDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadyAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MarkedReadyAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MarkedReadyByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CollectedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentMethod = table.Column<int>(type: "int", nullable: true),
                    PaidAmountAmd = table.Column<long>(type: "bigint", nullable: true),
                    DeclinedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclinedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeclinedBecause = table.Column<int>(type: "int", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NoShowAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NoShowByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TakeawayOrders", x => x.Id);
                    table.CheckConstraint("CK_TakeawayOrders_PaidAmount", "[PaidAmountAmd] IS NULL OR [PaidAmountAmd] >= 0");
                    table.CheckConstraint("CK_TakeawayOrders_Status", "[Status] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_StaffMembers_AcceptedByStaffId",
                        column: x => x.AcceptedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_StaffMembers_CollectedByStaffId",
                        column: x => x.CollectedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_StaffMembers_DeclinedByStaffId",
                        column: x => x.DeclinedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_StaffMembers_MarkedReadyByStaffId",
                        column: x => x.MarkedReadyByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrders_StaffMembers_NoShowByStaffId",
                        column: x => x.NoShowByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TakeawayOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TakeawayOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitPriceAmdSnapshot = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TakeawayOrderLines", x => x.Id);
                    table.CheckConstraint("CK_TakeawayOrderLines_Price", "[UnitPriceAmdSnapshot] >= 0");
                    table.CheckConstraint("CK_TakeawayOrderLines_Quantity", "[Quantity] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_TakeawayOrderLines_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakeawayOrderLines_TakeawayOrders_TakeawayOrderId",
                        column: x => x.TakeawayOrderId,
                        principalTable: "TakeawayOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked', 'booking-on-your-way', 'takeaway-accepted', 'takeaway-ready', 'takeaway-declined')");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrderLines_MenuItemId",
                table: "TakeawayOrderLines",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrderLines_TakeawayOrderId_Position",
                table: "TakeawayOrderLines",
                columns: new[] { "TakeawayOrderId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_AcceptDeadlineUtc",
                table: "TakeawayOrders",
                column: "AcceptDeadlineUtc",
                filter: "[AcceptDeadlineUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_AcceptedByStaffId",
                table: "TakeawayOrders",
                column: "AcceptedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_BranchId_Status",
                table: "TakeawayOrders",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_CollectedByStaffId",
                table: "TakeawayOrders",
                column: "CollectedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_DeclinedByStaffId",
                table: "TakeawayOrders",
                column: "DeclinedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_DinerUserId_CreatedAtUtc",
                table: "TakeawayOrders",
                columns: new[] { "DinerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_MarkedReadyByStaffId",
                table: "TakeawayOrders",
                column: "MarkedReadyByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_NoShowByStaffId",
                table: "TakeawayOrders",
                column: "NoShowByStaffId");

            migrationBuilder.CreateIndex(
                name: "UX_TakeawayOrders_BranchId_LocalDate_Code",
                table: "TakeawayOrders",
                columns: new[] { "BranchId", "LocalDate", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TakeawayOrders_ClientCommandId",
                table: "TakeawayOrders",
                column: "ClientCommandId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TakeawayOrderLines");

            migrationBuilder.DropTable(
                name: "TakeawayOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The feed rows and queued pushes of kinds the older schema does not know.
            migrationBuilder.Sql(
                "DELETE FROM dbo.DinerNotifications WHERE [Kind] IN ('takeaway-accepted', 'takeaway-ready', 'takeaway-declined');");
            migrationBuilder.Sql(
                "DELETE FROM dbo.OutboxMessages WHERE [Type] IN ('takeaway.accepted', 'takeaway.ready', 'takeaway.declined');");

            migrationBuilder.DropColumn(
                name: "GuestRunningLateAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "GuestRunningLateMinutes",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "StaffAskedOnMyWayAtUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "AvailableForTakeaway",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "TakeawayAutoAccept",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "TakeawayEnabled",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "TakeawayLastOrderMinutes",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "TakeawayMaxInProgress",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "TakeawayPrepMinutes",
                table: "Branches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked', 'booking-on-your-way')");
        }
    }
}
