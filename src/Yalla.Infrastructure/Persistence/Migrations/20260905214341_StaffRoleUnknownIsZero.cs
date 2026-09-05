using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Moves <c>PlatformAdmin</c> off zero, and gives every staff device the identifier its client
    /// generated for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The role remap is the reason this migration exists.</b> <c>PlatformAdmin</c> was 0, which
    /// is what an unset field, a deserialisation default and any insert path that forgot to set a
    /// role all produce - so the most privileged value in the system was also the one you got by
    /// accident. <c>Unknown = 0</c> now occupies that slot and grants nothing.
    /// </para>
    /// <para>
    /// The remap is a <b>single CASE</b> and not a sequence of updates. Two statements - "set 0 to 5"
    /// then "set 4 to 0" - would cascade: the second would catch the rows the first had just moved.
    /// One pass, every row evaluated against the values it had when the statement began.
    /// </para>
    /// </remarks>
    public partial class StaffRoleUnknownIsZero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable first. The unique index below is filtered to live devices, so backfilling
            // every existing row with the same empty string would collide the moment a branch has
            // two of them - which every branch that has been set up does.
            migrationBuilder.AddColumn<string>(
                name: "ClientDeviceId",
                table: "StaffDevices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // Devices enrolled before clients sent an identifier get one derived from their own row,
            // so they stay distinguishable and the index holds. A browser that re-enrols will send
            // its own and get a fresh row.
            migrationBuilder.Sql(
                "UPDATE [StaffDevices] SET [ClientDeviceId] = CONCAT('legacy-', LOWER(CONVERT(nvarchar(36), [Id]))) "
                + "WHERE [ClientDeviceId] IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "ClientDeviceId",
                table: "StaffDevices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_StaffDevices_BranchId_ClientDeviceId_Live",
                table: "StaffDevices",
                columns: new[] { "BranchId", "ClientDeviceId" },
                unique: true,
                filter: "[RevokedAtUtc] IS NULL");

            // The remap. One statement, every row evaluated against its pre-statement value.
            migrationBuilder.Sql(
                @"UPDATE [StaffMembers]
                  SET [Role] = CASE [Role]
                                   WHEN 0 THEN 5   -- PlatformAdmin, off the default slot
                                   ELSE [Role]     -- Owner 1, Manager 2, Waiter 3, Kitchen 4 unchanged
                               END
                  WHERE [Role] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Puts PlatformAdmin back on zero. Reversible, and a reversal nobody should want.
            migrationBuilder.Sql(
                @"UPDATE [StaffMembers]
                  SET [Role] = CASE [Role]
                                   WHEN 5 THEN 0
                                   ELSE [Role]
                               END
                  WHERE [Role] = 5;");

            migrationBuilder.DropIndex(
                name: "UX_StaffDevices_BranchId_ClientDeviceId_Live",
                table: "StaffDevices");

            migrationBuilder.DropColumn(
                name: "ClientDeviceId",
                table: "StaffDevices");
        }
    }
}
