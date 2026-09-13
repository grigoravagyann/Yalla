using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The diner app on real data: the listing a venue writes, Explore/search/details reading it,
/// reviews, table markers on the cover photo, and the Orders tab.
/// </summary>
/// <remarks>
/// Through the real pipeline, because what is being proved is mostly about the pipeline: that a
/// console write reaches the anonymous read, that one diner reviews once, that an unproved number is
/// refused, and that one diner's orders never reach another.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerBrowseTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ listing, search, details

    [SkippableFact]
    public async Task A_managers_listing_reaches_search_and_the_details_screen()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        Guid first;
        Guid second;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            first = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
            second = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var cuisine = $"Armenian Mediterranean {Guid.NewGuid():N}";

        var saved = await manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/listing", new
        {
            cuisine,
            about = "A bright all-day cafe.",
            priceLevel = 2,
            websiteUrl = "https://thegreentable.example",
            amenities = new[] { "WIFI", "vegan" },
            galleryPhotoIds = new[] { second, first },
            latitude = 40.1843,
            longitude = 44.5129,
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var listing = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["wifi", "vegan"], listing.GetProperty("amenities").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(second, listing.GetProperty("gallery")[0].GetProperty("photoId").GetGuid());

        using var anyone = factory.CreateClient();

        var found = await anyone.GetFromJsonAsync<JsonElement>(
            $"/api/public/branches/search?q={Uri.EscapeDataString(cuisine.ToUpperInvariant())}&lat=40.1777&lng=44.5126");
        var card = Assert.Single(found.EnumerateArray());
        Assert.Equal(mine.BranchId, card.GetProperty("branchId").GetGuid());
        Assert.Equal(2, card.GetProperty("priceLevel").GetInt32());
        Assert.Equal(40.1843, card.GetProperty("latitude").GetDouble());
        Assert.InRange(card.GetProperty("distanceKm").GetDouble(), 0.5, 1.5);
        Assert.Equal(0, card.GetProperty("reviewCount").GetInt32());
        Assert.False(card.TryGetProperty("rating", out _), "an unreviewed branch has no rating");

        // Created seconds ago, so it is new - derived, not stored.
        Assert.Contains("new", card.GetProperty("badges").EnumerateArray().Select(b => b.GetString()));

        var all = await anyone.GetFromJsonAsync<JsonElement>("/api/public/branches");
        Assert.Contains(all.EnumerateArray(), b => b.GetProperty("branchId").GetGuid() == mine.BranchId);

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}");
        Assert.Equal(cuisine, detail.GetProperty("listing").GetProperty("cuisine").GetString());
        Assert.Equal("A bright all-day cafe.", detail.GetProperty("about").GetString());
        Assert.Equal(first, detail.GetProperty("gallery")[1].GetProperty("photoId").GetGuid());
        Assert.True(detail.GetProperty("tableCount").GetInt32() > 0);
        Assert.True(detail.GetProperty("openingHours").GetArrayLength() > 0);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await anyone.GetAsync("/api/public/branches/search?q=x&lat=40.1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/api/public/branches/{Guid.NewGuid()}")).StatusCode);
    }

    [SkippableFact]
    public async Task A_listing_out_of_bounds_or_with_another_branchs_photo_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        Guid theirPhoto;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            var theirs = await AuthTestData.CreateBranchAsync(db);
            theirPhoto = await TestMenuBuilder.AddPhotoAsync(db, theirs.BranchId);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));

        var outOfBounds = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/listing", new { priceLevel = 7, amenities = new[] { "jacuzzi" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOfBounds.StatusCode);

        var foreign = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/listing", new { cuisine = "Georgian", galleryPhotoIds = new[] { theirPhoto } });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var unchanged = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/listing");
        Assert.False(unchanged.TryGetProperty("cuisine", out _), "a refused form writes nothing");
    }

    // ------------------------------------------------------------ reviews

    [SkippableFact]
    public async Task A_verified_diner_reviews_once_revises_it_and_the_rating_is_the_real_average()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        var route = $"/api/diner/branches/{mine.BranchId}/review";
        using var ani = factory.CreateClientWithToken((await SignInDinerAsync(factory)).AccessToken);
        using var narek = factory.CreateClientWithToken((await SignInDinerAsync(factory)).AccessToken);

        var created = await ani.PostAsJsonAsync(route, new { rating = 5, text = " Warm lavash. " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Warm lavash.", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());

        // One per diner per branch.
        Assert.Equal(HttpStatusCode.Conflict, (await ani.PostAsJsonAsync(route, new { rating = 1 })).StatusCode);

        var revised = await ani.PutAsJsonAsync(route, new { rating = 4 });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var revisedBody = await revised.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, revisedBody.GetProperty("rating").GetInt32());
        Assert.False(revisedBody.TryGetProperty("text", out _), "a revision without text clears it");

        Assert.Equal(HttpStatusCode.Created, (await narek.PutAsJsonAsync(route, new { rating = 2 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await narek.PutAsJsonAsync(route, new { rating = 9 })).StatusCode);

        var mineBack = await ani.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(4, mineBack.GetProperty("rating").GetInt32());

        using var anyone = factory.CreateClient();
        var page = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}/reviews?page=1");
        Assert.Equal(2, page.GetProperty("reviewCount").GetInt32());
        Assert.Equal(3.0, page.GetProperty("rating").GetDouble());
        Assert.Equal(2, page.GetProperty("reviews").GetArrayLength());
        Assert.Equal("Yalla diner", page.GetProperty("reviews")[0].GetProperty("authorName").GetString());

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}");
        Assert.Equal(3.0, detail.GetProperty("listing").GetProperty("rating").GetDouble());
        Assert.Equal(2, detail.GetProperty("recentReviews").GetArrayLength());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await anyone.GetAsync($"/api/public/branches/{mine.BranchId}/reviews?page=0")).StatusCode);
    }

    [SkippableFact]
    public async Task A_diner_whose_number_was_never_proved_cannot_review()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registered = await anonymous.PostAsJsonAsync("/api/auth/diner/register", new
        {
            username = $"ani_{suffix}",
            email = $"ani.{suffix}@example.test",
            password = "khachapuri-2026",
            phoneE164 = $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}",
            displayName = "Ani",
        });
        registered.EnsureSuccessStatusCode();

        using var unproved = factory.CreateClientWithToken(
            (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        var refused = await unproved.PostAsJsonAsync($"/api/diner/branches/{mine.BranchId}/review", new { rating = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("phone-not-verified", await refused.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ table markers

    [SkippableFact]
    public async Task A_table_placed_on_the_cover_photo_is_served_as_a_live_marker_and_in_availability()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var plan = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/floor-plan");
        var firstId = plan.GetProperty("tables")[0].GetProperty("id").GetGuid();

        var saved = await manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/floor-plan", new
        {
            floorWidth = plan.GetProperty("floorWidth").GetInt32(),
            floorHeight = plan.GetProperty("floorHeight").GetInt32(),
            areas = Array.Empty<object>(),
            tables = plan.GetProperty("tables").EnumerateArray().Select(t => new
            {
                id = t.GetProperty("id").GetGuid(),
                label = t.GetProperty("label").GetString(),
                seats = t.GetProperty("seats").GetInt32(),
                x = t.GetProperty("x").GetInt32(),
                y = t.GetProperty("y").GetInt32(),
                width = t.GetProperty("width").GetInt32(),
                height = t.GetProperty("height").GetInt32(),
                rotationDegrees = t.GetProperty("rotationDegrees").GetDouble(),
                shape = t.GetProperty("shape").GetInt32(),
                isBookable = t.GetProperty("isBookable").GetBoolean(),
                photoX = t.GetProperty("id").GetGuid() == firstId ? 0.25 : (double?)null,
                photoY = t.GetProperty("id").GetGuid() == firstId ? 0.5 : (double?)null,
            }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var anyone = factory.CreateClient();
        var markers = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}/table-markers");
        var marker = Assert.Single(markers.GetProperty("tables").EnumerateArray());
        Assert.Equal(firstId, marker.GetProperty("tableId").GetGuid());
        Assert.Equal(0.25, marker.GetProperty("photoX").GetDouble());
        Assert.Equal(0.5, marker.GetProperty("photoY").GetDouble());
        Assert.Equal(1, marker.GetProperty("state").GetInt32());

        var availability = await anyone.GetFromJsonAsync<JsonElement>(
            $"/api/public/branches/{mine.BranchId}/availability?partySize=2");
        var placed = availability.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("tableId").GetGuid() == firstId);
        Assert.Equal(0.25, placed.GetProperty("photoX").GetDouble());

        // One coordinate without the other is refused, naming the field.
        var half = await manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/floor-plan", new
        {
            floorWidth = plan.GetProperty("floorWidth").GetInt32(),
            floorHeight = plan.GetProperty("floorHeight").GetInt32(),
            areas = Array.Empty<object>(),
            tables = plan.GetProperty("tables").EnumerateArray().Select(t => new
            {
                id = t.GetProperty("id").GetGuid(),
                label = t.GetProperty("label").GetString(),
                seats = t.GetProperty("seats").GetInt32(),
                x = t.GetProperty("x").GetInt32(),
                y = t.GetProperty("y").GetInt32(),
                width = t.GetProperty("width").GetInt32(),
                height = t.GetProperty("height").GetInt32(),
                rotationDegrees = t.GetProperty("rotationDegrees").GetDouble(),
                shape = t.GetProperty("shape").GetInt32(),
                isBookable = t.GetProperty("isBookable").GetBoolean(),
                photoX = 0.4,
            }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, half.StatusCode);
    }

    // ------------------------------------------------------------ orders

    [SkippableFact]
    public async Task A_diner_sees_their_own_and_their_tables_orders_with_the_kitchen_timeline_and_nobody_elses()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var ani = await SignInDinerAsync(factory);
        var stranger = await SignInDinerAsync(factory);

        AuthBranch mine;
        Guid ownOrderId;
        Guid tableOrderId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            mine = await AuthTestData.CreateBranchAsync(db);
            var menu = await TestMenuBuilder.CreateAsync(db, mine.BranchId);
            var tab = await AuthTestData.CreateOpenTabAsync(db, mine, mine.FirstTableId, now);

            var participant = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false, ani.DinerUserId);
            participant.Approve(now);
            db.TabParticipants.Add(participant);

            var ownOrder = TabOrder.PlacedByDiner(tab.TabId, participant.Id, now);
            ownOrder.AddLine(menu.Coffee, "Flat white", TestMenu.CoffeeAmd, 2, note: "oat milk");
            db.TabOrders.Add(ownOrder);

            // Keyed in by the waiter for the whole table: the diner is on the tab, so it is theirs too.
            var forTable = TabOrder.PlacedByStaffMember(tab.TabId, mine.WaiterId, now.AddMinutes(1));
            forTable.AddLine(menu.Wine, "Areni red, bottle", TestMenu.WineAmd, 1, isTableAttributed: true);
            db.TabOrders.Add(forTable);

            await db.SaveChangesAsync();
            ownOrderId = ownOrder.Id;
            tableOrderId = forTable.Id;
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PostAsJsonAsync($"/api/orders/{ownOrderId}/status", new { status = 2 })).StatusCode);

        using var diner = factory.CreateClientWithToken(ani.AccessToken);
        var active = await diner.GetFromJsonAsync<JsonElement>("/api/diner/orders?status=active");
        Assert.Equal([tableOrderId, ownOrderId], active.EnumerateArray().Select(o => o.GetProperty("orderId").GetGuid()));

        var own = active.EnumerateArray().Single(o => o.GetProperty("orderId").GetGuid() == ownOrderId);
        Assert.Equal("preparing", own.GetProperty("status").GetString());
        Assert.Equal(2, own.GetProperty("kitchenStatus").GetInt32());
        Assert.Equal("dineIn", own.GetProperty("kind").GetString());
        Assert.Equal(mine.BranchId, own.GetProperty("branchId").GetGuid());
        Assert.Equal(2 * TestMenu.CoffeeAmd, own.GetProperty("totalAmd").GetInt64());
        Assert.Equal("oat milk", own.GetProperty("items")[0].GetProperty("note").GetString());
        Assert.False(own.GetProperty("canCancel").GetBoolean());
        Assert.Equal(
            ["confirmed", "preparing"],
            own.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("status").GetString()));

        Assert.Equal(0, (await diner.GetFromJsonAsync<JsonElement>("/api/diner/orders?status=history")).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/diner/orders/{ownOrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await diner.GetAsync("/api/diner/orders?status=soon")).StatusCode);

        using var other = factory.CreateClientWithToken(stranger.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/diner/orders/{ownOrderId}")).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/diner/orders")).GetArrayLength());

        using var anyone = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.GetAsync("/api/diner/orders")).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static async Task<(string AccessToken, Guid DinerUserId)> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid());
    }
}
