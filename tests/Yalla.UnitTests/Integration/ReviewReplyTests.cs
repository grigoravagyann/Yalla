using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue's public reply to a review (venue console phase 4): one per review, written and edited by
/// the owner, the general manager or the branch's manager, shown wherever the review is.
/// </summary>
/// <remarks>
/// The venue is the one <c>AuthTestData</c> builds - a home branch with its manager - plus the venue's
/// owner and general manager. The review being answered is written straight to the table by a diner who
/// is signed in, so the same test can read it back through the diner's own route.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReviewReplyTests(SqlServerFixture fixture)
{
    private const string Thanks = "Thank you - Friday evenings now have two more waiters.";

    // ------------------------------------------------------------ writing, editing, removing

    [SkippableFact]
    public async Task An_owner_replies_a_general_manager_edits_and_every_read_of_the_review_shows_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));
        using var gm = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.GeneralManager));
        using var author = factory.CreateClientWithToken(world.AuthorToken);
        using var anyone = factory.CreateClient();

        var replyUrl = ReplyUrl(world.Home.BranchId, world.ReviewId);

        var written = await owner.PutAsJsonAsync(replyUrl, new { text = "  " + Thanks + "  " });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var view = await written.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(world.ReviewId, view.GetProperty("reviewId").GetGuid());

        var reply = view.GetProperty("reply");
        Assert.Equal(Thanks, reply.GetProperty("text").GetString());
        Assert.Equal("Founder", reply.GetProperty("authorName").GetString());
        Assert.Equal(1, reply.GetProperty("authorRole").GetInt32());
        Assert.False(reply.GetProperty("edited").GetBoolean());
        var firstWritten = reply.GetProperty("createdAtUtc").GetDateTime();
        Assert.Equal(firstWritten, reply.GetProperty("updatedAtUtc").GetDateTime());

        // The console list, the public page, the place's details and the author's own review.
        Assert.Equal(Thanks, (await ConsoleReplyAsync(owner, world)).GetProperty("text").GetString());
        AssertAbsent(await ConsoleRowAsync(owner, world.Home.BranchId, world.OtherReviewId), "reply");

        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}/reviews?page=1");
        var publicRow = page.GetProperty("reviews").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == world.ReviewId);
        Assert.Equal(Thanks, publicRow.GetProperty("reply").GetProperty("text").GetString());
        Assert.Equal("Founder", publicRow.GetProperty("reply").GetProperty("authorName").GetString());

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}");
        var recent = detail.GetProperty("recentReviews").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == world.ReviewId);
        Assert.Equal(Thanks, recent.GetProperty("reply").GetProperty("text").GetString());

        var mine = await author.GetFromJsonAsync<JsonElement>($"/api/diner/branches/{world.Home.BranchId}/review");
        Assert.Equal(Thanks, mine.GetProperty("reply").GetProperty("text").GetString());

        // The author hears about it once, in the feed.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var entry = await db.DinerNotifications.AsNoTracking()
                .SingleAsync(n => n.DinerUserId == world.AuthorId && n.Kind == DinerNotificationKinds.ReviewReplied);

            Assert.Equal(world.Home.BranchId, entry.BranchId);
            Assert.Contains(world.ReviewId.ToString(), entry.ParamsJson, StringComparison.Ordinal);
            Assert.Equal(1, await ReplyAuditCountAsync(db, world.ReviewId, ReviewModerationService.ReplyAuditAction));
        }

        // The same text again writes nothing.
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var same = await owner.PutAsJsonAsync(replyUrl, new { text = Thanks });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.False((await same.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("edited").GetBoolean());

        // An edit: the last writer signs it, the first-written instant stays, and nobody is told again.
        const string edited = "Thank you, Arthur. Ask for the terrace next time.";
        var changed = await gm.PutAsJsonAsync(replyUrl, new { text = edited });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var after = (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply");
        Assert.Equal(edited, after.GetProperty("text").GetString());
        Assert.Equal("General Manager", after.GetProperty("authorName").GetString());
        Assert.Equal(6, after.GetProperty("authorRole").GetInt32());
        Assert.True(after.GetProperty("edited").GetBoolean());
        Assert.Equal(firstWritten, after.GetProperty("createdAtUtc").GetDateTime());
        Assert.True(after.GetProperty("updatedAtUtc").GetDateTime() > firstWritten);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                1,
                await db.DinerNotifications.CountAsync(n => n.DinerUserId == world.AuthorId && n.Kind == DinerNotificationKinds.ReviewReplied));
            Assert.Equal(2, await ReplyAuditCountAsync(db, world.ReviewId, ReviewModerationService.ReplyAuditAction));
        }

        // The public and the author read the writer's first name and surname initial, as diners are
        // shown; the console keeps the full name.
        Assert.Equal("General Manager", (await ConsoleReplyAsync(owner, world)).GetProperty("authorName").GetString());

        var publicAfter = (await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}/reviews?page=1"))
            .GetProperty("reviews").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == world.ReviewId)
            .GetProperty("reply");
        Assert.Equal("General M.", publicAfter.GetProperty("authorName").GetString());
        Assert.Equal(6, publicAfter.GetProperty("authorRole").GetInt32());
        Assert.True(publicAfter.GetProperty("edited").GetBoolean());

        var recentAfter = (await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}"))
            .GetProperty("recentReviews").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == world.ReviewId);
        Assert.Equal("General M.", recentAfter.GetProperty("reply").GetProperty("authorName").GetString());

        var mineAfter = await author.GetFromJsonAsync<JsonElement>($"/api/diner/branches/{world.Home.BranchId}/review");
        Assert.Equal("General M.", mineAfter.GetProperty("reply").GetProperty("authorName").GetString());
    }

    // ------------------------------------------------------------ the platform's takedown of a reply

    [SkippableFact]
    public async Task A_platform_admin_takes_a_reply_down_and_only_that_reaches_the_activity_feed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));

        var replyUrl = ReplyUrl(world.Home.BranchId, world.ReviewId);
        var platformUrl = $"/api/platform/reviews/{world.ReviewId}/reply";

        // The venue writes, takes back and writes again: none of that is the platform's news.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(replyUrl, new { text = Thanks })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync(replyUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(replyUrl, new { text = "Rude words about the guest." })).StatusCode);

        // Yalla hides the review, and then takes the reply down too - the venue cannot write to it now.
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PutAsJsonAsync($"/api/platform/reviews/{world.ReviewId}/visibility", new { hidden = true, reason = "Offensive." })).StatusCode);

        // The venue's own staff do not use the platform route (refused by the route's policy, no body).
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync(platformUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync(platformUrl)).StatusCode);
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.DeleteAsync($"/api/platform/reviews/{Guid.CreateVersion7()}/reply"), HttpStatusCode.NotFound, "not-found");

        var removed = await platform.DeleteAsync(platformUrl);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        AssertAbsent(await removed.Content.ReadFromJsonAsync<JsonElement>(), "reply");
        AssertAbsent(await ConsoleRowAsync(manager, world.Home.BranchId, world.ReviewId), "reply");

        // Again is a no-op.
        Assert.Equal(HttpStatusCode.OK, (await platform.DeleteAsync(platformUrl)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Empty(await db.DinerNotifications.AsNoTracking()
                .Where(n => n.DinerUserId == world.AuthorId && n.Kind == DinerNotificationKinds.ReviewReplied)
                .ToListAsync());

            var platformRows = await db.PlatformAuditLogs.AsNoTracking()
                .Where(l => l.TargetId == world.ReviewId
                            && l.Action == ReviewModerationService.RemoveReplyAuditAction
                            && l.ActorStaffMemberId == world.Admin.StaffMemberId)
                .ToListAsync();
            var row = Assert.Single(platformRows);
            Assert.Contains("\"actorType\":\"platform\"", row.ChangesJson, StringComparison.Ordinal);
        }

        // The feed: the platform's takedown, and no venue reply or venue removal.
        var feed = await platform.GetFromJsonAsync<JsonElement>($"/api/platform/activity?venueId={world.Home.VenueId}&limit=100");
        var actions = feed.GetProperty("items").EnumerateArray()
            .Select(i => (Action: i.GetProperty("action").GetString(), Role: i.GetProperty("actor").GetProperty("role").GetInt32()))
            .ToList();

        Assert.DoesNotContain(actions, a => a.Action == ReviewModerationService.ReplyAuditAction);
        Assert.Equal(
            (ReviewModerationService.RemoveReplyAuditAction, 5),
            Assert.Single(actions, a => a.Action == ReviewModerationService.RemoveReplyAuditAction));
    }

    // ------------------------------------------------------------ an account that is going

    [SkippableFact]
    public async Task A_reply_or_a_takedown_writes_no_notice_into_an_account_that_is_not_live()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            (await db.DinerUsers.SingleAsync(d => d.Id == world.AuthorId)).SetActive(false);
            await db.SaveChangesAsync();
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));

        // The reply itself is the venue's, and is written.
        var written = await manager.PutAsJsonAsync(ReplyUrl(world.Home.BranchId, world.ReviewId), new { text = Thanks });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.Equal(Thanks, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("text").GetString());

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(
                $"/api/branches/{world.Home.BranchId}/reviews/{world.ReviewId}/visibility",
                new { hidden = true, reason = "Names a member of staff." })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Empty(await db.DinerNotifications.AsNoTracking().Where(n => n.DinerUserId == world.AuthorId).ToListAsync());
        }
    }

    // ------------------------------------------------------------ the row's own rules

    [SkippableFact]
    public async Task The_database_holds_a_reply_whole_and_signed_by_a_venue_rank()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var id = world.ReviewId;
        var at = factory.Clock.UtcNow;
        var staff = world.Owner.StaffMemberId;

        await using var db = fixture.CreateContext(factory.Clock);

        // Half a reply.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.BranchReviews SET ReplyText = N'Thanks.' WHERE Id = {id}"));

        // Whole, but signed by a waiter.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
            $"""
             UPDATE dbo.BranchReviews SET ReplyText = N'Thanks.', ReplyAuthorName = N'A Waiter', ReplyAuthorRole = 3,
                 ReplyByStaffMemberId = {staff}, RepliedAtUtc = {at}, ReplyUpdatedAtUtc = {at}
             WHERE Id = {id}
             """));

        // Whole, by a general manager.
        Assert.Equal(1, await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE dbo.BranchReviews SET ReplyText = N'Thanks.', ReplyAuthorName = N'General Manager', ReplyAuthorRole = 6,
                 ReplyByStaffMemberId = {staff}, RepliedAtUtc = {at}, ReplyUpdatedAtUtc = {at}
             WHERE Id = {id}
             """));
    }

    [SkippableFact]
    public async Task Removing_a_reply_clears_it_everywhere_takes_back_the_unread_notice_and_is_idempotent()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var author = factory.CreateClientWithToken(world.AuthorToken);
        using var anyone = factory.CreateClient();

        var replyUrl = ReplyUrl(world.Home.BranchId, world.ReviewId);

        var written = await manager.PutAsJsonAsync(replyUrl, new { text = Thanks });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.Equal(2, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("authorRole").GetInt32());

        // The undo of a first reply.
        var removed = await manager.DeleteAsync(replyUrl);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        AssertAbsent(await removed.Content.ReadFromJsonAsync<JsonElement>(), "reply");

        AssertAbsent(await ConsoleRowAsync(manager, world.Home.BranchId, world.ReviewId), "reply");

        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}/reviews?page=1");
        AssertAbsent(page.GetProperty("reviews").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == world.ReviewId), "reply");

        var mine = await author.GetFromJsonAsync<JsonElement>($"/api/diner/branches/{world.Home.BranchId}/review");
        AssertAbsent(mine, "reply");

        // Nothing left in the feed about a reply that is gone, and again is a no-op.
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync(replyUrl)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Empty(await db.DinerNotifications.AsNoTracking()
                .Where(n => n.DinerUserId == world.AuthorId && n.Kind == DinerNotificationKinds.ReviewReplied)
                .ToListAsync());
            Assert.Equal(1, await ReplyAuditCountAsync(db, world.ReviewId, ReviewModerationService.RemoveReplyAuditAction));
        }

        // A new reply after a removal is a new reply: it is first written now, and the author is told again.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await manager.PutAsJsonAsync(replyUrl, new { text = "Second thoughts." });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetProperty("edited").GetBoolean());

        // A notice the author has already read stays: it is history.
        Assert.Equal(HttpStatusCode.NoContent, (await author.PostAsJsonAsync("/api/diner/notifications/read", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync(replyUrl)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                1,
                await db.DinerNotifications.CountAsync(n => n.DinerUserId == world.AuthorId && n.Kind == DinerNotificationKinds.ReviewReplied));
        }
    }

    // ------------------------------------------------------------ refusals

    [SkippableFact]
    public async Task A_reply_needs_text_of_at_most_500_characters_and_a_review_that_is_shown()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));
        using var author = factory.CreateClientWithToken(world.AuthorToken);
        using var anyone = factory.CreateClient();

        var replyUrl = ReplyUrl(world.Home.BranchId, world.ReviewId);

        var blank = await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(replyUrl, new { text = "   " }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "text");
        Assert.Equal("required", Bound(blank));

        var missing = await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(replyUrl, new { }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "text");
        Assert.Equal("required", Bound(missing));

        // Nothing a reader would see is nothing: zero-width and other invisible characters only.
        var invisible = await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(replyUrl, new { text = "\u200B\u200D\uFEFF \u2060" }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "text");
        Assert.Equal("required", Bound(invisible));

        var tooLong = await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(replyUrl, new { text = new string('x', BranchReview.MaxReplyLength + 1) }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "text");
        Assert.Equal("max", Bound(tooLong));

        // Exactly the limit is fine, and the trimmed length is what counts.
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(replyUrl, new { text = " " + new string('x', BranchReview.MaxReplyLength) + " " })).StatusCode);

        // Hidden by the venue: the reply goes out of the public list with the review, the venue and the
        // author still see it, and it cannot be written to until the review is back.
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(
                $"/api/branches/{world.Home.BranchId}/reviews/{world.ReviewId}/visibility",
                new { hidden = true, reason = "Names a member of staff." })).StatusCode);

        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{world.Home.BranchId}/reviews?page=1");
        Assert.DoesNotContain(page.GetProperty("reviews").EnumerateArray(), r => r.GetProperty("reviewId").GetGuid() == world.ReviewId);
        Assert.Equal(JsonValueKind.Object, (await ConsoleRowAsync(manager, world.Home.BranchId, world.ReviewId)).GetProperty("reply").ValueKind);
        Assert.Equal(
            JsonValueKind.Object,
            (await author.GetFromJsonAsync<JsonElement>($"/api/diner/branches/{world.Home.BranchId}/review")).GetProperty("reply").ValueKind);

        await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(replyUrl, new { text = "Changed while hidden." }),
            HttpStatusCode.Conflict, "conflicting-state");

        // Hidden by Yalla: never answered.
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PutAsJsonAsync(
                $"/api/platform/reviews/{world.OtherReviewId}/visibility", new { hidden = true, reason = "Offensive." })).StatusCode);
        await ReviewIntegrityTests.AssertProblemAsync(
            await manager.PutAsJsonAsync(ReplyUrl(world.Home.BranchId, world.OtherReviewId), new { text = Thanks }),
            HttpStatusCode.Conflict, "conflicting-state");

        // Taking a reply down is never refused for the review being hidden.
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync(replyUrl)).StatusCode);
    }

    [SkippableFact]
    public async Task The_platform_does_not_reply_as_the_venue_and_a_review_of_another_branch_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));
        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));

        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.PutAsJsonAsync(ReplyUrl(world.Home.BranchId, world.ReviewId), new { text = Thanks }),
            HttpStatusCode.Forbidden, "forbidden");
        await ReviewIntegrityTests.AssertProblemAsync(
            await platform.DeleteAsync(ReplyUrl(world.Home.BranchId, world.ReviewId)),
            HttpStatusCode.Forbidden, "forbidden");

        await ReviewIntegrityTests.AssertProblemAsync(
            await owner.PutAsJsonAsync(ReplyUrl(world.Home.BranchId, world.SiblingReviewId), new { text = Thanks }),
            HttpStatusCode.NotFound, "not-found");
        await ReviewIntegrityTests.AssertProblemAsync(
            await owner.PutAsJsonAsync(ReplyUrl(world.Home.BranchId, Guid.CreateVersion7()), new { text = Thanks }),
            HttpStatusCode.NotFound, "not-found");

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Null((await db.BranchReviews.AsNoTracking().SingleAsync(r => r.Id == world.SiblingReviewId)).ReplyText);
    }

    // ------------------------------------------------------------ the list's summary

    [SkippableFact]
    public async Task The_console_list_counts_the_whole_branch_and_names_who_hid_a_review()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var now = factory.Clock.UtcNow;

        // Beside the arranged 3-star and 5-star reviews: an old 2-star, a reported 4-star, and a 1-star
        // the manager hides.
        Guid reportedId;
        Guid toHideId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedReviewAsync(
                db, world.Home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Old Timer", now), 2, "Long ago.", now.AddDays(-40));
            reportedId = await ReviewTestData.SeedReviewAsync(
                db, world.Home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Karen M", now), 4, "Spam link.", now.AddDays(-2));
            toHideId = await ReviewTestData.SeedReviewAsync(
                db, world.Home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Gor T", now), 1, "Rude words.", now.AddDays(-1));

            db.BranchReviewReports.Add(new BranchReviewReport(
                reportedId, await ReviewTestData.SeedDinerAsync(db, "Reporter", now), ReviewReportReasons.Spam, null, now));
            await db.SaveChangesAsync();
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, world.Home));
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(
                $"/api/branches/{world.Home.BranchId}/reviews/{toHideId}/visibility",
                new { hidden = true, reason = "Offensive words." })).StatusCode);

        // The summary is the branch's, whatever the filter and the page.
        var list = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{world.Home.BranchId}/reviews?filter=hidden&pageSize=1");
        var summary = list.GetProperty("summary");

        Assert.Equal(5, summary.GetProperty("all").GetInt32());
        Assert.Equal(1, summary.GetProperty("reported").GetInt32());
        Assert.Equal(1, summary.GetProperty("hidden").GetInt32());
        Assert.Equal(4, summary.GetProperty("visible").GetInt32());

        // Visible: 3, 5, 2, 4 -> 3.5. Written more than 30 days ago: the 2 alone -> 2.0.
        Assert.Equal(3.5, summary.GetProperty("rating").GetDouble());
        Assert.Equal(1.5, summary.GetProperty("ratingChange30Days").GetDouble());

        var distribution = summary.GetProperty("distribution").EnumerateArray()
            .Select(d => (d.GetProperty("stars").GetInt32(), d.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal(new List<(int, int)> { (5, 1), (4, 1), (3, 1), (2, 1), (1, 0) }, distribution);

        var hidden = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(toHideId, hidden.GetProperty("reviewId").GetGuid());
        Assert.Equal("Test Manager", hidden.GetProperty("hiddenByName").GetString());

        // Yalla's takedowns carry no name; a shown review carries none either.
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, world.Admin));
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PutAsJsonAsync(
                $"/api/platform/reviews/{reportedId}/visibility", new { hidden = true, reason = "Spam." })).StatusCode);

        AssertAbsent(await ConsoleRowAsync(manager, world.Home.BranchId, reportedId), "hiddenByName");
        AssertAbsent(await ConsoleRowAsync(manager, world.Home.BranchId, world.ReviewId), "hiddenByName");

        // Yalla's takedown leaves three shown.
        var afterTakedown = (await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{world.Home.BranchId}/reviews"))
            .GetProperty("summary");
        Assert.Equal(3, afterTakedown.GetProperty("visible").GetInt32());

        // One recent review and nothing older: a rating, but no change to show.
        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, world.Owner));
        var sibling = (await owner.GetFromJsonAsync<JsonElement>($"/api/branches/{world.SiblingBranchId}/reviews"))
            .GetProperty("summary");
        Assert.Equal(1, sibling.GetProperty("visible").GetInt32());
        AssertAbsent(sibling, "ratingChange30Days");
        Assert.Equal(5, sibling.GetProperty("distribution").GetArrayLength());
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        AuthBranch Home,
        Guid SiblingBranchId,
        PanelAccount Owner,
        PanelAccount GeneralManager,
        PlatformAdminAccount Admin,
        string AuthorToken,
        Guid AuthorId,
        Guid ReviewId,
        Guid OtherReviewId,
        Guid SiblingReviewId);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        // Signed in first, so the review written below is one this diner can read back as their own.
        var (authorToken, authorId) = await ReviewIntegrityTests.SignInDinerAsync(factory);

        await using var db = fixture.CreateContext(factory.Clock);
        var now = factory.Clock.UtcNow;

        var home = await AuthTestData.CreateBranchAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);

        var review = await ReviewTestData.SeedReviewAsync(db, home.BranchId, authorId, 3, "Slow on Friday.", now.AddMinutes(-10));
        var other = await ReviewTestData.SeedReviewAsync(
            db, home.BranchId, await ReviewTestData.SeedDinerAsync(db, "Lilit Sargsyan", now), 5, "Lovely.", now.AddMinutes(-5));
        var elsewhere = await ReviewTestData.SeedReviewAsync(
            db, sibling, await ReviewTestData.SeedDinerAsync(db, "Mariam Hakobyan", now), 4, null, now.AddMinutes(-3));

        return new World(
            home,
            sibling,
            await AuthTestData.SeedOwnerAsync(db, home.VenueId),
            await AuthTestData.SeedGeneralManagerAsync(db, home.VenueId),
            await AuthTestData.CreatePlatformAdminAsync(db),
            authorToken,
            authorId,
            review,
            other,
            elsewhere);
    }

    /// <summary>Null is left off the wire, so "no reply" and "no name" are an absent property.</summary>
    private static void AssertAbsent(JsonElement element, string property) =>
        Assert.False(element.TryGetProperty(property, out _), $"Expected no '{property}' on {element}");

    private static string ReplyUrl(Guid branchId, Guid reviewId) => $"/api/branches/{branchId}/reviews/{reviewId}/reply";

    private static async Task<JsonElement> ConsoleRowAsync(HttpClient client, Guid branchId, Guid reviewId)
    {
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/branches/{branchId}/reviews?pageSize=100");

        return list.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("reviewId").GetGuid() == reviewId);
    }

    private static async Task<JsonElement> ConsoleReplyAsync(HttpClient client, World world) =>
        (await ConsoleRowAsync(client, world.Home.BranchId, world.ReviewId)).GetProperty("reply");

    private static string? Bound(JsonElement problem) =>
        problem.GetProperty("context").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("field").GetString() == "text")
            .GetProperty("bound").GetString();

    private static Task<int> ReplyAuditCountAsync(Yalla.Infrastructure.Persistence.YallaDbContext db, Guid reviewId, string action) =>
        db.PlatformAuditLogs.AsNoTracking().CountAsync(l => l.TargetId == reviewId && l.Action == action);
}
