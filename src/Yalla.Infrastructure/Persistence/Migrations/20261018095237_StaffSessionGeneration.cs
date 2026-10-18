using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>StaffMembers.SessionGeneration</c>: the admin-panel session generation every venue-user token now
    /// carries as <c>sgen</c>. Every existing row starts at 0, which is what a token minted before the
    /// claim existed reads as, so those keep working until they expire. A changed or reset password moves
    /// it, which ends every admin-panel token the person holds. Down drops it.
    /// </summary>
    public partial class StaffSessionGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionGeneration",
                table: "StaffMembers",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionGeneration",
                table: "StaffMembers");
        }
    }
}
