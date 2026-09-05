using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderingAndBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientCommandId",
                table: "TabOrders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "EstimatedReadyAtUtc",
                table: "TabOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OnBehalfOfParticipantId",
                table: "TabOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTableAttributed",
                table: "TabOrderLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "TabOrderLines",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientCommandId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "TipAmd",
                table: "Payments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ServiceRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiningTableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Preset = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceRequests_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequests_DiningTables_DiningTableId",
                        column: x => x.DiningTableId,
                        principalTable: "DiningTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequests_StaffMembers_AcknowledgedByStaffId",
                        column: x => x.AcknowledgedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequests_TabParticipants_RequestedByParticipantId",
                        column: x => x.RequestedByParticipantId,
                        principalTable: "TabParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequests_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    AmountAmd = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabAdjustments_StaffMembers_CreatedByStaffId",
                        column: x => x.CreatedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabAdjustments_TabOrderLines_TabOrderLineId",
                        column: x => x.TabOrderLineId,
                        principalTable: "TabOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabAdjustments_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActorType = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabEvents_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_OnBehalfOfParticipantId",
                table: "TabOrders",
                column: "OnBehalfOfParticipantId");

            migrationBuilder.CreateIndex(
                name: "UX_Payments_ClientCommandId",
                table: "Payments",
                column: "ClientCommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_AcknowledgedByStaffId",
                table: "ServiceRequests",
                column: "AcknowledgedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_DiningTableId",
                table: "ServiceRequests",
                column: "DiningTableId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_Open",
                table: "ServiceRequests",
                columns: new[] { "BranchId", "CreatedAtUtc" },
                filter: "[AcknowledgedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_RequestedByParticipantId",
                table: "ServiceRequests",
                column: "RequestedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_TabId_CreatedAtUtc",
                table: "ServiceRequests",
                columns: new[] { "TabId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TabAdjustments_CreatedByStaffId",
                table: "TabAdjustments",
                column: "CreatedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TabAdjustments_TabId",
                table: "TabAdjustments",
                column: "TabId");

            migrationBuilder.CreateIndex(
                name: "IX_TabAdjustments_TabOrderLineId",
                table: "TabAdjustments",
                column: "TabOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_TabEvents_TabId_Sequence",
                table: "TabEvents",
                columns: new[] { "TabId", "Sequence" });

            migrationBuilder.AddForeignKey(
                name: "FK_TabOrders_TabParticipants_OnBehalfOfParticipantId",
                table: "TabOrders",
                column: "OnBehalfOfParticipantId",
                principalTable: "TabParticipants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TabOrders_TabParticipants_OnBehalfOfParticipantId",
                table: "TabOrders");

            migrationBuilder.DropTable(
                name: "ServiceRequests");

            migrationBuilder.DropTable(
                name: "TabAdjustments");

            migrationBuilder.DropTable(
                name: "TabEvents");

            migrationBuilder.DropIndex(
                name: "IX_TabOrders_OnBehalfOfParticipantId",
                table: "TabOrders");

            migrationBuilder.DropIndex(
                name: "UX_Payments_ClientCommandId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ClientCommandId",
                table: "TabOrders");

            migrationBuilder.DropColumn(
                name: "EstimatedReadyAtUtc",
                table: "TabOrders");

            migrationBuilder.DropColumn(
                name: "OnBehalfOfParticipantId",
                table: "TabOrders");

            migrationBuilder.DropColumn(
                name: "IsTableAttributed",
                table: "TabOrderLines");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "TabOrderLines");

            migrationBuilder.DropColumn(
                name: "ClientCommandId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TipAmd",
                table: "Payments");
        }
    }
}
