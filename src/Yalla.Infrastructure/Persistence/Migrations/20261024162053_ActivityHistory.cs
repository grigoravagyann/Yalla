using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The activity log (<c>ActivityEntries</c>) and whose each table, order and payment was: the table's waiter on the
    /// sitting, the waiter an order and a payment were taken under, who took a payment, and who claimed a call. See <c>docs/activity.md</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Backfilled from what was already stored</b>, so the results pages are not empty for the evenings before this:
    /// a sitting's waiter is whoever seated it, else the first waiter to order on it; an order and a payment were taken
    /// under that waiter (nobody transferred a table before transfers existed); a cash payment was taken by the staff
    /// member its <c>PaymentRecorded</c> event names; a call already acknowledged was claimed by whoever acknowledged it. The History itself starts empty - nothing was logged to fill it.
    /// </para>
    /// </remarks>
    public partial class ActivityHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServerStaffMemberId",
                table: "TabOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ServerAssignedAtUtc",
                table: "TableSessions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerStaffMemberId",
                table: "TableSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAtUtc",
                table: "ServiceRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedByStaffId",
                table: "ServiceRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedByStaffId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerStaffMemberId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VenueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SubjectType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TableLabel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    GuestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ItemNames = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AmountAmd = table.Column<long>(type: "bigint", nullable: true),
                    IsMoney = table.Column<bool>(type: "bit", nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SummaryJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_ServerStaffMemberId",
                table: "TabOrders",
                column: "ServerStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_ServerStaffMemberId",
                table: "TableSessions",
                column: "ServerStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_BranchId_ClaimedAtUtc",
                table: "ServiceRequests",
                columns: new[] { "BranchId", "ClaimedAtUtc" },
                filter: "[ClaimedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_ClaimedByStaffId",
                table: "ServiceRequests",
                column: "ClaimedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RecordedByStaffId",
                table: "Payments",
                column: "RecordedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ServerStaffMemberId",
                table: "Payments",
                column: "ServerStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_BranchId_OccurredAtUtc",
                table: "ActivityEntries",
                columns: new[] { "BranchId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_Retention",
                table: "ActivityEntries",
                columns: new[] { "IsMoney", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_StaffMemberId_OccurredAtUtc",
                table: "ActivityEntries",
                columns: new[] { "StaffMemberId", "OccurredAtUtc" },
                filter: "[StaffMemberId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_Subject",
                table: "ActivityEntries",
                columns: new[] { "SubjectType", "SubjectId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_TableSessionId",
                table: "ActivityEntries",
                columns: new[] { "TableSessionId", "OccurredAtUtc" },
                filter: "[TableSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_VenueId_OccurredAtUtc",
                table: "ActivityEntries",
                columns: new[] { "VenueId", "OccurredAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_StaffMembers_RecordedByStaffId",
                table: "Payments",
                column: "RecordedByStaffId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_StaffMembers_ServerStaffMemberId",
                table: "Payments",
                column: "ServerStaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_StaffMembers_ClaimedByStaffId",
                table: "ServiceRequests",
                column: "ClaimedByStaffId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TableSessions_StaffMembers_ServerStaffMemberId",
                table: "TableSessions",
                column: "ServerStaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TabOrders_StaffMembers_ServerStaffMemberId",
                table: "TabOrders",
                column: "ServerStaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Whose each sitting was: who seated it, else the first waiter to order on it.
            migrationBuilder.Sql(
                """
                UPDATE s SET
                    ServerStaffMemberId = COALESCE(s.SeatedByStaffId, firstOrder.PlacedByStaffId),
                    ServerAssignedAtUtc = CASE WHEN s.SeatedByStaffId IS NOT NULL THEN s.SeatedAtUtc ELSE firstOrder.PlacedAtUtc END
                FROM dbo.TableSessions s
                OUTER APPLY (
                    SELECT TOP (1) o.PlacedByStaffId, o.PlacedAtUtc
                    FROM dbo.Tabs t
                    JOIN dbo.TabOrders o ON o.TabId = t.Id
                    WHERE t.TableSessionId = s.Id AND o.PlacedByStaffId IS NOT NULL
                    ORDER BY o.PlacedAtUtc
                ) firstOrder
                WHERE s.ServerStaffMemberId IS NULL
                  AND COALESCE(s.SeatedByStaffId, firstOrder.PlacedByStaffId) IS NOT NULL;
                """);

            // Orders and payments were taken under that waiter: no table was ever transferred before now.
            migrationBuilder.Sql(
                """
                UPDATE o SET ServerStaffMemberId = s.ServerStaffMemberId
                FROM dbo.TabOrders o
                JOIN dbo.Tabs t ON t.Id = o.TabId
                JOIN dbo.TableSessions s ON s.Id = t.TableSessionId
                WHERE o.ServerStaffMemberId IS NULL AND s.ServerStaffMemberId IS NOT NULL;

                UPDATE p SET ServerStaffMemberId = s.ServerStaffMemberId
                FROM dbo.Payments p
                JOIN dbo.Tabs t ON t.Id = p.TabId
                JOIN dbo.TableSessions s ON s.Id = t.TableSessionId
                WHERE p.ServerStaffMemberId IS NULL AND s.ServerStaffMemberId IS NOT NULL;
                """);

            // Who took each payment: the staff member its PaymentRecorded event (15) names, when that is still a row.
            migrationBuilder.Sql(
                """
                UPDATE p SET RecordedByStaffId = e.ActorId
                FROM dbo.Payments p
                JOIN dbo.TabEvents e
                  ON e.TabId = p.TabId
                 AND e.Type = 15
                 AND e.ActorType = 2
                 AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(e.PayloadJson, '$.paymentId')) = p.Id
                WHERE p.RecordedByStaffId IS NULL
                  AND EXISTS (SELECT 1 FROM dbo.StaffMembers m WHERE m.Id = e.ActorId);
                """);

            // Every call already answered was claimed by whoever answered it - the response time's start and end.
            migrationBuilder.Sql(
                """
                UPDATE dbo.ServiceRequests SET ClaimedAtUtc = AcknowledgedAtUtc, ClaimedByStaffId = AcknowledgedByStaffId
                WHERE ClaimedAtUtc IS NULL AND AcknowledgedAtUtc IS NOT NULL AND AcknowledgedByStaffId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_StaffMembers_RecordedByStaffId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_StaffMembers_ServerStaffMemberId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_StaffMembers_ClaimedByStaffId",
                table: "ServiceRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_TableSessions_StaffMembers_ServerStaffMemberId",
                table: "TableSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_TabOrders_StaffMembers_ServerStaffMemberId",
                table: "TabOrders");

            migrationBuilder.DropTable(
                name: "ActivityEntries");

            migrationBuilder.DropIndex(
                name: "IX_TabOrders_ServerStaffMemberId",
                table: "TabOrders");

            migrationBuilder.DropIndex(
                name: "IX_TableSessions_ServerStaffMemberId",
                table: "TableSessions");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_BranchId_ClaimedAtUtc",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_ClaimedByStaffId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RecordedByStaffId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ServerStaffMemberId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ServerStaffMemberId",
                table: "TabOrders");

            migrationBuilder.DropColumn(
                name: "ServerAssignedAtUtc",
                table: "TableSessions");

            migrationBuilder.DropColumn(
                name: "ServerStaffMemberId",
                table: "TableSessions");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ClaimedByStaffId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "RecordedByStaffId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ServerStaffMemberId",
                table: "Payments");
        }
    }
}
