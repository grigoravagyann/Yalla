using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The star distribution under a branch's rating: <c>ratingCounts</c> on the public reviews page and the
/// details screen, so the page can draw the bars without holding every review.
/// </summary>
/// <remarks>
/// Counted over exactly the reviews the average and the count are - the shown ones - so the five bars
/// always add up to the number printed beside them.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class PublicRatingCountsTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_counts_per_star_add_up_to_the_count_and_leave_hidden_reviews_out()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        TestBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            var now = factory.Clock.UtcNow;

            // Shown: three fives, a four, a two and a one.
            foreach (var stars in new[] { 5, 5, 4, 2, 1, 5 })
            {
                await ReviewTestData.SeedReviewAsync(
                    db, branch.BranchId, await ReviewTestData.SeedDinerAsync(db, "Ani S", now), stars, null, now);
            }

            // Taken down: a five and a one, counted nowhere.
            foreach (var stars in new[] { 5, 1 })
            {
                var id = await ReviewTestData.SeedReviewAsync(
                    db, branch.BranchId, await ReviewTestData.SeedDinerAsync(db, "Narek P", now), stars, "spam", now);

                var review = await db.BranchReviews.SingleAsync(r => r.Id == id);
                review.Hide(branch.ManagerId, byPlatform: true, "spam", now);
                await db.SaveChangesAsync();
            }
        }

        using var anonymous = factory.CreateClient();

        var reviews = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal([1, 1, 0, 1, 3], Counts(reviews));
        Assert.Equal(6, reviews.GetProperty("reviewCount").GetInt32());
        Assert.Equal(reviews.GetProperty("reviewCount").GetInt32(), Counts(reviews).Sum());

        // The summary is the branch's, not the page's: a page past the last review says the same.
        var later = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}/reviews?page=2");
        Assert.Equal([1, 1, 0, 1, 3], Counts(later));

        // The details screen carries the same five numbers beside the same live count.
        var detail = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}");
        Assert.Equal([1, 1, 0, 1, 3], Counts(detail));
        Assert.Equal(6, detail.GetProperty("listing").GetProperty("reviewCount").GetInt32());
    }

    [SkippableFact]
    public async Task A_branch_with_no_reviews_has_five_zeros()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        TestBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
        }

        using var anonymous = factory.CreateClient();

        var reviews = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal([0, 0, 0, 0, 0], Counts(reviews));
        Assert.Equal(0, reviews.GetProperty("reviewCount").GetInt32());

        var detail = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}");
        Assert.Equal([0, 0, 0, 0, 0], Counts(detail));
    }

    /// <summary><c>ratingCounts</c>: one star first, five stars last.</summary>
    private static int[] Counts(JsonElement body) =>
        [.. body.GetProperty("ratingCounts").EnumerateArray().Select(c => c.GetInt32())];

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
