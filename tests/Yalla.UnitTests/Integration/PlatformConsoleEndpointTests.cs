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
        Assert.False(listed.TryGetProperty("district", out _), "a branch with no district leaves it out");
        Assert.Equal(1, row.GetProperty("paidBranchCount").GetInt32());
        Assert.Equal(0, row.GetProperty("openTabCount").GetInt32());
        Assert.Equal(0, row.GetProperty("reviewCount").GetInt32());
        Assert.False(row.TryGetProperty("averageRating", out _), "an unrated venue leaves averageRating out");
        Assert.False(row.TryGetProperty("coverThumbnailUrl", out _), "a venue with no cover leaves coverThumbnailUrl out");
        var issue = row.GetProperty("readinessIssue");
        Assert.Equal("no-menu", issue.GetProperty("code").GetString());
        Assert.False(issue.TryGetProperty("count", out _));
        Assert.StartsWith("Test Branch", issue.GetProperty("branchName").GetString(), StringComparison.Ordinal);

        // A blank tier is every tier, as an empty select sends it - not the first tier there is.
        var blankTier = await (await platform.GetAsync($"/api/platform/venues?search={marker}&tier="))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(branch.VenueId, Assert.Single(blankTier.GetProperty("items").EnumerateArray()).GetProperty("venueId").GetGuid());

        var refused = await platform.GetAsync("/api/platform/venues?status=gone");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("status", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
    }

    [SkippableFact]
    public async Task The_admin_renames_themself_and_a_password_change_ends_every_session_but_answers_this_one_a_fresh_pair()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        var (hereToken, hereRefresh) = await SignInAsync(factory, admin.Email, admin.Password);
        var (elsewhereToken, elsewhereRefresh) = await SignInAsync(factory, admin.Email, admin.Password);

        using var here = factory.CreateClientWithToken(hereToken);
        using var elsewhere = factory.CreateClientWithToken(elsewhereToken);

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
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var pair = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["accessToken", "refreshToken", "expiresInSeconds"], pair.EnumerateObject().Select(p => p.Name));

        // Every access token from before is dead now - this one and the other sign-in's - with the
        // answer a client knows to refresh or sign in on.
        foreach (var stale in new[] { here, elsewhere })
        {
            var refused = await stale.GetAsync("/api/platform/me");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("session-revoked", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // The fresh pair carries on.
        using var fresh = factory.CreateClientWithToken(pair.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/platform/me")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = pair.GetProperty("refreshToken").GetString() })).StatusCode);

        // Neither old refresh token mints anything, and the old password no longer signs in.
        foreach (var stale in new[] { hereRefresh, elsewhereRefresh })
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = stale })).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/sign-in", new { email = admin.Email, password = admin.Password })).StatusCode);
        await SignInAsync(factory, admin.Email, newPassword);
    }

    [SkippableFact]
    public async Task Wrong_current_passwords_lock_the_route_with_Retry_After()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await platform.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = $"wrong-{i}-password", newPassword = "a-new-password-long-enough" })).StatusCode);
        }

        var locked = await platform.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = admin.Password, newPassword = "a-new-password-long-enough" });

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 and <= 900 }, $"Retry-After was {locked.Headers.RetryAfter}");

        var problem = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too-many-attempts", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("context").GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task The_password_route_is_under_the_auth_rate_limit()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:GlobalPermitLimit", "10000")
            .With("RateLimiting:AuthPermitLimit", "3")
            .With("RateLimiting:AuthWindowSeconds", "600");
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        // Signing in spends the address's auth budget, the password route the admin's own.
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await platform.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = admin.Password, newPassword = "short" })).StatusCode);
        }

        var limited = await platform.PostAsJsonAsync("/api/platform/me/password", new { currentPassword = admin.Password, newPassword = "short" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // The rest of the console is not on that budget.
        Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync("/api/platform/me")).StatusCode);
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

        // The console's undo: the reports count again.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync($"/api/platform/reviews/{reviewId}/restore-reports", new { })).StatusCode);

        var undone = await platform.PostAsJsonAsync($"/api/platform/reviews/{reviewId}/restore-reports", new { });
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(2, (await undone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reportCount").GetInt32());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await platform.PostAsJsonAsync($"/api/platform/reviews/{Guid.NewGuid()}/restore-reports", new { })).StatusCode);

        // The feed has both, the undo newest, under the admin and the branch.
        var feed = await (await platform.GetAsync($"/api/platform/activity?venueId={branch.VenueId}")).Content.ReadFromJsonAsync<JsonElement>();
        var entries = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["review.restore-reports", "review.dismiss-reports"], entries.Select(e => e.GetProperty("action").GetString()));
        var entry = entries[1];
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
