using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform's feature switches, enforced (<c>docs/branch-features.md</c>): for each feature, a new action at a branch
/// where it is blocked is refused 409 <c>feature-not-enabled</c> naming the feature and the branch, and the work already
/// in flight when it was blocked finishes as it would have. One file per feature beside this one.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed partial class BranchFeatureEnforcementTests(SqlServerFixture fixture)
{
    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>
    /// Every feature but <paramref name="blocked"/> - and without pre-orders once neither booking channel is left, the one
    /// set the platform refuses.
    /// </summary>
    private static List<BranchFeature> AllBut(params BranchFeature[] blocked)
    {
        var allowed = BranchFeatures.Full.Except(blocked).ToList();

        if (!allowed.Contains(BranchFeature.AppBookings) && !allowed.Contains(BranchFeature.WebBookings))
        {
            allowed.Remove(BranchFeature.PreOrders);
        }

        return allowed;
    }

    /// <summary>Blocks features at the branch straight on the row, as the platform's save does - for service-level reads.</summary>
    private static async Task BlockAsync(YallaDbContext db, Guid branchId, params BranchFeature[] blocked)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId);
        branch.SetAllowedFeatures(AllBut(blocked), incompleteMenuItemCount: 0);
        await db.SaveChangesAsync();
    }

    /// <summary>Sets the branch's allowed set to every feature but <paramref name="blocked"/>, on its own context.</summary>
    private async Task BlockAsync(YallaApiFactory factory, Guid branchId, params BranchFeature[] blocked)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        await BlockAsync(db, branchId, blocked);
    }

    /// <summary>
    /// Blocks features through the platform console's own route, as an admin does - which also drops what the token check
    /// cached about the branch's devices, so a staff phone or a kitchen session is refused on its very next call.
    /// </summary>
    private async Task BlockByPlatformAsync(YallaApiFactory factory, Guid branchId, params BranchFeature[] blocked)
    {
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, admin.Email, admin.Password));

        var saved = await platform.PutAsJsonAsync($"/api/platform/branches/{branchId}/features", new { features = AllBut(blocked) });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
    }

    /// <summary>A service's refusal, as the API maps it: 409 <c>feature-not-enabled</c> with the feature and the branch.</summary>
    private static void AssertRefused(Exception refused, BranchFeature feature, Guid branchId)
    {
        var mapped = ApiExceptionMapper.Map(refused);

        Assert.Equal(409, mapped.Status);
        Assert.Equal("feature-not-enabled", mapped.Code);
        Assert.Equal(feature, mapped.Context!["feature"]);
        Assert.Equal(branchId, mapped.Context["branchId"]);
    }

    /// <summary>The same refusal on the wire: the feature as its number, and the branch.</summary>
    private static async Task AssertRefusedAsync(HttpResponseMessage response, BranchFeature feature, Guid branchId)
    {
        var problem = await ReviewIntegrityTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "feature-not-enabled");
        var context = problem.GetProperty("context");

        Assert.Equal((int)feature, context.GetProperty("feature").GetInt32());
        Assert.Equal(branchId, context.GetProperty("branchId").GetGuid());
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");

        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url) => await OkAsync(await client.GetAsync(url));

    /// <summary>A branch whose staff have real credentials, its policy saved and online bookings on, with the test menu.</summary>
    private async Task<(AuthBranch Branch, TestMenu Menu)> ArrangeAsync(YallaApiFactory factory, int tableCount = 3)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db, tableCount: tableCount);
        await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        return (branch, menu);
    }
}
