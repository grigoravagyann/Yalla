using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FavoritesAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DinerFavorites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DinerFavorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DinerFavorites_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DinerNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ParamsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DinerNotifications", x => x.Id);
                    table.CheckConstraint("CK_DinerNotifications_Kind", "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden')");
                    table.ForeignKey(
                        name: "FK_DinerNotifications_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_DinerFavorites_DinerUserId_BranchId",
                table: "DinerFavorites",
                columns: new[] { "DinerUserId", "BranchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DinerNotifications_CreatedAtUtc",
                table: "DinerNotifications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DinerNotifications_DinerUserId_CreatedAtUtc",
                table: "DinerNotifications",
                columns: new[] { "DinerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DinerNotifications_DinerUserId_Unread",
                table: "DinerNotifications",
                columns: new[] { "DinerUserId", "CreatedAtUtc" },
                filter: "[ReadAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DinerNotifications_ReservationId",
                table: "DinerNotifications",
                column: "ReservationId",
                filter: "[ReservationId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DinerFavorites");

            migrationBuilder.DropTable(
                name: "DinerNotifications");
        }
    }
}
