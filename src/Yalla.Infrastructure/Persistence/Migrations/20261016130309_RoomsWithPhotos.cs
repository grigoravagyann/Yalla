using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Rooms with photos: a room (<c>FloorAreas</c>) gets ordered photos (<c>FloorAreaPhotos</c>), a
    /// table's pin names the room photo it is on (<c>DiningTables.PhotoId</c>), and the drawn floor plan
    /// goes - table geometry and the branch canvas are dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Old pins are cleared, not carried over.</b> Every existing <c>PhotoX</c>/<c>PhotoY</c> was a
    /// place on the branch's cover photo. The cover is not a room photo, and making it one would
    /// invent a room nobody created, so the positions are nulled and the tables stay - unpinned, which
    /// staff still seat and diners are not offered until a manager pins them on a room photo. The
    /// cover itself is untouched.
    /// </para>
    /// <para>
    /// Down puts the columns back with values the old code accepts - a 1000x700 canvas, every table an
    /// 80x80 rectangle in the corner, unrotated - because the drawing was dropped and cannot be
    /// recovered. Pins go too: a place on a room photo means nothing on the cover.
    /// </para>
    /// </remarks>
    public partial class RoomsWithPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Positions on the cover photo; see the remarks. Before PhotoId exists, so no row can be
            // left half pinned.
            migrationBuilder.Sql("UPDATE [DiningTables] SET [PhotoX] = NULL, [PhotoY] = NULL;");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "RotationDegrees",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "Shape",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "X",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "Y",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "FloorHeight",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "FloorWidth",
                table: "Branches");

            migrationBuilder.AddColumn<Guid>(
                name: "PhotoId",
                table: "DiningTables",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FloorAreaPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FloorAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhotoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAreaPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorAreaPhotos_FloorAreas_FloorAreaId",
                        column: x => x.FloorAreaId,
                        principalTable: "FloorAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FloorAreaPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_PhotoId",
                table: "DiningTables",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaPhotos_FloorAreaId_PhotoId",
                table: "FloorAreaPhotos",
                columns: new[] { "FloorAreaId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaPhotos_FloorAreaId_Position",
                table: "FloorAreaPhotos",
                columns: new[] { "FloorAreaId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaPhotos_PhotoId",
                table: "FloorAreaPhotos",
                column: "PhotoId");

            migrationBuilder.AddForeignKey(
                name: "FK_DiningTables_Photos_PhotoId",
                table: "DiningTables",
                column: "PhotoId",
                principalTable: "Photos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A place on a room photo is not a place on the cover.
            migrationBuilder.Sql("UPDATE [DiningTables] SET [PhotoX] = NULL, [PhotoY] = NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_DiningTables_Photos_PhotoId",
                table: "DiningTables");

            migrationBuilder.DropTable(
                name: "FloorAreaPhotos");

            migrationBuilder.DropIndex(
                name: "IX_DiningTables_PhotoId",
                table: "DiningTables");

            migrationBuilder.DropColumn(
                name: "PhotoId",
                table: "DiningTables");

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "DiningTables",
                type: "int",
                nullable: false,
                defaultValue: 80);

            migrationBuilder.AddColumn<double>(
                name: "RotationDegrees",
                table: "DiningTables",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "Shape",
                table: "DiningTables",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "DiningTables",
                type: "int",
                nullable: false,
                defaultValue: 80);

            migrationBuilder.AddColumn<int>(
                name: "X",
                table: "DiningTables",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Y",
                table: "DiningTables",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FloorHeight",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 700);

            migrationBuilder.AddColumn<int>(
                name: "FloorWidth",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 1000);
        }
    }
}
