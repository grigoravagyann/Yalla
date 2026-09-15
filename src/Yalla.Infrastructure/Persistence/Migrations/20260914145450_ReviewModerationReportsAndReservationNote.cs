using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewModerationReportsAndReservationNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BranchReviews_BranchId_UpdatedAtUtc",
                table: "BranchReviews");

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Reservations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenAtUtc",
                table: "BranchReviews",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HiddenByPlatform",
                table: "BranchReviews",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "HiddenByStaffMemberId",
                table: "BranchReviews",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiddenReason",
                table: "BranchReviews",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BranchReviewReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchReviewReports", x => x.Id);
                    table.CheckConstraint("CK_BranchReviewReports_Reason", "[Reason] IN ('spam', 'offensive', 'not-a-visit', 'personal-info', 'other')");
                    table.ForeignKey(
                        name: "FK_BranchReviewReports_BranchReviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "BranchReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BranchReviewReports_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchReviews_BranchId_CreatedAtUtc",
                table: "BranchReviews",
                columns: new[] { "BranchId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchReviewReports_DinerUserId",
                table: "BranchReviewReports",
                column: "DinerUserId");

            migrationBuilder.CreateIndex(
                name: "UX_BranchReviewReports_ReviewId_DinerUserId",
                table: "BranchReviewReports",
                columns: new[] { "ReviewId", "DinerUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchReviewReports");

            migrationBuilder.DropIndex(
                name: "IX_BranchReviews_BranchId_CreatedAtUtc",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "HiddenAtUtc",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "HiddenByPlatform",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "HiddenByStaffMemberId",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "HiddenReason",
                table: "BranchReviews");

            migrationBuilder.CreateIndex(
                name: "IX_BranchReviews_BranchId_UpdatedAtUtc",
                table: "BranchReviews",
                columns: new[] { "BranchId", "UpdatedAtUtc" });
        }
    }
}
