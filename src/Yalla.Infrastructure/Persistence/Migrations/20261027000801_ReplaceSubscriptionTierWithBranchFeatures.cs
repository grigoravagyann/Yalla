using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The Free/Paid <c>Branches.SubscriptionTier</c> becomes <c>Branches.AllowedFeatures</c>: one bit per
    /// <c>BranchFeature</c> the platform allows at the branch (<c>1 &lt;&lt; (value - 1)</c>). See
    /// <c>BranchFeatures</c> and <c>docs/platform-admin.md</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every existing branch keeps exactly what it had.</b> Free (1) becomes the Basic preset - every feature but
    /// table service, <c>1007</c> - and Paid (2) becomes Full, all ten, <c>1023</c>. Any other value (there is none: the
    /// column was guarded) is read as Free, the safer of the two. The new column is filled before the old one is dropped,
    /// in the one transaction the migration runs in.
    /// </para>
    /// <para>
    /// <b><see cref="Down"/> works over real rows</b>: a branch with table service goes back to Paid, any other to Free.
    /// That loses only what the tier could never say (a branch with, say, reports blocked).
    /// </para>
    /// </remarks>
    public partial class ReplaceSubscriptionTierWithBranchFeatures : Migration
    {
        /// <summary>Every feature: the Full preset, what Paid was.</summary>
        private const int Full = 1023;

        /// <summary>Every feature but table service (bit 16): the Basic preset, what Free was.</summary>
        private const int Basic = 1007;

        /// <summary>Table service's bit.</summary>
        private const int TableService = 16;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A default only so the column can be added to a table with rows; every row is written just below, and the
            // model has no default - every insert states its set.
            migrationBuilder.AddColumn<int>(
                name: "AllowedFeatures",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: Basic);

            migrationBuilder.Sql(
                $"UPDATE dbo.Branches SET AllowedFeatures = CASE WHEN SubscriptionTier = 2 THEN {Full} ELSE {Basic} END;");

            migrationBuilder.DropColumn(
                name: "SubscriptionTier",
                table: "Branches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubscriptionTier",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql(
                $"UPDATE dbo.Branches SET SubscriptionTier = CASE WHEN AllowedFeatures & {TableService} <> 0 THEN 2 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "AllowedFeatures",
                table: "Branches");
        }
    }
}
