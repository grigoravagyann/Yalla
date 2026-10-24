using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Guest messages (2026-10-23): <c>Conversations</c> - one per booking or takeaway order, made by its first message -
    /// and <c>ConversationMessages</c>; the branch's message settings (guests and waiters may write, the app's own quick
    /// replies, a reminder after 3 minutes); the <c>venue-message</c> feed kind; and <c>IX_Reservations_BranchId_EndUtc_Live</c>,
    /// the live bookings by end, which the tablet's threads in view seek on every floor poll. No backfill: every branch
    /// starts with the defaults and no thread. See <c>docs/messages.md</c>.
    /// </summary>
    public partial class GuestMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<bool>(
                name: "GuestsCanMessage",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "MessageRemindAfterMinutes",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "MessageTemplatesJson",
                table: "Branches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitersCanMessage",
                table: "Branches",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakeawayOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastMessageUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GuestLastReadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VenueLastReadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_OneSubject", "([ReservationId] IS NOT NULL AND [TakeawayOrderId] IS NULL) OR ([ReservationId] IS NULL AND [TakeawayOrderId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Conversations_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_TakeawayOrders_TakeawayOrderId",
                        column: x => x.TakeawayOrderId,
                        principalTable: "TakeawayOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Author = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AnswerKind = table.Column<int>(type: "int", nullable: true),
                    LateMinutes = table.Column<int>(type: "int", nullable: true),
                    HeldUntilLocal = table.Column<TimeOnly>(type: "time", nullable: true),
                    StaffMemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaffName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.CheckConstraint("CK_ConversationMessages_Author", "[Author] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_ConversationMessages_Body", "([Kind] = 1 AND [Body] IS NOT NULL) OR ([Kind] <> 1 AND [Body] IS NULL)");
                    table.CheckConstraint("CK_ConversationMessages_Kind", "[Kind] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_ConversationMessages_LateMinutes", "[LateMinutes] IS NULL OR [LateMinutes] BETWEEN 1 AND 120");
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked', 'booking-on-your-way', 'takeaway-accepted', 'takeaway-ready', 'takeaway-declined', 'venue-message')");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId_CreatedAtUtc_Sequence",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "CreatedAtUtc", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_StaffMemberId",
                table: "ConversationMessages",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "UX_ConversationMessages_ConversationId_Author_ClientCommandId",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "Author", "ClientCommandId" },
                unique: true,
                filter: "[ClientCommandId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BranchId_LastMessageUtc",
                table: "Conversations",
                columns: new[] { "BranchId", "LastMessageUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_DinerUserId_LastMessageUtc",
                table: "Conversations",
                columns: new[] { "DinerUserId", "LastMessageUtc" },
                filter: "[DinerUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Conversations_ReservationId",
                table: "Conversations",
                column: "ReservationId",
                unique: true,
                filter: "[ReservationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Conversations_TakeawayOrderId",
                table: "Conversations",
                column: "TakeawayOrderId",
                unique: true,
                filter: "[TakeawayOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BranchId_EndUtc_Live",
                table: "Reservations",
                columns: new[] { "BranchId", "EndUtc" },
                filter: "[Status] IN (1, 2, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_BranchId_EndUtc_Live",
                table: "Reservations");

            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            // The feed rows and queued pushes of the kind the older schema does not know.
            migrationBuilder.Sql("DELETE FROM dbo.DinerNotifications WHERE [Kind] = 'venue-message';");
            migrationBuilder.Sql("DELETE FROM dbo.OutboxMessages WHERE [Type] = 'message.to-guest';");

            migrationBuilder.DropColumn(
                name: "GuestsCanMessage",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "MessageRemindAfterMinutes",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "MessageTemplatesJson",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "WaitersCanMessage",
                table: "Branches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied', 'booking-changed-by-link', 'booking-cancelled-by-link', 'bookings-blocked-no-shows', 'booking-no-show', 'bookings-unblocked', 'booking-on-your-way', 'takeaway-accepted', 'takeaway-ready', 'takeaway-declined')");
        }
    }
}
