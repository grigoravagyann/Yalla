using System.Text.Json;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// Reports: blocked, every report - the JSON and the CSV - is refused at the branch; a venue rollup asked at a sibling
    /// that has them leaves the blocked branch out rather than refusing.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_reports_are_refused_with_their_csv_and_left_out_of_a_venue_rollup()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        Guid sibling;
        PanelAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", "Second");
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var today = DateOnly.FromDateTime(factory.Clock.UtcNow.AddHours(4));
        var window = $"from={today.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        var before = await ReadAsync(ownerClient, $"/api/branches/{sibling}/reports/occupancy?{window}&rollUpVenue=true");

        await BlockAsync(factory, branch.BranchId, BranchFeature.Reports);

        foreach (var report in new[] { "occupancy", "reservations", "revenue", "menu", "staff" })
        {
            await AssertRefusedAsync(
                await manager.GetAsync($"/api/branches/{branch.BranchId}/reports/{report}?{window}"), BranchFeature.Reports, branch.BranchId);
        }

        await AssertRefusedAsync(
            await manager.GetAsync($"/api/branches/{branch.BranchId}/reports/revenue?{window}&format=csv"), BranchFeature.Reports, branch.BranchId);

        // Asked at the sibling, the rollup runs - over the sibling alone.
        var after = await ReadAsync(ownerClient, $"/api/branches/{sibling}/reports/occupancy?{window}&rollUpVenue=true");
        Assert.Equal([branch.BranchId, sibling], Branches(before).Order());
        Assert.Equal([sibling], Branches(after));

        static IEnumerable<Guid> Branches(JsonElement report) =>
            report.GetProperty("scope").GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).ToList();
    }
}
