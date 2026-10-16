using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>StaffRole.GeneralManager = 6</c> and binds the branch to the role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until now a manager (2) whose row named no branch covered the whole venue. That is what a
    /// general manager is, so every such row becomes one first; a manager with a branch stays a
    /// branch manager. Only then is the rule pinned - a branch manager names a branch, a general
    /// manager names none - so no row that existed before this migration can break it.
    /// </para>
    /// <para>
    /// Down reverses both: the constraint goes, and every general manager becomes a branchless
    /// manager again, which is exactly the venue-wide manager the old code understood.
    /// </para>
    /// </remarks>
    public partial class GeneralManagerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [StaffMembers] SET [Role] = 6 WHERE [Role] = 2 AND [BranchId] IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_RoleBranch",
                table: "StaffMembers",
                sql: "([Role] <> 2 OR [BranchId] IS NOT NULL) AND ([Role] <> 6 OR [BranchId] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_RoleBranch",
                table: "StaffMembers");

            migrationBuilder.Sql("UPDATE [StaffMembers] SET [Role] = 2 WHERE [Role] = 6;");
        }
    }
}
