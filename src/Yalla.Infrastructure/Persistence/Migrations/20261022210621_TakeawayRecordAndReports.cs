using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Takeaway in the guest record and the reports (2026-10-23): the revenue report's index on a branch's collected
    /// takeaway orders by when they were paid, with the amount and method included. No column changes - a takeaway
    /// no-show counts in the guest record and toward the block from the rows already there.
    /// </summary>
    public partial class TakeawayRecordAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TakeawayOrders_BranchId_CollectedAtUtc_Reporting",
                table: "TakeawayOrders",
                columns: new[] { "BranchId", "CollectedAtUtc" },
                filter: "[Status] = 4")
                .Annotation("SqlServer:Include", new[] { "PaidAmountAmd", "PaymentMethod" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TakeawayOrders_BranchId_CollectedAtUtc_Reporting",
                table: "TakeawayOrders");
        }
    }
}
