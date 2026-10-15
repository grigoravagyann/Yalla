using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NonceHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    ChatId = table.Column<long>(type: "bigint", nullable: true),
                    PhoneE164 = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LocaleCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedFromAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CallerDinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramVerifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramVerifications_TelegramUserId_Status_ExpiresAtUtc",
                table: "TelegramVerifications",
                columns: new[] { "TelegramUserId", "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TelegramVerifications_NonceHash",
                table: "TelegramVerifications",
                column: "NonceHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramVerifications");
        }
    }
}
