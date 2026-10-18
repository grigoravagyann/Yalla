using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The platform console's activity feed and review-report dismissal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PlatformAuditLogs</c> gains the names the feed reads a row by - the actor's role and name, the
    /// target's name - and the venue the row concerns, all nullable. Rows already in the log are filled
    /// from what their ids name today: the nearest thing to "as it was then" that is left for them, and
    /// better than a feed of bare ids. A row whose actor is not a staff member (the diner deleting their
    /// own account writes the diner's id) keeps a null role and name, which is how the feed leaves it out.
    /// A review's row is named by its branch, never its author.
    /// </para>
    /// <para>
    /// <c>BranchReviewReports</c> gains <c>DismissedAtUtc</c> and <c>DismissedByStaffMemberId</c>. Nothing
    /// existing is dismissed.
    /// </para>
    /// <para>
    /// Down drops the columns and indexes; the copied names go with them.
    /// </para>
    /// </remarks>
    public partial class PlatformActivityAndReportDismissal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorDisplay",
                table: "PlatformAuditLogs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActorRole",
                table: "PlatformAuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDisplay",
                table: "PlatformAuditLogs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VenueId",
                table: "PlatformAuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DismissedAtUtc",
                table: "BranchReviewReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DismissedByStaffMemberId",
                table: "BranchReviewReports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_AtUtc",
                table: "PlatformAuditLogs",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_VenueId_AtUtc",
                table: "PlatformAuditLogs",
                columns: new[] { "VenueId", "AtUtc" });

            // Who: every row whose actor is a staff member. The diner's own account deletion matches
            // no staff row and keeps nulls.
            migrationBuilder.Sql(
                """
                UPDATE l SET l.[ActorRole] = s.[Role], l.[ActorDisplay] = LEFT(s.[FullName], 200)
                FROM [PlatformAuditLogs] l
                JOIN [StaffMembers] s ON s.[Id] = l.[ActorStaffMemberId];
                """);

            // What, and which venue - one statement per target type the log has ever written.
            migrationBuilder.Sql(
                """
                UPDATE l SET l.[TargetDisplay] = LEFT(v.[Name], 200), l.[VenueId] = v.[Id]
                FROM [PlatformAuditLogs] l
                JOIN [Venues] v ON v.[Id] = l.[TargetId]
                WHERE l.[TargetType] = N'Venue';

                UPDATE l SET l.[TargetDisplay] = LEFT(b.[Name], 200), l.[VenueId] = b.[VenueId]
                FROM [PlatformAuditLogs] l
                JOIN [Branches] b ON b.[Id] = l.[TargetId]
                WHERE l.[TargetType] = N'Branch';

                UPDATE l SET l.[TargetDisplay] = LEFT(s.[FullName], 200), l.[VenueId] = s.[VenueId]
                FROM [PlatformAuditLogs] l
                JOIN [StaffMembers] s ON s.[Id] = l.[TargetId]
                WHERE l.[TargetType] = N'StaffMember';

                UPDATE l SET l.[TargetDisplay] = t.[Label], l.[VenueId] = b.[VenueId]
                FROM [PlatformAuditLogs] l
                JOIN [DiningTables] t ON t.[Id] = l.[TargetId]
                JOIN [Branches] b ON b.[Id] = t.[BranchId]
                WHERE l.[TargetType] = N'DiningTable';

                UPDATE l SET l.[TargetDisplay] = LEFT(b.[Name], 200), l.[VenueId] = b.[VenueId]
                FROM [PlatformAuditLogs] l
                JOIN [BranchReviews] r ON r.[Id] = l.[TargetId]
                JOIN [Branches] b ON b.[Id] = r.[BranchId]
                WHERE l.[TargetType] = N'BranchReview';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformAuditLogs_AtUtc",
                table: "PlatformAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_PlatformAuditLogs_VenueId_AtUtc",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorDisplay",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "TargetDisplay",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "VenueId",
                table: "PlatformAuditLogs");

            migrationBuilder.DropColumn(
                name: "DismissedAtUtc",
                table: "BranchReviewReports");

            migrationBuilder.DropColumn(
                name: "DismissedByStaffMemberId",
                table: "BranchReviewReports");
        }
    }
}
