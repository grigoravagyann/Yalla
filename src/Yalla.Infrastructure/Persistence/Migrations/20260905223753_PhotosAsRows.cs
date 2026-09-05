using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhotosAsRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PhotoId arrives nullable and PhotoUrl stays, so the backfill below has something to
            // read. EF scaffolded this the other way round - drop the column, then add a non-null
            // one defaulted to Guid.Empty - which would have silently blanked the photo off every
            // menu item that had one and left a foreign key pointing at a row that does not exist.
            migrationBuilder.AddColumn<Guid>(
                name: "PhotoId",
                table: "MenuItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CoverPhotoId",
                table: "Branches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Photos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ThumbnailPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CardPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    FullPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    BytesStored = table.Column<long>(type: "bigint", nullable: false),
                    IsExternallyHosted = table.Column<bool>(type: "bit", nullable: false),
                    UploadedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Photos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Photos_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_PhotoId",
                table: "MenuItems",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CoverPhotoId",
                table: "Branches",
                column: "CoverPhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_UploadedAtUtc",
                table: "Photos",
                column: "UploadedAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_Photos_BranchId_ContentHash",
                table: "Photos",
                columns: new[] { "BranchId", "ContentHash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Branches_Photos_CoverPhotoId",
                table: "Branches",
                column: "CoverPhotoId",
                principalTable: "Photos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ---------------------------------------------------------------- carry the URLs across
            //
            // One Photo row per distinct (branch, URL). Marked IsExternallyHosted, because these
            // images live wherever they always did: this system has never seen their bytes, so there
            // is nothing to strip, no variants to generate and nothing to stream. The read model
            // hands the URL to the client unchanged.
            //
            // The hash is of the URL rather than of any bytes, which keeps the unique index
            // meaningful: two menu items pointing at the same picture share one row, exactly as two
            // identical uploads would.
            migrationBuilder.Sql(
                @"INSERT INTO [Photos]
                      (Id, BranchId, ContentHash, ThumbnailPath, CardPath, FullPath,
                       Width, Height, BytesStored, IsExternallyHosted, UploadedByStaffId,
                       UploadedAtUtc, CreatedAtUtc)
                  SELECT NEWID(),
                         src.BranchId,
                         CONVERT(varchar(64), HASHBYTES('SHA2_256', src.PhotoUrl), 2),
                         src.PhotoUrl, src.PhotoUrl, src.PhotoUrl,
                         NULL, NULL, 0, 1, NULL,
                         SYSUTCDATETIME(), SYSUTCDATETIME()
                  FROM (
                      SELECT DISTINCT c.BranchId, i.PhotoUrl
                      FROM [MenuItems] i
                      JOIN [MenuCategories] c ON c.Id = i.MenuCategoryId
                      WHERE i.PhotoUrl IS NOT NULL AND LEN(i.PhotoUrl) > 0
                  ) src;");

            migrationBuilder.Sql(
                @"UPDATE i
                  SET i.PhotoId = p.Id
                  FROM [MenuItems] i
                  JOIN [MenuCategories] c ON c.Id = i.MenuCategoryId
                  JOIN [Photos] p ON p.BranchId = c.BranchId AND p.FullPath = i.PhotoUrl;");

            // Anything still unmatched had no URL at all, which the old schema forbade. Nothing to
            // point at, so the column cannot be tightened while it is there - fail loudly rather
            // than inventing a photo.
            migrationBuilder.Sql(
                @"IF EXISTS (SELECT 1 FROM [MenuItems] WHERE PhotoId IS NULL)
                      THROW 50000, 'Menu items exist with no photo URL to migrate. Give them a photo before applying this migration.', 1;");

            migrationBuilder.AlterColumn<Guid>(
                name: "PhotoId",
                table: "MenuItems",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                table: "MenuItems");

            migrationBuilder.AddForeignKey(
                name: "FK_MenuItems_Photos_PhotoId",
                table: "MenuItems",
                column: "PhotoId",
                principalTable: "Photos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Branches_Photos_CoverPhotoId",
                table: "Branches");

            migrationBuilder.Sql(
                @"IF COL_LENGTH('MenuItems', 'PhotoUrl') IS NULL
                      ALTER TABLE [MenuItems] ADD [PhotoUrl] nvarchar(2048) NOT NULL DEFAULT '';");

            migrationBuilder.Sql(
                @"UPDATE i SET i.PhotoUrl = p.FullPath
                  FROM [MenuItems] i JOIN [Photos] p ON p.Id = i.PhotoId;");

            migrationBuilder.DropForeignKey(
                name: "FK_MenuItems_Photos_PhotoId",
                table: "MenuItems");

            migrationBuilder.DropTable(
                name: "Photos");

            migrationBuilder.DropIndex(
                name: "IX_MenuItems_PhotoId",
                table: "MenuItems");

            migrationBuilder.DropIndex(
                name: "IX_Branches_CoverPhotoId",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "PhotoId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "CoverPhotoId",
                table: "Branches");

            // The column was recreated and repopulated at the top of Down, before PhotoId was
            // dropped - by this point there is nothing left to read it from.
        }
    }
}
