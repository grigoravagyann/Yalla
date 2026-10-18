using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>Venues.ActiveBeforeDeletion</c>: whether a venue was switched on when it was soft-deleted, which
    /// is what restoring it puts back. Null for every live venue; null too for a venue deleted before this
    /// migration, which restores switched on - the state nearly every deleted venue was in. Down drops it.
    /// </summary>
    public partial class VenueRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActiveBeforeDeletion",
                table: "Venues",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveBeforeDeletion",
                table: "Venues");
        }
    }
}
