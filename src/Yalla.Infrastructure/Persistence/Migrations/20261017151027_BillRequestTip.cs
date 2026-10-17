using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A request for the bill from the diner's pay sheet says how much: <c>ServiceRequests</c> gains
    /// <c>BillScope</c>, <c>BillAmountAmd</c>, <c>TipPercent</c> and <c>TipAmd</c>, priced by the
    /// server when the request is raised.
    /// </summary>
    /// <remarks>
    /// All four are nullable and every existing row keeps them null, which is what a plain "the bill,
    /// please" still writes. <c>CK_ServiceRequests_Bill</c> holds them together: none at all, or a
    /// request for the bill (preset 3) with a scope and a positive amount whose tip is a percentage
    /// and an amount or neither. Down drops the constraint and the columns; the amounts are a
    /// snapshot of the tab and nothing else reads them.
    /// </remarks>
    public partial class BillRequestTip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Bill",
                table: "ServiceRequests",
                sql: "([BillScope] IS NULL AND [BillAmountAmd] IS NULL AND [TipPercent] IS NULL AND [TipAmd] IS NULL) OR ([Preset] = 3 AND [BillScope] IS NOT NULL AND [BillAmountAmd] > 0 AND (([TipPercent] IS NULL AND [TipAmd] IS NULL) OR ([TipPercent] >= 0 AND [TipAmd] >= 0)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "TipAmd",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "TipPercent",
                table: "ServiceRequests");
        }
    }
}
