using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K8: who may write a first review, what an unchanged revision does, the order the public list
/// keeps, and what the diner is told about their own review.
/// </summary>
/// <remarks>
/// Through the real pipeline, because each rule is a statement about what reaches the wire: a 403 with
/// its window, an <c>updatedAtUtc</c> that did not move, a list whose first entry did not change.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReviewIntegrityTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_diner_with_no_visit_is_refused_and_a_place_at_one_of_the_branchs_tables_opens_the_review()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        var (token, dinerUserId) = await SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        var route = $"/api/diner/branches/{mine.BranchId}/review";

        var refused = await AssertProblemAsync(
            await diner.PostAsJsonAsync(route, new { rating = 1, text = "Never been." }),
            HttpStatusCode.Forbidden,
            "review-needs-visit");

        Assert.Equal(mine.BranchId, refused.GetProperty("context").GetProperty("branchId").GetGuid());
        Assert.Equal(180, refused.GetProperty("context").GetProperty("windowDays").GetInt32());

        // A first write through PUT is a first review too.
        await AssertProblemAsync(
            await diner.PutAsJsonAsync(route, new { rating = 1 }), HttpStatusCode.Forbidden, "review-needs-visit");

        // A visit somewhere else is not a visit here.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, elsewhere, factory.Clock.UtcNow, dinerUserId);
        }

        await AssertProblemAsync(
            await diner.PostAsJsonAsync(route, new { rating = 1 }), HttpStatusCode.Forbidden, "review-needs-visit");

        // Two days ago, at one of this branch's tables.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, mine, factory.Clock.UtcNow.AddDays(-2), dinerUserId);
        }

        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync(route, new { rating = 5, text = "Warm lavash." })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.BranchReviews.CountAsync(r => r.DinerUserId == dinerUserId));
        }
    }

    [SkippableFact]
    public async Task A_seated_or_completed_booking_in_the_window_counts_and_an_unseated_or_older_one_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        var (token, dinerUserId) = await SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        var route = $"/api/diner/branches/{mine.BranchId}/review";
        var now = factory.Clock.UtcNow;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            // Confirmed and never seated: a promise, not a visit.
            await ReviewTestData.SeedBookingAsync(db, mine, dinerUserId, now.AddDays(-3), ReservationStatus.Confirmed);

            // Seated, but a day longer ago than the window.
            await ReviewTestData.SeedBookingAsync(db, mine, dinerUserId, now.AddDays(-181), ReservationStatus.Seated);
        }

        await AssertProblemAsync(
            await diner.PostAsJsonAsync(route, new { rating = 4 }), HttpStatusCode.Forbidden, "review-needs-visit");

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedBookingAsync(db, mine, dinerUserId, now.AddDays(-179), ReservationStatus.Completed);
        }

        Assert.Equal(HttpStatusCode.Created, (await diner.PostAsJsonAsync(route, new { rating = 4 })).StatusCode);
    }

    [SkippableFact]
    public async Task Revising_an_existing_review_is_never_refused_for_the_visit()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        var (token, dinerUserId) = await SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        var route = $"/api/diner/branches/{mine.BranchId}/review";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, mine, factory.Clock.UtcNow, dinerUserId);
        }

        Assert.Equal(HttpStatusCode.Created, (await diner.PostAsJsonAsync(route, new { rating = 5 })).StatusCode);

        // The visit no longer on record - the way deleting the place at the table would leave it.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.TabParticipants
                .Where(p => p.UserId == dinerUserId)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.UserId, (Guid?)null));
        }

        var revised = await diner.PutAsJsonAsync(route, new { rating = 3, text = "Changed my mind." });

        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        Assert.Equal(3, (await revised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rating").GetInt32());
    }

    [SkippableFact]
    public async Task An_identical_put_writes_nothing_and_a_revision_is_marked_edited_without_moving_up_the_list()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        var (firstToken, firstId) = await SignInDinerAsync(factory);
        var (secondToken, secondId) = await SignInDinerAsync(factory);
        using var first = factory.CreateClientWithToken(firstToken);
        using var second = factory.CreateClientWithToken(secondToken);
        using var anyone = factory.CreateClient();
        var route = $"/api/diner/branches/{mine.BranchId}/review";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, mine, factory.Clock.UtcNow, firstId, secondId);
        }

        // R1, then a minute later R2.
        var r1 = await first.PutAsJsonAsync(route, new { rating = 4, text = "Good coffee." });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var r1Body = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var r1Id = r1Body.GetProperty("reviewId").GetGuid();
        var r1Updated = r1Body.GetProperty("updatedAtUtc").GetDateTime();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        var r2 = await second.PostAsJsonAsync(route, new { rating = 5 });
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var r2Id = (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reviewId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        // The form re-sends R1 unchanged - the text differs only by the whitespace that is trimmed.
        var same = await first.PutAsJsonAsync(route, new { rating = 4, text = "  Good coffee. " });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(r1Updated, (await same.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAtUtc").GetDateTime());

        var unchanged = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}/reviews?page=1");
        Assert.False(unchanged.GetProperty("reviews").EnumerateArray()
            .Single(r => r.GetProperty("reviewId").GetGuid() == r1Id)
            .GetProperty("edited").GetBoolean());

        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        // Now a real revision of R1.
        var revised = await first.PutAsJsonAsync(route, new { rating = 3, text = "Good coffee, slow service." });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        Assert.True((await revised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAtUtc").GetDateTime() > r1Updated);

        // R2 is still first: the list is by when a review was written, and R1 says it was edited.
        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}/reviews?page=1");
        var reviews = page.GetProperty("reviews").EnumerateArray().ToList();

        Assert.Equal([r2Id, r1Id], reviews.Select(r => r.GetProperty("reviewId").GetGuid()));
        Assert.False(reviews[0].GetProperty("edited").GetBoolean());
        Assert.True(reviews[1].GetProperty("edited").GetBoolean());
        Assert.Equal(3, reviews[1].GetProperty("rating").GetInt32());

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}");
        Assert.Equal(
            [r2Id, r1Id],
            detail.GetProperty("recentReviews").EnumerateArray().Select(r => r.GetProperty("reviewId").GetGuid()));
    }

    [SkippableFact]
    public async Task The_diners_own_review_says_what_name_it_is_published_under_and_whether_it_is_hidden()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        var (token, dinerUserId) = await SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        using var anyone = factory.CreateClient();
        var route = $"/api/diner/branches/{mine.BranchId}/review";

        Assert.Equal(
            HttpStatusCode.OK,
            (await diner.PutAsJsonAsync("/api/diner/me", new { displayName = "Anahit Sargsyan" })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, mine, factory.Clock.UtcNow, dinerUserId);
        }

        var created = await diner.PostAsJsonAsync(route, new { rating = 2, text = "Cold soup." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anahit S.", body.GetProperty("publicAuthorName").GetString());
        Assert.False(body.GetProperty("hidden").GetBoolean());

        // Taken down by moderation.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var review = await db.BranchReviews.SingleAsync(r => r.DinerUserId == dinerUserId);
            review.Hide(Guid.CreateVersion7(), byPlatform: true, "Test takedown.", factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        // The diner still sees it, marked; nobody else sees it at all.
        var mineBack = await diner.GetFromJsonAsync<JsonElement>(route);
        Assert.True(mineBack.GetProperty("hidden").GetBoolean());
        Assert.Equal("Anahit S.", mineBack.GetProperty("publicAuthorName").GetString());

        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}/reviews?page=1");
        Assert.Equal(0, page.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0, page.GetProperty("reviews").GetArrayLength());
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    internal static async Task<(string AccessToken, Guid DinerUserId)> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = ReviewTestData.NextPhone();

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid());
    }

    internal static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var raw = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == status, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {raw}");

        var problem = JsonDocument.Parse(raw).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());

        if (field is not null)
        {
            Assert.Equal(field, problem.GetProperty("context").GetProperty("field").GetString());
        }

        return problem;
    }
}
