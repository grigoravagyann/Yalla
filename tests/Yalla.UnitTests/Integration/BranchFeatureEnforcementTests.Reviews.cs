using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// Reviews: blocked, writing, revising, reporting and replying are refused, and the public and diner reads hide every
    /// review - no list, no rating, no count, not an error; the diner's own review reads as none. The rows stay: allowed
    /// again, the review is back as it was.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_reviews_refuse_writes_and_replies_and_hide_every_review_until_allowed_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory, tableCount: 2);

        var (reviewerToken, reviewerId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        var (reporterToken, _) = await ReviewIntegrityTests.SignInDinerAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedBookingAsync(db, branch, reviewerId, factory.Clock.UtcNow.AddDays(-3), ReservationStatus.Completed);
        }

        using var reviewer = factory.CreateClientWithToken(reviewerToken);
        using var reporter = factory.CreateClientWithToken(reporterToken);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var anyone = factory.CreateClient();

        var reviewUrl = $"/api/diner/branches/{branch.BranchId}/review";
        var written = await OkAsync(await reviewer.PostAsJsonAsync(reviewUrl, new { rating = 5, text = "Lovely." }));
        var reviewId = written.GetProperty("reviewId").GetGuid();

        await BlockAsync(factory, branch.BranchId, BranchFeature.Reviews);

        // Every write refused.
        await AssertRefusedAsync(
            await reviewer.PutAsJsonAsync(reviewUrl, new { rating = 4, text = "Still lovely." }), BranchFeature.Reviews, branch.BranchId);
        await AssertRefusedAsync(
            await reporter.PostAsJsonAsync($"/api/diner/reviews/{reviewId}/report", new { reason = "spam", note = (string?)null }),
            BranchFeature.Reviews,
            branch.BranchId);
        await AssertRefusedAsync(
            await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/reviews/{reviewId}/reply", new { text = "Thank you!" }),
            BranchFeature.Reviews,
            branch.BranchId);

        // Hidden, not refused: no reviews, no rating, no count - on the place's card, its page of reviews and the list.
        var detail = await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}");
        Assert.Equal(0, detail.GetProperty("recentReviews").GetArrayLength());
        Assert.Equal(0, detail.GetProperty("listing").GetProperty("reviewCount").GetInt32());
        AssertNoRating(detail.GetProperty("listing"));

        var page = await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal(0, page.GetProperty("reviews").GetArrayLength());
        Assert.Equal(0, page.GetProperty("reviewCount").GetInt32());
        Assert.All(page.GetProperty("ratingCounts").EnumerateArray(), c => Assert.Equal(0, c.GetInt32()));
        AssertNoRating(page);

        var card = (await ReadAsync(anyone, "/api/public/branches")).EnumerateArray()
            .Single(b => b.GetProperty("branchId").GetGuid() == branch.BranchId);
        Assert.Equal(0, card.GetProperty("reviewCount").GetInt32());
        AssertNoRating(card);

        // The diner's own reads as none.
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync(reviewUrl)).StatusCode);

        // Allowed again: the review the rows kept is back.
        await BlockAsync(factory, branch.BranchId);

        var back = await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}");
        Assert.Equal(reviewId, Assert.Single(back.GetProperty("recentReviews").EnumerateArray()).GetProperty("reviewId").GetGuid());
        Assert.Equal(5.0, back.GetProperty("listing").GetProperty("rating").GetDouble());
        Assert.Equal(5, (await ReadAsync(reviewer, reviewUrl)).GetProperty("rating").GetInt32());
    }

    private static void AssertNoRating(JsonElement element) =>
        Assert.True(
            !element.TryGetProperty("rating", out var rating) || rating.ValueKind == JsonValueKind.Null,
            $"Expected no rating, got {element}");
}
