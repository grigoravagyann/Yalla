using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The anonymous surface: what it publishes, and everything it must not.
/// </summary>
/// <remarks>
/// This is the one surface with no token in front of it and a URL meant to be pasted into a group
/// chat, so the tests that matter are the negative ones - what a suspended venue answers, and what
/// is absent from the body.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class PublicSurfaceTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 1. suspended and inactive

    /// <summary>
    /// <b>Test 1.</b> A suspended venue and an inactive branch are 404 from every public route.
    /// </summary>
    /// <remarks>
    /// Not an empty result, and not a distinguishable answer. A public page that said "this venue is
    /// suspended" would be publishing a customer's billing status to anybody who guessed a slug.
    /// </remarks>
    [SkippableFact]
    public async Task Every_public_route_is_a_404_for_a_suspended_venue_and_an_inactive_branch()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();

        // While it is trading, everything answers.
        foreach (var route in Routes(world))
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(route)).StatusCode);
        }

        // Suspend the venue, and every one of them stops.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var venue = await db.Venues.FirstAsync(v => v.Id == world.VenueId);
            venue.Suspend(factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        foreach (var route in Routes(world))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(route)).StatusCode);
        }

        // Reactivate the venue and switch the branch off instead: same answer, different cause.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var venue = await db.Venues.FirstAsync(v => v.Id == world.VenueId);
            venue.Reactivate();

            var branch = await db.Branches.FirstAsync(b => b.Id == world.BranchId);
            branch.SetActive(false);

            await db.SaveChangesAsync();
        }

        foreach (var route in Routes(world))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(route)).StatusCode);
        }
    }

    // ------------------------------------------------------------ 2. the menu is the diner's menu

    /// <summary>
    /// <b>Test 2.</b> The public menu excludes incomplete items; the console's includes them.
    /// </summary>
    [SkippableFact]
    public async Task The_public_menu_excludes_incomplete_items_and_the_console_menu_includes_them()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        Guid unfinishedId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var menu = fixture.CreateMenuService(db);
            var category = (await menu.GetMenuAsync(world.BranchId)).First();

            // Typed in, not yet photographed. The state a menu spends its first week in.
            unfinishedId = (await menu.CreateItemAsync(
                world.BranchId, category.Id, new CreateMenuItemCommand("Tonight's fish", 4_500L))).Id;
        }

        using var anonymous = factory.CreateClient();

        var publicMenu = await ReadAsync(anonymous, $"/api/public/branches/{world.BranchId}/menu");

        var publicIds = publicMenu.GetProperty("categories").EnumerateArray()
            .SelectMany(c => c.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.DoesNotContain(unfinishedId, publicIds);
        Assert.NotEmpty(publicIds);

        // And every item that did make it is safe to read every field of, which is the point.
        foreach (var item in publicMenu.GetProperty("categories").EnumerateArray()
                     .SelectMany(c => c.GetProperty("items").EnumerateArray()))
        {
            Assert.True(item.GetProperty("isComplete").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("allergens").GetString()));
        }

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, world.Branch));

        var console = await ReadAsync(manager, $"/api/branches/{world.BranchId}/menu/manage");

        var consoleIds = console.EnumerateArray()
            .SelectMany(c => c.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(unfinishedId, consoleIds);
    }

    // ------------------------------------------------------------ 3. nothing private on the wire

    /// <summary>
    /// <b>Test 3.</b> No public response carries staff, tab, participant or other-diner data.
    /// </summary>
    /// <remarks>
    /// <b>Asserted on the serialised body, not on the DTO shape.</b> A test that inspected the
    /// record types would pass while a projection leaked a field through an anonymous type, and the
    /// thing that reaches a scraper is the JSON. The QR token is the one that would matter most:
    /// it is the credential that opens a tab, and it lives on the same table row the floor plan is
    /// drawn from.
    /// </remarks>
    [SkippableFact]
    public async Task No_public_response_contains_anything_private()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        // Put something private in every table the public routes touch, so "absent" is a fact about
        // the response rather than about an empty database.
        string qrToken;
        string waiterName;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            qrToken = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == world.BranchId)
                .Select(t => t.QrToken)
                .FirstAsync();

            waiterName = await db.StaffMembers.AsNoTracking()
                .Where(s => s.Id == world.Branch.WaiterId)
                .Select(s => s.FullName)
                .FirstAsync();
        }

        using var anonymous = factory.CreateClient();

        // A live tab with a named participant on it, so there is something to leak.
        var opened = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken,
                deviceId = "phone-private",
                clientCommandId = Guid.CreateVersion7(),
                displayName = "Anahit Secret",
            });

        opened.EnsureSuccessStatusCode();

        var forbidden = new[]
        {
            qrToken,
            waiterName,
            "Anahit Secret",

            // Field names, not only values: a field that is present and empty is still a field a
            // scraper learns the shape of.
            "qrToken",
            "staffMember",
            "participant",
            "tabId",
            "subtotalAmd",
            "pinHash",
            "deviceId",
        };

        foreach (var route in Routes(world))
        {
            var body = await (await anonymous.GetAsync(route)).Content.ReadAsStringAsync();

            foreach (var secret in forbidden)
            {
                Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ------------------------------------------------------------ 5. the slug pair

    /// <summary>
    /// <b>Test 5.</b> A branch is reachable by its slug pair, and a pairing across venues is a 404.
    /// </summary>
    /// <remarks>
    /// Slugs are the public identity - half of a printed link - so a branch slug that resolved
    /// under the wrong venue would make one venue's link open another's page.
    /// </remarks>
    [SkippableFact]
    public async Task A_branch_resolves_by_its_own_slug_pair_and_not_by_another_venues()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var mine = await ArrangeAsync(factory);
        var theirs = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();

        var page = await ReadAsync(anonymous, $"/api/public/branches/{mine.VenueSlug}/{mine.BranchSlug}");

        Assert.Equal(mine.BranchId, page.GetProperty("branchId").GetGuid());
        Assert.Equal(mine.VenueSlug, page.GetProperty("venueSlug").GetString());

        // Crossed over: my branch slug under their venue, and theirs under mine.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/public/branches/{theirs.VenueSlug}/{mine.BranchSlug}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/public/branches/{mine.VenueSlug}/{theirs.BranchSlug}")).StatusCode);

        // And the uniqueness the public identity rests on is enforced by the database, not by hope.
        await using var db = fixture.CreateContext(factory.Clock);

        Assert.Equal(1, await db.Venues.CountAsync(v => v.Slug == mine.VenueSlug));
        Assert.Equal(
            1,
            await db.Branches.CountAsync(b => b.Slug == mine.BranchSlug && b.VenueId == mine.VenueId));
    }

    /// <summary>The branch page carries the room, and the room carries no credentials.</summary>
    [SkippableFact]
    public async Task The_branch_page_carries_the_floor_plan_the_hours_and_a_live_table_count()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var page = await ReadAsync(anonymous, $"/api/public/branches/{world.VenueSlug}/{world.BranchSlug}");

        Assert.Equal("Asia/Yerevan", page.GetProperty("timeZoneId").GetString());
        Assert.Equal(7, page.GetProperty("openingHours").GetArrayLength());

        var floorPlan = page.GetProperty("floorPlan");
        var tables = floorPlan.GetProperty("tables").EnumerateArray().ToList();

        Assert.NotEmpty(tables);
        Assert.All(tables, t =>
        {
            Assert.False(t.TryGetProperty("qrToken", out _));
            Assert.False(t.TryGetProperty("status", out _));
            Assert.True(t.GetProperty("isFree").GetBoolean());
        });

        // Every table free before anybody sits down, and the count agrees with the plan.
        Assert.Equal(tables.Count, page.GetProperty("freeTableCount").GetInt32());

        var meta = await ReadAsync(anonymous, $"/api/public/branches/{world.BranchId}/meta");

        Assert.Contains(world.BranchSlug, meta.GetProperty("canonicalPath").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("title").GetString()));

        // The card carries no live count, because a chat app caches it for hours.
        Assert.False(meta.TryGetProperty("freeTableCount", out _));
    }

    // ------------------------------------------------------------ 4. rate limiting

    /// <summary>
    /// <b>Test 4.</b> The public limit triggers at its own threshold, and the app routes are not
    /// throttled with it.
    /// </summary>
    /// <remarks>
    /// This is the surface a scraper finds. Everything else anonymous here is reached by somebody
    /// who has at least scanned a code at a table.
    /// </remarks>
    [SkippableFact]
    public async Task The_public_routes_are_rate_limited_separately_from_the_app_routes()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int limit = 5;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:PublicPermitLimit", limit.ToString())
            .With("RateLimiting:PublicWindowSeconds", "60")

            // Generous, so the per-branch ceiling is not what fires first.
            .With("RateLimiting:PublicBranchPermitLimit", "1000")

            // And the app routes stay wide open, which is the second half of the assertion.
            .With("RateLimiting:GlobalPermitLimit", "1000")
            .With("RateLimiting:AvailabilityPermitLimit", "1000");

        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var route = $"/api/public/branches/{world.BranchId}/menu";

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < limit + 3; i++)
        {
            statuses.Add((await anonymous.GetAsync(route)).StatusCode);
        }

        Assert.Equal(limit, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // The app's own menu route, past the public budget, is untouched: the two limits are
        // separate policies rather than one shared window.
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/branches/{world.BranchId}/menu")).StatusCode);
    }

    // ------------------------------------------------------------ 6. the browse list

    /// <summary>
    /// The browse list carries each branch's time zone, nothing a person in the doorway could not
    /// see, and no suspended venue.
    /// </summary>
    /// <remarks>
    /// The diner app's venue and booking screens turn a slot into the branch's wall clock, and this
    /// list is the only public read in front of them. Without the zone they guessed
    /// <c>Asia/Yerevan</c>. The estate query already selected it into the cache and then dropped it
    /// while building the card.
    /// </remarks>
    [SkippableFact]
    public async Task The_browse_list_carries_each_branch_with_its_zone_and_drops_a_suspended_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var card = Card(await ReadAsync(anonymous, "/api/public/venues"), world.VenueSlug);

        Assert.NotNull(card);
        Assert.Equal((int)VenueType.Restaurant, card.Value.GetProperty("type").GetInt32());

        var branch = Assert.Single(card.Value.GetProperty("branches").EnumerateArray());

        Assert.Equal(world.BranchId, branch.GetProperty("branchId").GetGuid());
        Assert.Equal(world.BranchSlug, branch.GetProperty("branchSlug").GetString());
        Assert.Equal("Asia/Yerevan", branch.GetProperty("timeZoneId").GetString());

        // Field names as well as values, for the reason test 3 gives.
        foreach (var secret in new[] { "qrToken", "tabId", "participant", "deviceId", "pinHash", "staffMember" })
        {
            Assert.DoesNotContain(secret, card.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var venue = await db.Venues.FirstAsync(v => v.Id == world.VenueId);
            venue.Suspend(factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        // Read from a process with nothing cached, so this is a statement about the query. How long
        // a cached list may lag a suspension is LiveFor, and the statement-count test below pins
        // that it is the cache answering inside that window.
        await using var restarted = NewFactory();
        using var another = restarted.CreateClient();

        Assert.Null(Card(await ReadAsync(another, "/api/public/venues"), world.VenueSlug));
    }

    /// <summary>
    /// The free count counts the tables the table count counts - every table on the plan - and a bar
    /// stool somebody is sitting on is drawn taken and not counted.
    /// </summary>
    /// <remarks>
    /// The free count took every active table and the table count only the bookable ones, so an
    /// empty bar read as "3 of 2 free". Narrowing the free count to bookable tables made the two
    /// agree and left walk-in seats out of the one number a walk-in reads; widening the table count
    /// makes them agree and keeps both equal to what the plan draws.
    /// </remarks>
    [SkippableFact]
    public async Task The_free_count_counts_the_same_tables_as_the_table_count()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        // Beside the two bookable tables, two stools that only ever take walk-ins: one stays empty
        // and one is about to be sat at.
        string takenStoolQr;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var empty = Stool(world.BranchId, "Bar 1");
            var taken = Stool(world.BranchId, "Bar 2");

            db.DiningTables.AddRange(empty, taken);
            await db.SaveChangesAsync();

            takenStoolQr = taken.QrToken;
        }

        using var anonymous = factory.CreateClient();

        var seated = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken = takenStoolQr, deviceId = "phone-at-the-bar", clientCommandId = Guid.CreateVersion7() });

        seated.EnsureSuccessStatusCode();

        var page = await ReadAsync(anonymous, $"/api/public/branches/{world.VenueSlug}/{world.BranchSlug}");
        var tableCount = page.GetProperty("tableCount").GetInt32();
        var freeCount = page.GetProperty("freeTableCount").GetInt32();

        Assert.Equal(4, tableCount);
        Assert.True(
            freeCount <= tableCount,
            $"The page says {freeCount} of {tableCount} free: the two numbers count different tables.");
        Assert.Equal(3, freeCount);

        var tables = page.GetProperty("floorPlan").GetProperty("tables").EnumerateArray()
            .ToDictionary(t => t.GetProperty("label").GetString()!);

        Assert.False(tables["Bar 2"].GetProperty("isFree").GetBoolean());
        Assert.True(tables["Bar 1"].GetProperty("isFree").GetBoolean());

        // The number under the venue name is the plan's green tables, out of the plan's tables.
        Assert.Equal(tables.Count, tableCount);
        Assert.Equal(tables.Values.Count(t => t.GetProperty("isFree").GetBoolean()), freeCount);

        // And the browse card says the same as the page.
        var card = Card(await ReadAsync(anonymous, "/api/public/venues"), world.VenueSlug);

        Assert.NotNull(card);
        Assert.Equal(
            freeCount,
            Assert.Single(card.Value.GetProperty("branches").EnumerateArray()).GetProperty("freeTableCount").GetInt32());
    }

    /// <summary>
    /// An empty walk-in stool is a free table, so a room with every bookable table taken does not
    /// say "none free" beside an empty seat on its own plan.
    /// </summary>
    /// <remarks>
    /// With the free count narrowed to bookable tables, a bar with empty stools and both tables
    /// taken read zero - "No tables free right now" on the diner's Explore card and the web page -
    /// to the walk-in the live count is for, over a plan drawing the stool free.
    /// </remarks>
    [SkippableFact]
    public async Task An_empty_walk_in_stool_counts_as_free_when_every_bookable_table_is_taken()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        List<string> bookableQrs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            db.DiningTables.Add(Stool(world.BranchId, "Bar 1"));
            await db.SaveChangesAsync();

            bookableQrs = await db.DiningTables
                .Where(t => t.BranchId == world.BranchId && t.IsBookable)
                .Select(t => t.QrToken)
                .ToListAsync();
        }

        Assert.Equal(2, bookableQrs.Count);

        using var anonymous = factory.CreateClient();

        for (var i = 0; i < bookableQrs.Count; i++)
        {
            var seated = await anonymous.PostAsJsonAsync(
                "/api/tabs/open",
                new { qrToken = bookableQrs[i], deviceId = $"phone-at-table-{i}", clientCommandId = Guid.CreateVersion7() });

            seated.EnsureSuccessStatusCode();
        }

        var page = await ReadAsync(anonymous, $"/api/public/branches/{world.VenueSlug}/{world.BranchSlug}");
        var freeCount = page.GetProperty("freeTableCount").GetInt32();

        Assert.Equal(1, freeCount);
        Assert.Equal(3, page.GetProperty("tableCount").GetInt32());

        var drawnFree = page.GetProperty("floorPlan").GetProperty("tables").EnumerateArray()
            .Where(t => t.GetProperty("isFree").GetBoolean())
            .Select(t => t.GetProperty("label").GetString())
            .ToList();

        Assert.Equal("Bar 1", Assert.Single(drawnFree));

        var card = Card(await ReadAsync(anonymous, "/api/public/venues"), world.VenueSlug);

        Assert.NotNull(card);
        Assert.Equal(
            1,
            Assert.Single(card.Value.GetProperty("branches").EnumerateArray()).GetProperty("freeTableCount").GetInt32());
    }

    /// <summary>
    /// Between refreshes a browse read sends nothing to the database.
    /// </summary>
    /// <remarks>
    /// The estate and the free counts were cached; the published-branch check and the opening-hours
    /// read behind "open now" were not, so every Explore load and every pull-to-refresh in the city
    /// cost two queries whatever the cache held.
    /// </remarks>
    [SkippableFact]
    public async Task A_second_browse_read_inside_the_live_window_sends_nothing_to_the_database()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);

        await using (var db = fixture.CreateContext(clock))
        {
            await AuthTestData.CreateBranchAsync(db);
        }

        var counter = new CommandCountingInterceptor();
        await using var readDb = fixture.CreateContext(clock, counter);
        var query = SqlServerFixture.CreatePublicQuery(readDb, clock);

        var first = await query.GetVenuesAsync();
        var sentForTheFirst = counter.Count;

        Assert.NotEmpty(first);

        var second = await query.GetVenuesAsync();

        Assert.Equal(sentForTheFirst, counter.Count);
        Assert.Equal(first.Count, second.Count);
    }

    /// <summary>
    /// The browse list is refused neither at a branch's ceiling nor at the page budget.
    /// </summary>
    /// <remarks>
    /// It carries no branch in its route, so it fell into the single shared partition under the
    /// branch ceiling - one budget for every phone in the city - and under the thirty-a-minute page
    /// budget per address, which a carrier's shared address spends before dinner. Once the diner
    /// app's Explore reads it, a 429 there is the no-restaurants screen again.
    /// </remarks>
    [SkippableFact]
    public async Task The_browse_list_is_not_refused_at_a_branch_ceiling_or_the_page_budget()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int ceiling = 3;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:PublicPermitLimit", ceiling.ToString())
            .With("RateLimiting:PublicWindowSeconds", "60")
            .With("RateLimiting:PublicBranchPermitLimit", ceiling.ToString())
            .With("RateLimiting:PublicBranchWindowSeconds", "60")
            .With("RateLimiting:GlobalPermitLimit", "1000");

        var world = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var browse = new List<HttpStatusCode>();

        for (var i = 0; i < ceiling * 3; i++)
        {
            browse.Add((await anonymous.GetAsync("/api/public/venues")).StatusCode);
        }

        Assert.All(browse, status => Assert.Equal(HttpStatusCode.OK, status));

        // Both limits it escaped are live: a branch's page, from the same caller, stops at them.
        var page = new List<HttpStatusCode>();

        for (var i = 0; i < ceiling + 2; i++)
        {
            page.Add((await anonymous.GetAsync($"/api/public/branches/{world.BranchId}/menu")).StatusCode);
        }

        Assert.Equal(ceiling, page.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.TooManyRequests, page);
    }

    /// <summary>The browse list's own two budgets each fire at their own threshold.</summary>
    [SkippableTheory]
    [InlineData("RateLimiting:PublicBrowsePermitLimit")]
    [InlineData("RateLimiting:PublicBrowseCeilingPermitLimit")]
    public async Task The_browse_list_has_limits_of_its_own(string setting)
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int limit = 4;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With(setting, limit.ToString())
            .With("RateLimiting:GlobalPermitLimit", "1000");

        using var anonymous = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < limit * 2; i++)
        {
            statuses.Add((await anonymous.GetAsync("/api/public/venues")).StatusCode);
        }

        Assert.Equal(limit, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        AuthBranch Branch,
        Guid VenueId,
        Guid BranchId,
        string VenueSlug,
        string BranchSlug);

    private static IEnumerable<string> Routes(World world) =>
    [
        $"/api/public/branches/{world.VenueSlug}/{world.BranchSlug}",
        $"/api/public/branches/{world.BranchId}/menu",
        $"/api/public/branches/{world.BranchId}/meta",
        $"/api/public/branches/{world.BranchId}/availability?partySize=2",
    ];

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);
        await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var slugs = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { b.Slug, VenueSlug = b.Venue.Slug, b.VenueId })
            .FirstAsync();

        return new World(branch, slugs.VenueId, branch.BranchId, slugs.VenueSlug, slugs.Slug);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>One venue's card on the browse list, found by its slug, or null.</summary>
    /// <remarks>
    /// The test database is shared, so the list carries every venue any test made. Only ours is
    /// asserted on.
    /// </remarks>
    private static JsonElement? Card(JsonElement list, string venueSlug)
    {
        foreach (var venue in list.EnumerateArray())
        {
            if (venue.GetProperty("venueSlug").GetString() == venueSlug)
            {
                return venue;
            }
        }

        return null;
    }

    /// <summary>A one-seat stool at the bar, which only ever takes walk-ins.</summary>
    private static DiningTable Stool(Guid branchId, string label) =>
        new(branchId, label, seats: 1, x: 700, y: 50, width: 40, height: 40, shape: TableShape.Round, isBookable: false);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
