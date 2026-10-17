using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A request for the bill from the diner's pay sheet says how much: <c>ServiceRequests</c> gains
    /// <c>BillScope</c>, <c>BillAmountAmd</c>, <c>TipPercent</c> and <c>TipAmd</c>, priced by the
    /// server when the request is raised, and <c>SupersededAtUtc</c>, stamped when the same diner asks
    /// for the bill again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All five are nullable and every existing row keeps them null, which is what a plain "the bill,
    /// please" still writes. <c>CK_ServiceRequests_Bill</c> holds the four bill columns together: none
    /// at all, or a request for the bill (preset 3) with a scope of 1 or 2 and a positive amount whose
    /// tip is a percentage from 0 to 25 and an amount, or neither - every comparison guarded with
    /// <c>IS NOT NULL</c>, because a CHECK lets an UNKNOWN through. <c>SupersededAtUtc</c> is left out
    /// of it.
    /// </para>
    /// <para>
    /// <c>IX_ServiceRequests_Open</c> is rebuilt with the superseded rows filtered out too, since the
    /// waiter's open list no longer reads them. Down drops the constraint and the columns and restores
    /// the old filter; the amounts are a snapshot of the tab and nothing else reads them.
    /// </para>
    /// </remarks>
    public partial class BillRequestTip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_Open",
                table: "ServiceRequests");

            migrationBuilder.AddColumn<long>(
                name: "BillAmountAmd",
                table: "ServiceRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillScope",
                table: "ServiceRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                table: "ServiceRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TipAmd",
                table: "ServiceRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TipPercent",
                table: "ServiceRequests",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_Open",
                table: "ServiceRequests",
                columns: new[] { "BranchId", "CreatedAtUtc" },
                filter: "[AcknowledgedAtUtc] IS NULL AND [SupersededAtUtc] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Bill",
                table: "ServiceRequests",
                sql: "([BillScope] IS NULL AND [BillAmountAmd] IS NULL AND [TipPercent] IS NULL AND [TipAmd] IS NULL) OR ([Preset] = 3 AND [BillScope] IS NOT NULL AND [BillScope] IN (1, 2) AND [BillAmountAmd] IS NOT NULL AND [BillAmountAmd] > 0 AND (([TipPercent] IS NULL AND [TipAmd] IS NULL) OR ([TipPercent] IS NOT NULL AND [TipAmd] IS NOT NULL AND [TipPercent] BETWEEN 0 AND 25 AND [TipAmd] >= 0)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_Open",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_Bill",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "BillAmountAmd",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "BillScope",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "TipAmd",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "TipPercent",
                table: "ServiceRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_Open",
                table: "ServiceRequests",
                columns: new[] { "BranchId", "CreatedAtUtc" },
                filter: "[AcknowledgedAtUtc] IS NULL");
        }
    }
}
