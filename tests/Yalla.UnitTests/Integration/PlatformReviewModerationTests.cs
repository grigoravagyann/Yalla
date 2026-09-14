using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K8 and its extension: the platform and the venue taking reviews down, diners reporting them, and
/// account deletion taking the reports with it.
/// </summary>
/// <remarks>
/// The venue is the one <c>AuthTestData</c> builds - a home branch with its manager, a sibling branch
/// beside it - with two reviews at home and one at the sibling, written straight to the table so the
/// tests are about moderation rather than about who may write them.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformReviewModerationTests(SqlServerFixture fixture)
{
    private const string Password = "khachapuri-2026";

    // ------------------------------------------------------------ the platform

    [SkippableFact]
    public async Task A_platform_admin_takes_a_review_down_and_it_leaves_the_list_the_rating_and_the_count_with_one_audit_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));
        using var anyone = factory.CreateClient();

        var reviewsUrl = $"/api/public/branches/{world.Home.BranchId}/reviews?page=1";
        var before = await anyone.GetFromJsonAsync<JsonElement>(reviewsUrl);
        Assert.Equal(2, before.GetProperty("reviewCount").GetInt32());
        Assert.Equal(3.0, before.GetProperty("rating").GetDouble());

        var hideUrl = $"/api/platform/reviews/{world.HarshReviewId}/visibility";

        // A takedown needs a reason, a short one, and an explicit flag.
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.PutAsJsonAsync(hideUrl, new { hidden = true }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "reason");
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.PutAsJsonAsync(hideUrl, new { hidden = true, reason = new string('x', 501) }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "reason");
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.PutAsJsonAsync(hideUrl, new { reason = "No flag." }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "hidden");
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.PutAsJsonAsync($"/api/platform/reviews/{Guid.CreateVersion7()}/visibility", new { hidden = true, reason = "Gone." }),
            HttpStatusCode.NotFound, "not-found");

        const string reason = "Names a member of staff.";
        var hidden = await platform.PutAsJsonAsync(hideUrl, new { hidden = true, reason });
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);

        var item = await hidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(item.GetProperty("hidden").GetBoolean());
        Assert.True(item.GetProperty("hiddenByPlatform").GetBoolean());
        Assert.Equal(reason, item.GetProperty("hiddenReason").GetString());
        Assert.True(item.TryGetProperty("hiddenAtUtc", out _));
        Assert.Equal("Ani G.", item.GetProperty("authorName").GetString());

        // Gone from the list, the rating, the count and the details screen at once.
        var after = await anyone.GetFromJsonAsync<JsonElement>(reviewsUrl);
        Assert.Equal(1, after.GetProperty("reviewCount").GetInt32());
        Assert.Equal(5.0, after.GetProperty("rating").GetDouble());
        Assert.DoesNotContain(
            world.HarshReviewId, after.GetProperty("reviews").EnumerateArray().Select(r => r.GetProperty("reviewId").GetGuid()));

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}");
        Assert.Equal(1, detail.GetProperty("listing").GetProperty("reviewCount").GetInt32());
        Assert.DoesNotContain(
            world.HarshReviewId, detail.GetProperty("recentReviews").EnumerateArray().Select(r => r.GetProperty("reviewId").GetGuid()));

        // Still on the moderation list, which is the point of hiding rather than deleting.
        var all = await platform.GetFromJsonAsync<JsonElement>($"/api/platform/branches/{world.Home.BranchId}/reviews");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var hiddenOnly = await platform.GetFromJsonAsync<JsonElement>(
            $"/api/platform/branches/{world.Home.BranchId}/reviews?filter=hidden");
        Assert.Equal(world.HarshReviewId, Assert.Single(hiddenOnly.GetProperty("items").EnumerateArray()).GetProperty("reviewId").GetGuid());

        // The same takedown again changes nothing, and so writes no second record.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(hideUrl, new { hidden = true, reason })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var rows = await db.PlatformAuditLogs.AsNoTracking()
                .Where(l => l.TargetId == world.HarshReviewId && l.Action == ReviewModerationService.HideAuditAction)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(world.Admin.StaffMemberId, row.ActorStaffMemberId);
            Assert.Contains("\"actorType\":\"platform\"", row.ChangesJson, StringComparison.Ordinal);
        }

        // And back.
        var shown = await platform.PutAsJsonAsync(hideUrl, new { hidden = false });
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.False((await shown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hidden").GetBoolean());
        Assert.Equal(2, (await anyone.GetFromJsonAsync<JsonElement>(reviewsUrl)).GetProperty("reviewCount").GetInt32());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.PlatformAuditLogs.CountAsync(
                l => l.TargetId == world.HarshReviewId && l.Action == ReviewModerationService.UnhideAuditAction));
        }
    }

    [SkippableFact]
    public async Task Venue_staff_diners_and_anonymous_callers_are_refused_the_platform_routes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));
        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);
        using var anonymous = factory.CreateClient();

        var listUrl = $"/api/platform/branches/{world.Home.BranchId}/reviews";
        var hideUrl = $"/api/platform/reviews/{world.HarshReviewId}/visibility";

        foreach (var (who, client) in new[] { ("manager", manager), ("owner", owner), ("diner", diner) })
        {
            Assert.True((await client.GetAsync(listUrl)).StatusCode == HttpStatusCode.Forbidden, $"the {who} read the platform list.");
            Assert.True(
                (await client.PutAsJsonAsync($"/api/platform/reviews/{world.HarshReviewId}/visibility", new { hidden = true, reason = "Not theirs." })).StatusCode
                == HttpStatusCode.Forbidden,
                $"the {who} used the platform takedown.");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(listUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(hideUrl, new { hidden = true, reason = "x" })).StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.False(await db.BranchReviews.AnyAsync(r => r.Id == world.HarshReviewId && r.HiddenAtUtc != null));
    }

    // ------------------------------------------------------------ the venue

    [SkippableFact]
    public async Task A_manager_moderates_their_own_branch_with_report_counts_and_cannot_undo_a_platform_takedown()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));
        using var first = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);
        using var second = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        // Two diners report the harsh one.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await first.PostAsJsonAsync($"/api/diner/reviews/{world.HarshReviewId}/report", new { reason = "offensive" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await second.PostAsJsonAsync(
                $"/api/diner/reviews/{world.HarshReviewId}/report", new { reason = "personal-info", note = "Names a waiter." })).StatusCode);

        var prefix = $"/api/branches/{world.Home.BranchId}/reviews";

        var reported = await manager.GetFromJsonAsync<JsonElement>($"{prefix}?filter=reported");
        Assert.Equal(1, reported.GetProperty("total").GetInt32());
        var flagged = Assert.Single(reported.GetProperty("items").EnumerateArray());
        Assert.Equal(world.HarshReviewId, flagged.GetProperty("reviewId").GetGuid());
        Assert.Equal(2, flagged.GetProperty("reportCount").GetInt32());
        Assert.True(flagged.TryGetProperty("lastReportedAtUtc", out _));

        // Everything at this branch, and nothing of the sibling's.
        var all = await manager.GetFromJsonAsync<JsonElement>(prefix);
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.DoesNotContain(
            world.SiblingReviewId, all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reviewId").GetGuid()));
        Assert.Equal(
            0,
            all.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("reviewId").GetGuid() == world.KindReviewId)
                .GetProperty("reportCount").GetInt32());

        // The manager takes the kind one down, and puts it back.
        var kindUrl = $"{prefix}/{world.KindReviewId}/visibility";
        var down = await manager.PutAsJsonAsync(kindUrl, new { hidden = true, reason = "Posted twice." });
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.False((await down.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hiddenByPlatform").GetBoolean());
        Assert.Equal(1, (await manager.GetFromJsonAsync<JsonElement>($"{prefix}?filter=hidden")).GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(kindUrl, new { hidden = false })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var hide = Assert.Single(await db.PlatformAuditLogs.AsNoTracking()
                .Where(l => l.TargetId == world.KindReviewId && l.Action == ReviewModerationService.HideAuditAction)
                .ToListAsync());

            Assert.Equal(world.Home.ManagerId, hide.ActorStaffMemberId);
            Assert.Contains("\"actorType\":\"venue\"", hide.ChangesJson, StringComparison.Ordinal);
            Assert.Equal(1, await db.PlatformAuditLogs.CountAsync(
                l => l.TargetId == world.KindReviewId && l.Action == ReviewModerationService.UnhideAuditAction));
        }

        // The platform takes the harsh one down. The venue cannot put it back, and hiding it again
        // leaves it the platform's.
        const string platformReason = "Names a member of staff.";
        var harshUrl = $"{prefix}/{world.HarshReviewId}/visibility";

        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PutAsJsonAsync(
                $"/api/platform/reviews/{world.HarshReviewId}/visibility", new { hidden = true, reason = platformReason })).StatusCode);

        await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(harshUrl, new { hidden = false }), HttpStatusCode.Forbidden, "forbidden");

        var rehidden = await manager.PutAsJsonAsync(harshUrl, new { hidden = true, reason = "Ours too." });
        Assert.Equal(HttpStatusCode.OK, rehidden.StatusCode);
        var rehiddenBody = await rehidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rehiddenBody.GetProperty("hiddenByPlatform").GetBoolean());
        Assert.Equal(platformReason, rehiddenBody.GetProperty("hiddenReason").GetString());

        // And a review the venue took down, the platform can put back.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(kindUrl, new { hidden = true, reason = "Again." })).StatusCode);

        var restored = await platform.PutAsJsonAsync(
            $"/api/platform/reviews/{world.KindReviewId}/visibility", new { hidden = false });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.False((await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hidden").GetBoolean());
    }

    [SkippableFact]
    public async Task A_manager_cannot_moderate_a_sibling_branch_or_another_venue_and_a_review_elsewhere_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        AuthBranch otherVenue;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            otherVenue = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var stranger = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, otherVenue));
        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));

        var home = $"/api/branches/{world.Home.BranchId}/reviews";
        var sibling = $"/api/branches/{world.SiblingBranchId}/reviews";

        // The home-branch manager on the sibling (K4).
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(sibling)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PutAsJsonAsync($"/api/branches/{world.SiblingBranchId}/reviews/{world.SiblingReviewId}/visibility", new { hidden = true, reason = "Not mine." })).StatusCode);

        // Another venue's manager.
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(home)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PutAsJsonAsync($"/api/branches/{world.Home.BranchId}/reviews/{world.HarshReviewId}/visibility", new { hidden = true, reason = "Rival." })).StatusCode);

        // The sibling's review addressed through the manager's own branch is simply not there.
        await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync($"/api/branches/{world.Home.BranchId}/reviews/{world.SiblingReviewId}/visibility", new { hidden = true, reason = "Wrong branch." }),
            HttpStatusCode.NotFound,
            "not-found");

        // The owner covers both.
        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonElement>(sibling)).GetProperty("total").GetInt32());

        // Paging and the filter are checked.
        await ReviewIntegrityTests.AssertProblemAsync(await manager.GetAsync($"{home}?filter=soon"), HttpStatusCode.BadRequest, "invalid-request", "filter");
        await ReviewIntegrityTests.AssertProblemAsync(await manager.GetAsync($"{home}?pageSize=0"), HttpStatusCode.BadRequest, "invalid-request", "pageSize");
        await ReviewIntegrityTests.AssertProblemAsync(await manager.GetAsync($"{home}?page=0"), HttpStatusCode.BadRequest, "invalid-request", "page");

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.BranchReviews.AnyAsync(r => r.HiddenAtUtc != null
            && (r.BranchId == world.Home.BranchId || r.BranchId == world.SiblingBranchId)));
    }

    // ------------------------------------------------------------ reports

    [SkippableFact]
    public async Task A_diner_reports_a_review_once_and_cannot_report_their_own_a_hidden_one_or_one_that_is_not_there()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        var (reporterToken, reporterId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        var (authorToken, authorId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        using var reporter = factory.CreateClientWithToken(reporterToken);
        using var author = factory.CreateClientWithToken(authorToken);
        using var anonymous = factory.CreateClient();

        Guid ownReviewId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            ownReviewId = await ReviewTestData.SeedReviewAsync(db, world.Home.BranchId, authorId, 4, "Mine.", factory.Clock.UtcNow);
        }

        var kindUrl = $"/api/diner/reviews/{world.KindReviewId}/report";

        // Once, and again: one report.
        Assert.Equal(HttpStatusCode.NoContent, (await reporter.PostAsJsonAsync(kindUrl, new { reason = "spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reporter.PostAsJsonAsync(kindUrl, new { reason = "other", note = "Again." })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var report = Assert.Single(await db.BranchReviewReports.AsNoTracking()
                .Where(r => r.ReviewId == world.KindReviewId)
                .ToListAsync());

            Assert.Equal(reporterId, report.DinerUserId);
            Assert.Equal("spam", report.Reason);
        }

        // The body is checked.
        await ReviewIntegrityTests.AssertProblemAsync(
            await reporter.PostAsJsonAsync(kindUrl, new { reason = "rude" }), HttpStatusCode.UnprocessableEntity, "validation-failed", "reason");
        await ReviewIntegrityTests.AssertProblemAsync(
            await reporter.PostAsJsonAsync(kindUrl, new { note = "No reason given." }), HttpStatusCode.UnprocessableEntity, "validation-failed", "reason");
        await ReviewIntegrityTests.AssertProblemAsync(
            await reporter.PostAsJsonAsync(kindUrl, new { reason = "other", note = new string('n', 501) }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "note");

        // Not one's own.
        await ReviewIntegrityTests.AssertProblemAsync(
            await author.PostAsJsonAsync($"/api/diner/reviews/{ownReviewId}/report", new { reason = "spam" }),
            HttpStatusCode.Conflict, "conflicting-state");

        // A hidden review is not there to report, and neither is one that never was.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var harsh = await db.BranchReviews.SingleAsync(r => r.Id == world.HarshReviewId);
            harsh.Hide(world.Admin.StaffMemberId, byPlatform: true, "Already down.", factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        await ReviewIntegrityTests.AssertProblemAsync(
            await reporter.PostAsJsonAsync($"/api/diner/reviews/{world.HarshReviewId}/report", new { reason = "spam" }),
            HttpStatusCode.NotFound, "not-found");
        await ReviewIntegrityTests.AssertProblemAsync(
            await reporter.PostAsJsonAsync($"/api/diner/reviews/{Guid.CreateVersion7()}/report", new { reason = "spam" }),
            HttpStatusCode.NotFound, "not-found");

        // Any diner account may report - a number that was never proved included.
        var (unprovedToken, _) = await RegisterAsync(anonymous);
        using var unproved = factory.CreateClientWithToken(unprovedToken);
        Assert.Equal(HttpStatusCode.NoContent, (await unproved.PostAsJsonAsync(kindUrl, new { reason = "not-a-visit" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(kindUrl, new { reason = "spam" })).StatusCode);
    }

    [SkippableFact]
    public async Task Deleting_an_account_removes_the_reports_it_filed_and_the_reports_about_its_reviews()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var (aToken, aId) = await RegisterAsync(anonymous);
        var (bToken, bId) = await RegisterAsync(anonymous);
        var (cToken, cId) = await RegisterAsync(anonymous);
        using var a = factory.CreateClientWithToken(aToken);
        using var b = factory.CreateClientWithToken(bToken);
        using var c = factory.CreateClientWithToken(cToken);

        Guid bReviewId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            bReviewId = await ReviewTestData.SeedReviewAsync(db, world.Home.BranchId, bId, 2, "Slow.", factory.Clock.UtcNow);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync($"/api/diner/reviews/{world.KindReviewId}/report", new { reason = "spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync($"/api/diner/reviews/{bReviewId}/report", new { reason = "offensive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/diner/reviews/{bReviewId}/report", new { reason = "not-a-visit" })).StatusCode);

        // A goes: every report A filed goes with them; C's report of B's review stays.
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccountAsync(a)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.BranchReviewReports.AnyAsync(r => r.DinerUserId == aId));
            Assert.Equal(cId, (await db.BranchReviewReports.AsNoTracking().SingleAsync(r => r.ReviewId == bReviewId)).DinerUserId);
        }

        // B goes: their review, and the report about it that somebody else filed.
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccountAsync(b)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.BranchReviews.AnyAsync(r => r.Id == bReviewId));
            Assert.False(await db.BranchReviewReports.AnyAsync(r => r.ReviewId == bReviewId));

            var audit = await db.PlatformAuditLogs.AsNoTracking()
                .SingleAsync(l => l.Action == DinerAccountDeletion.AuditAction && l.TargetId == bId);
            Assert.Contains("\"reviewReports\":1", audit.ChangesJson, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        AuthBranch Home,
        Guid SiblingBranchId,
        PanelAccount Owner,
        PlatformAdminAccount Admin,
        Guid KindReviewId,
        Guid HarshReviewId,
        Guid SiblingReviewId);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var now = factory.Clock.UtcNow;

        // The fixture manager's row names the home branch.
        var home = await AuthTestData.CreateBranchAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);

        var kind = await ReviewTestData.SeedReviewAsync(
            db, home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Narek Petrosyan", now), 5, "Lovely.", now.AddMinutes(-10));
        var harsh = await ReviewTestData.SeedReviewAsync(
            db, home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Ani Grigoryan", now), 1, "Awful.", now.AddMinutes(-5));
        var elsewhere = await ReviewTestData.SeedReviewAsync(
            db, sibling, await ReviewTestData.SeedDinerAsync(db, "Mariam Hakobyan", now), 4, null, now.AddMinutes(-3));

        return new World(
            home,
            sibling,
            await AuthTestData.SeedOwnerAsync(db, home.VenueId),
            await AuthTestData.CreatePlatformAdminAsync(db),
            kind,
            harsh,
            elsewhere);
    }

    /// <summary>A password account, registered through the real endpoint, whose number is not proved.</summary>
    private static async Task<(string AccessToken, Guid DinerUserId)> RegisterAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var registered = await client.PostAsJsonAsync(
            "/api/auth/diner/register",
            new
            {
                username = $"ani_{suffix}",
                email = $"ani-{suffix}@example.test",
                password = Password,
                phoneE164 = ReviewTestData.NextPhone(),
                displayName = "Ani",
            });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid());
    }

    /// <summary>The body travels on a DELETE, which <c>HttpClient.DeleteAsync</c> cannot send.</summary>
    private static Task<HttpResponseMessage> DeleteAccountAsync(HttpClient client) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me")
        {
            Content = JsonContent.Create(new { password = Password }),
        });
}
