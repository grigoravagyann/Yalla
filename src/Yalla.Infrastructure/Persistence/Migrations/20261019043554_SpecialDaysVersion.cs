using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>Branches.SpecialDaysVersion</c>: the special opening days' revision, which their whole-list save checks
    /// against the one the editor loaded, so a second editor cannot silently undo the first one's holiday.
    /// </summary>
    /// <remarks>Every branch starts at 0. Down drops the column.</remarks>
    public partial class SpecialDaysVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpecialDaysVersion",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecialDaysVersion",
                table: "Branches");
        }
    }
}
