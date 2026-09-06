using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Prints the whole public branch page, so the frontend has a concrete body to map against.
/// </summary>
/// <remarks>
/// <para>
/// The frontend wrote its mapper against an assumed shape and none of it matched, which is the
/// reason this prompt exists. A guess is what produced that; this is the antidote - a test that
/// fails the moment a key the mapper depends on is renamed or dropped, and whose output can be
/// pasted straight into the other repo.
/// </para>
/// <para>
/// It asserts on <b>key names</b> and not on values. Values move with the seed and the clock; the
/// contract a mapper breaks on is the set of keys.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class PublicBranchPageShapeTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [SkippableFact]
    public async Task The_public_branch_page_has_exactly_these_keys()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch branch;
        string venueSlug;
        string branchSlug;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            // A published number and bookings switched on, so the optional half is populated
            // rather than omitted - which is the shape the frontend actually has to render.
            var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            row.SetPhoneE164("+37411223344");
            row.SetAcceptsWebBookings(true);
            await db.SaveChangesAsync();

            var slugs = await db.Branches.AsNoTracking()
                .Where(b => b.Id == branch.BranchId)
                .Select(b => new { b.Slug, VenueSlug = b.Venue.Slug })
                .FirstAsync();

            venueSlug = slugs.VenueSlug;
            branchSlug = slugs.Slug;
        }

        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/public/branches/{venueSlug}/{branchSlug}");
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(raw).RootElement;

        // Printed for the frontend to map against. Run this test to regenerate it.
        output.WriteLine(JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(
            new[]
            {
                "acceptsWebBookings",
                "address",
                "asOfUtc",
                "bookingWindowDays",
                "branchId",
                "branchName",
                "branchSlug",
                "floorPlan",
                "freeTableCount",
                "isOpenNow",
                "latitude",
                "longitude",
                "openingHours",
                "phoneE164",
                "policy",
                "status",
                "tableCount",
                "timeZoneId",
                "venueName",
                "venueSlug",
                "venueType",
            },
            page.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "cancellationDeadlineMinutes", "minLeadMinutes", "turnTimeMinutes" },
            page.GetProperty("policy").EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "areas", "floorHeight", "floorWidth", "tables" },
            page.GetProperty("floorPlan").EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
