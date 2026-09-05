using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessedCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorType = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ProcessedCommands_ClientCommandId",
                table: "ProcessedCommands",
                column: "ClientCommandId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedCommands");
        }
    }
}
