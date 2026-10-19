using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The venue's public reply to a review, as six nullable columns on <c>BranchReviews</c> - the text,
    /// the signature as written (name and role), who wrote it last, and when it was first published and
    /// last changed - held whole or absent, and signed by an owner, branch manager or general manager, by
    /// <c>CK_BranchReviews_Reply</c>; and the feed kind <c>review-replied</c> admitted by
    /// <c>CK_DinerNotifications_Kind</c>.
    /// </summary>
    /// <remarks>
    /// Every existing review starts with no reply. Down drops the columns and deletes the
    /// <c>review-replied</c> feed entries before putting the narrower check back, which they would break.
    /// </remarks>
    public partial class ReviewReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.AddColumn<DateTime>(
                name: "RepliedAtUtc",
                table: "BranchReviews",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyAuthorName",
                table: "BranchReviews",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplyAuthorRole",
                table: "BranchReviews",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyByStaffMemberId",
                table: "BranchReviews",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyText",
                table: "BranchReviews",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReplyUpdatedAtUtc",
                table: "BranchReviews",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BranchReviews_Reply",
                table: "BranchReviews",
                sql: "([ReplyText] IS NULL AND [ReplyAuthorName] IS NULL AND [ReplyAuthorRole] IS NULL AND [ReplyByStaffMemberId] IS NULL AND [RepliedAtUtc] IS NULL AND [ReplyUpdatedAtUtc] IS NULL) OR ([ReplyText] IS NOT NULL AND [ReplyAuthorName] IS NOT NULL AND [ReplyAuthorRole] IN (1, 2, 6) AND [ReplyByStaffMemberId] IS NOT NULL AND [RepliedAtUtc] IS NOT NULL AND [ReplyUpdatedAtUtc] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided', 'review-replied')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BranchReviews_Reply",
                table: "BranchReviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications");

            migrationBuilder.DropColumn(
                name: "RepliedAtUtc",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "ReplyAuthorName",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "ReplyAuthorRole",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "ReplyByStaffMemberId",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "ReplyText",
                table: "BranchReviews");

            migrationBuilder.DropColumn(
                name: "ReplyUpdatedAtUtc",
                table: "BranchReviews");

            migrationBuilder.Sql("DELETE FROM [DinerNotifications] WHERE [Kind] = N'review-replied';");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DinerNotifications_Kind",
                table: "DinerNotifications",
                sql: "[Kind] IN ('booking-reminder', 'booking-confirmed', 'booking-declined', 'booking-cancelled-by-venue', 'order-ready', 'review-hidden', 'table-request', 'table-request-decided')");
        }
    }
}
