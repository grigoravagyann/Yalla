using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>venueBranchCount</c> on the public branch page: how many branches the venue's chooser lists, so the
/// page can say "All 3 branches" without downloading the city's venue list to count them.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PublicVenueBranchCountTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_branch_page_counts_the_branches_the_venues_chooser_lists_read_live()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        string venueSlug;
        string branchSlug;
        Guid venueId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var first = await TestBranchBuilder.CreateAsync(db);
            var venue = await db.Venues.SingleAsync(v => v.Id == first.VenueId);
            venueId = venue.Id;
            venueSlug = venue.Slug;
            branchSlug = await db.Branches.Where(b => b.Id == first.BranchId).Select(b => b.Slug).SingleAsync();

            // Two more: one published, one switched off - which the chooser does not list at all.
            db.Branches.Add(new Branch(venue, "North", $"north-{Guid.NewGuid():N}"[..20], "2 North St", 40.2, 44.5, "Asia/Yerevan"));

            var closed = new Branch(venue, "Closed", $"closed-{Guid.NewGuid():N}"[..20], "3 Old St", 40.2, 44.5, "Asia/Yerevan");
            closed.SetActive(false);
            db.Branches.Add(closed);

            await db.SaveChangesAsync();

            // Both set up to take guests, so what the chooser counts is the switch alone.
            foreach (var extra in await db.Branches.Where(b => b.VenueId == venueId && b.Id != first.BranchId).Select(b => b.Id).ToListAsync())
            {
                await TestBranchBuilder.TakeGuestsAsync(db, extra);
            }
        }

        using var anonymous = factory.CreateClient();
        var url = $"/api/public/branches/{venueSlug}/{branchSlug}";

        var page = await ReadAsync(anonymous, url);
        Assert.Equal(2, page.GetProperty("venueBranchCount").GetInt32());

        // The same number the chooser shows for this venue.
        var venues = await ReadAsync(anonymous, "/api/public/venues");
        var listed = venues.EnumerateArray().Single(v => v.GetProperty("venueSlug").GetString() == venueSlug);
        Assert.Equal(2, listed.GetProperty("branches").GetArrayLength());

        // Read live, not from the page's cached plan: a branch switched on shows at once.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var closed = await db.Branches.SingleAsync(b => b.VenueId == venueId && b.Name == "Closed");
            closed.SetActive(true);
            await db.SaveChangesAsync();
        }

        Assert.Equal(3, (await ReadAsync(anonymous, url)).GetProperty("venueBranchCount").GetInt32());
    }

    [SkippableFact]
    public async Task A_venue_with_one_branch_counts_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        string url;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var branch = await TestBranchBuilder.CreateAsync(db);
            var slugs = await db.Branches.Where(b => b.Id == branch.BranchId)
                .Select(b => new { VenueSlug = b.Venue.Slug, b.Slug })
                .SingleAsync();
            url = $"/api/public/branches/{slugs.VenueSlug}/{slugs.Slug}";
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(1, (await ReadAsync(anonymous, url)).GetProperty("venueBranchCount").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
