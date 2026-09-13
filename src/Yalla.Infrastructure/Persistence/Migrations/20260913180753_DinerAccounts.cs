using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Diner accounts: a username, an email, a password and a profile picture beside the phone
    /// number, and a photo that can belong to a diner instead of a branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every new column on <c>DinerUsers</c> is nullable, because every existing row is an account
    /// the code flow created and none of them has any of these. The one that is not simply left
    /// empty is <c>PhoneVerifiedAtUtc</c>, backfilled below.
    /// </para>
    /// <para>
    /// <c>Photos.BranchId</c> becomes nullable so a picture can belong to a diner; the check
    /// constraint says exactly one owner, and the branch uniqueness index gains a filter so two
    /// diners' copies of one picture do not collide on <c>(NULL, hash)</c> - SQL Server treats two
    /// NULLs as equal in a unique index. Nothing about a branch's photos changes: every existing
    /// row keeps its branch and satisfies the new constraint as it stands.
    /// </para>
    /// </remarks>
    public partial class DinerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Photos_BranchId_ContentHash",
                table: "Photos");

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "Photos",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "DinerUserId",
                table: "Photos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "DinerUsers",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "DinerUsers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneVerifiedAtUtc",
                table: "DinerUsers",
                type: "datetime2",
                nullable: true);

            // Every row that exists today was created by a one-time code coming back from its
            // number - that was the only way in - so every one of them is verified, and leaving
            // the column null would tell each of those diners their number is unproved and put a
            // "verify" link on a profile that never needed one. The stamp is the last sign-in,
            // which is set in the same save that creates the row and is therefore never null in
            // practice; CreatedAtUtc is the fallback for a row that somehow has none, because the
            // row's existence is the proof either way.
            migrationBuilder.Sql(
                """
                UPDATE [DinerUsers]
                SET [PhoneVerifiedAtUtc] = COALESCE([LastSignInAtUtc], [CreatedAtUtc])
                WHERE [PhoneVerifiedAtUtc] IS NULL;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "PhotoId",
                table: "DinerUsers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "DinerUsers",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Photos_BranchId_ContentHash",
                table: "Photos",
                columns: new[] { "BranchId", "ContentHash" },
                unique: true,
                filter: "[BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Photos_DinerUserId_ContentHash",
                table: "Photos",
                columns: new[] { "DinerUserId", "ContentHash" },
                unique: true,
                filter: "[DinerUserId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Photos_OneOwner",
                table: "Photos",
                sql: "CASE WHEN [BranchId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [DinerUserId] IS NULL THEN 0 ELSE 1 END = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DinerUsers_PhotoId",
                table: "DinerUsers",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "UX_DinerUsers_Email",
                table: "DinerUsers",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_DinerUsers_Username",
                table: "DinerUsers",
                column: "Username",
                unique: true,
                filter: "[Username] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_DinerUsers_Photos_PhotoId",
                table: "DinerUsers",
                column: "PhotoId",
                principalTable: "Photos",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Photos_DinerUsers_DinerUserId",
                table: "Photos",
                column: "DinerUserId",
                principalTable: "DinerUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DinerUsers_Photos_PhotoId",
                table: "DinerUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Photos_DinerUsers_DinerUserId",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "UX_Photos_BranchId_ContentHash",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "UX_Photos_DinerUserId_ContentHash",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Photos_OneOwner",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "IX_DinerUsers_PhotoId",
                table: "DinerUsers");

            migrationBuilder.DropIndex(
                name: "UX_DinerUsers_Email",
                table: "DinerUsers");

            migrationBuilder.DropIndex(
                name: "UX_DinerUsers_Username",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "DinerUserId",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAtUtc",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "PhotoId",
                table: "DinerUsers");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "DinerUsers");

            // A diner's picture has no branch, and the old schema cannot hold a photo without one.
            // EF's scaffold would have stamped every such row with an empty Guid - a branch that
            // does not exist - and left the foreign key to it failing. Delete them instead: going
            // back means the profile picture feature is gone, and a row for a picture nothing can
            // show is the leak the sweep exists to prevent. The files are left on disk; a rollback
            // is not the moment to be deleting uploads, and the sweep cannot see them once the
            // rows are gone, so this is the one case where that is stated and accepted.
            migrationBuilder.Sql(
                """
                DELETE FROM [Photos] WHERE [BranchId] IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "Photos",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Photos_BranchId_ContentHash",
                table: "Photos",
                columns: new[] { "BranchId", "ContentHash" },
                unique: true);
        }
    }
}
