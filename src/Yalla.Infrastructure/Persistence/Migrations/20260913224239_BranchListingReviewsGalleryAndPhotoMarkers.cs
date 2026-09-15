using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BranchListingReviewsGalleryAndPhotoMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PhotoX",
                table: "DiningTables",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PhotoY",
                table: "DiningTables",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "About",
                table: "Branches",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmenityKeys",
                table: "Branches",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cuisine",
                table: "Branches",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceLevel",
                table: "Branches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteUrl",
                table: "Branches",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BranchGalleryPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhotoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchGalleryPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchGalleryPhotos_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BranchGalleryPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BranchReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchReviews", x => x.Id);
                    table.CheckConstraint("CK_BranchReviews_Rating", "[Rating] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_BranchReviews_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchReviews_DinerUsers_DinerUserId",
                        column: x => x.DinerUserId,
                        principalTable: "DinerUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchGalleryPhotos_BranchId_PhotoId",
                table: "BranchGalleryPhotos",
                columns: new[] { "BranchId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BranchGalleryPhotos_BranchId_Position",
                table: "BranchGalleryPhotos",
                columns: new[] { "BranchId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchGalleryPhotos_PhotoId",
                table: "BranchGalleryPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchReviews_BranchId_UpdatedAtUtc",
                table: "BranchReviews",
                columns: new[] { "BranchId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchReviews_DinerUserId",
                table: "BranchReviews",
                column: "DinerUserId");

            migrationBuilder.CreateIndex(
                name: "UX_BranchReviews_BranchId_DinerUserId",
                table: "BranchReviews",
                columns: new[] { "BranchId", "DinerUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchGalleryPhotos");

            migrationBuilder.DropTable(
                name: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "PhotoX",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "PhotoY",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "About",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "AmenityKeys",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "Cuisine",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "PriceLevel",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "WebsiteUrl",
                table: "Branches");
        }
    }
}
