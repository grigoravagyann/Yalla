using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A branch's district - the part of town the diner app prints after the venue type and cuisine,
    /// and that search matches. Nullable with no default: every existing branch reads as "not said"
    /// until somebody at the venue sets it, rather than as a neighbourhood nobody chose.
    /// </summary>
    public partial class BranchDistrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "Branches",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "District",
                table: "Branches");
        }
    }
}
