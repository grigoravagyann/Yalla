using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TabParticipantsByDinerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TabParticipants_UserId",
                table: "TabParticipants",
                column: "UserId")
                .Annotation("SqlServer:Include", new[] { "TabId", "Status", "CanSeeTableTotal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TabParticipants_UserId",
                table: "TabParticipants");
        }
    }
}
