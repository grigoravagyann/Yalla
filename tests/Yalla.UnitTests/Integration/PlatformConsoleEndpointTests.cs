using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform console's new routes through the real pipeline: the policy on the group, the shapes
/// the console binds to, the admin's own account end to end with real tokens, and the review
/// dismissal.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformConsoleEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_console_reads_answer_a_platform_admin_and_refuse_a_venue_manager()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var anonymous = factory.CreateClient();

        string[] routes =
        [
            "/api/platform/overview",
            "/api/platform/activity?limit=5",
            $"/api/platform/venues/{branch.VenueId}/bookings-summary",
            "/api/platform/me",
        ];

        foreach (var route in routes)
        {
            Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        }

        var overview = await (await platform.GetAsync("/api/platform/overview")).Content.ReadFromJsonAsync<JsonElement>();

        foreach (var key in new[] { "generatedAtUtc", "venues", "bookings", "pendingApprovals", "openTabs", "notReady", "recentVenues", "outbox" })
        {
            Assert.True(overview.TryGetProperty(key, out _), $"overview.{key} is missing");
        }

        Assert.Equal(30, overview.GetProperty("bookings").GetProperty("perDay").GetArrayLength());
        Assert.True(overview.GetProperty("outbox").TryGetProperty("deadLetteredLastDay", out _));

        var summary = await (await platform.GetAsync($"/api/platform/venues/{branch.VenueId}/bookings-summary?days=7")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, summary.GetProperty("days").GetInt32());
        Assert.Equal(7, summary.GetProperty("byWeekday").GetArrayLength());

        // No bookings: the average is written as null, not left out.
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("averagePartySize").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/platform/venues/{Guid.NewGuid()}/bookings-summary")).StatusCode);

        var badLimit = await platform.GetAsync("/api/platform/activity?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        Assert.Equal("limit", (await badLimit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
    }

    [SkippableFact]
    public async Task The_venue_list_takes_its_new_filters_and_names_one_it_does_not_know()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;
        TestBranch branch;
        var marker = Guid.NewGuid().ToString("N")[..10];

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Paid);
            await ConsoleTestData.RenameVenueAsync(db, branch.VenueId, $"Sunset {marker}");
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var page = await (await platform.GetAsync($"/api/platform/venues?search={marker}&status=active&tier=paid&sort=rating&dir=desc"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(branch.VenueId, row.GetProperty("venueId").GetGuid());
        Assert.Equal(3, row.GetProperty("tierMix").GetInt32());
        Assert.Equal(1, row.GetProperty("status").GetInt32());
        var listed = Assert.Single(row.GetProperty("branches").EnumerateArray());
        Assert.Equal(branch.BranchId, listed.GetProperty("branchId").GetGuid());
        Assert.Equal(2, listed.GetProperty("subscriptionTier").GetInt32());
        Assert.True(listed.TryGetProperty("name", out _));
        Assert.Equal(1, row.GetProperty("paidBranchCount").GetInt32());
        Assert.Equal(0, row.GetProperty("openTabCount").GetInt32());
        Assert.Equal(0, row.GetProperty("reviewCount").GetInt32());
        Assert.False(row.TryGetProperty("averageRating", out _), "an unrated venue leaves averageRating out");
        Assert.False(row.TryGetProperty("coverThumbnailUrl", out _), "a venue with no cover leaves coverThumbnailUrl out");
        var issue = row.GetProperty("readinessIssue");
        Assert.Equal("no-menu", issue.GetProperty("code").GetString());
        Assert.False(issue.TryGetProperty("count", out _));
        Assert.StartsWith("Test Branch", issue.GetProperty("branchName").GetString(), StringComparison.Ordinal);

        var refused = await platform.GetAsync("/api/platform/venues?status=gone");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("status", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
    }

    [SkippableFact]
    public async Task The_admin_renames_themself_and_changes_the_password_keeping_this_sign_in_and_ending_the_other()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        var (hereToken, hereRefresh) = await SignInAsync(factory, admin.Email, admin.Password);
        var (_, elsewhereRefresh) = await SignInAsync(factory, admin.Email, admin.Password);

        using var here = factory.CreateClientWithToken(hereToken);

        // ---- the name
        var blank = await here.PatchAsJsonAsync("/api/platform/me", new { fullName = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);

        var renamed = await here.PatchAsJsonAsync("/api/platform/me", new { fullName = "Grigor Avagyan" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var me = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Grigor Avagyan", me.GetProperty("fullName").GetString());
        Assert.Equal(admin.Email, me.GetProperty("email").GetString());

        // ---- the password: wrong current one and a short new one, both named, and still signed in.
        var wrong = await here.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = "not-the-password-at-all", newPassword = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);

        var fields = (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("fields")
            .EnumerateArray()
            .Select(f => f.GetProperty("field").GetString())
            .ToList();
        Assert.Equal(["currentPassword", "newPassword"], fields);

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/platform/me")).StatusCode);

        const string newPassword = "a-much-better-password-now";
        var changed = await here.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = admin.Password, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var anonymous = factory.CreateClient();

        // The old password no longer signs in; the new one does.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/sign-in", new { email = admin.Email, password = admin.Password })).StatusCode);
        await SignInAsync(factory, admin.Email, newPassword);

        // This sign-in refreshes and carries on; the other one is signed out.
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = hereRefresh })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = elsewhereRefresh })).StatusCode);
    }

    [SkippableFact]
    public async Task Dismissing_a_reviews_reports_answers_it_unreported_and_is_platform_only()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PlatformAdminAccount admin;
        Guid reviewId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            (reviewId, _) = await ReviewReportDismissalTests.ReportedReviewAsync(db, branch.BranchId, factory.Clock, reports: 2);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync($"/api/platform/reviews/{reviewId}/dismiss-reports", new { })).StatusCode);

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var dismissed = await platform.PostAsJsonAsync($"/api/platform/reviews/{reviewId}/dismiss-reports", new { });
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        var body = await dismissed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reviewId, body.GetProperty("reviewId").GetGuid());
        Assert.Equal(0, body.GetProperty("reportCount").GetInt32());
        Assert.False(body.GetProperty("hidden").GetBoolean());

        // Idempotent.
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"/api/platform/reviews/{reviewId}/dismiss-reports", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await platform.PostAsJsonAsync($"/api/platform/reviews/{Guid.NewGuid()}/dismiss-reports", new { })).StatusCode);

        var reported = await (await platform.GetAsync($"/api/platform/branches/{branch.BranchId}/reviews?filter=reported"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, reported.GetProperty("total").GetInt32());

        // The feed has it, under the admin and the branch.
        var feed = await (await platform.GetAsync($"/api/platform/activity?venueId={branch.VenueId}")).Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(feed.GetProperty("items").EnumerateArray());
        Assert.Equal("review.dismiss-reports", entry.GetProperty("action").GetString());
        Assert.Equal(5, entry.GetProperty("actor").GetProperty("role").GetInt32());
        Assert.Equal(reviewId, entry.GetProperty("target").GetProperty("id").GetGuid());
        Assert.Equal(2, entry.GetProperty("detail").GetProperty("dismissed").GetInt32());
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(YallaApiFactory factory, string email, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/venue/sign-in", new { email, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }
}
