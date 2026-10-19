using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LineOnBehalfOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OnBehalfOfParticipantId",
                table: "TabOrderLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_OnBehalfOfParticipantId",
                table: "TabOrderLines",
                column: "OnBehalfOfParticipantId");

            migrationBuilder.AddForeignKey(
                name: "FK_TabOrderLines_TabParticipants_OnBehalfOfParticipantId",
                table: "TabOrderLines",
                column: "OnBehalfOfParticipantId",
                principalTable: "TabParticipants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TabOrderLines_TabParticipants_OnBehalfOfParticipantId",
                table: "TabOrderLines");

            migrationBuilder.DropIndex(
                name: "IX_TabOrderLines_OnBehalfOfParticipantId",
                table: "TabOrderLines");

            migrationBuilder.DropColumn(
                name: "OnBehalfOfParticipantId",
                table: "TabOrderLines");
        }
    }
}
