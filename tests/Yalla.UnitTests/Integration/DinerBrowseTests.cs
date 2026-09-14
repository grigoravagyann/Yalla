using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

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
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var listing = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["wifi", "vegan"], listing.GetProperty("amenities").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(second, listing.GetProperty("gallery")[0].GetProperty("photoId").GetGuid());

        // Moving the pin is the owner's call (K5), so the owner saves the same form with the location.
        PanelAccount owner;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            owner = await AuthTestData.SeedOwnerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var moved = await ownerClient.PutAsJsonAsync($"/api/branches/{mine.BranchId}/listing", new
        {
            cuisine,
            about = "A bright all-day cafe.",
            priceLevel = 2,
            websiteUrl = "https://thegreentable.example",
            amenities = new[] { "wifi", "vegan" },
            address = "3 Test Street, Yerevan",
            latitude = 40.1843,
            longitude = 44.5129,
        });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

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
        var aniAccount = await SignInDinerAsync(factory);
        var narekAccount = await SignInDinerAsync(factory);
        using var ani = factory.CreateClientWithToken(aniAccount.AccessToken);
        using var narek = factory.CreateClientWithToken(narekAccount.AccessToken);
        using var anyone = factory.CreateClient();

        // Both sat at one of the branch's tables: a first review needs a visit (K8).
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(
                db, mine, factory.Clock.UtcNow, aniAccount.DinerUserId, narekAccount.DinerUserId);
        }

        // The diner opens the place first, which fills the fifteen-second listing cache with no reviews.
        var before = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}");
        Assert.Equal(0, before.GetProperty("listing").GetProperty("reviewCount").GetInt32());

        var created = await ani.PostAsJsonAsync(route, new { rating = 5, text = " Warm lavash. " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Warm lavash.", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());

        // Back on the details screen at once: the card agrees with the review drawn beneath it,
        // rather than reading "no reviews yet" from the cache above that very review.
        var after = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}");
        Assert.Equal(1, after.GetProperty("recentReviews").GetArrayLength());
        Assert.Equal(1, after.GetProperty("listing").GetProperty("reviewCount").GetInt32());
        Assert.Equal(5.0, after.GetProperty("listing").GetProperty("rating").GetDouble());

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

        // A page whose offset would overflow int is refused, not sent to SQL Server as a negative
        // OFFSET and answered with a 500. The last page that fits is just empty.
        var last = await anyone.GetAsync($"/api/public/branches/{mine.BranchId}/reviews?page={PublicListingQuery.MaxReviewPage}");
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        Assert.Equal(0, (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reviews").GetArrayLength());

        foreach (var overflowing in new[] { PublicListingQuery.MaxReviewPage + 1, int.MaxValue })
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await anyone.GetAsync($"/api/public/branches/{mine.BranchId}/reviews?page={overflowing}")).StatusCode);
        }
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
        // The positions are fractions of the cover photo, so there has to be one - and a second, to
        // change it to later.
        Guid cover;
        Guid newCover;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cover = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
            newCover = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        }

        var profile = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/public-profile");

        // The public-profile form as the console sends it: phone and switch as they are, and the cover.
        Task<HttpResponseMessage> SaveCoverAsync(Guid? photoId) =>
            manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/public-profile", new
            {
                phoneE164 = profile.TryGetProperty("phoneE164", out var phone) ? phone.GetString() : null,
                acceptsWebBookings = profile.GetProperty("acceptsWebBookings").GetBoolean(),
                coverPhotoId = photoId,
            });

        Assert.Equal(HttpStatusCode.OK, (await SaveCoverAsync(cover)).StatusCode);

        var plan = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/floor-plan");
        var firstId = plan.GetProperty("tables")[0].GetProperty("id").GetGuid();

        // Pins have their own route (K7), saved against the cover they were placed on.
        Task<HttpResponseMessage> PlaceFirstAsync(Guid coverPhotoId, double? photoX, double? photoY) =>
            manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/table-photo-positions", new
            {
                coverPhotoId,
                positions = new[] { new { tableId = firstId, photoX, photoY } },
            });

        var saved = await PlaceFirstAsync(cover, 0.25, 0.5);
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
        var half = await PlaceFirstAsync(cover, 0.4, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, half.StatusCode);

        var markersUrl = $"/api/public/branches/{mine.BranchId}/table-markers";

        // Saving the profile with the same cover - the form re-sends it on every save - keeps the pin.
        Assert.Equal(HttpStatusCode.OK, (await SaveCoverAsync(cover)).StatusCode);
        Assert.Single((await anyone.GetFromJsonAsync<JsonElement>(markersUrl)).GetProperty("tables").EnumerateArray());

        // A different picture: the pin described the old one, so it comes off, in the database too.
        Assert.Equal(HttpStatusCode.OK, (await SaveCoverAsync(newCover)).StatusCode);
        var moved = await anyone.GetFromJsonAsync<JsonElement>(markersUrl);
        Assert.Equal(newCover, moved.GetProperty("photo").GetProperty("photoId").GetGuid());
        Assert.Empty(moved.GetProperty("tables").EnumerateArray());
        Assert.Empty((await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{mine.BranchId}"))
            .GetProperty("tableMarkers").EnumerateArray());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.DiningTables.AnyAsync(t => t.BranchId == mine.BranchId && t.PhotoX != null));
        }

        // No cover at all: no markers, and no pin can be placed on a picture that is gone.
        Assert.Equal(HttpStatusCode.OK, (await SaveCoverAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceFirstAsync(newCover, 0.3, 0.6)).StatusCode);

        var uncovered = await anyone.GetFromJsonAsync<JsonElement>(markersUrl);
        Assert.False(uncovered.TryGetProperty("photo", out var photo) && photo.ValueKind != JsonValueKind.Null);
        Assert.Empty(uncovered.GetProperty("tables").EnumerateArray());
    }

    // ------------------------------------------------------------ orders

    [SkippableFact]
    public async Task A_diner_sees_their_own_and_their_tables_orders_with_the_kitchen_timeline_and_nobody_elses()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var ani = await SignInDinerAsync(factory);
        var stranger = await SignInDinerAsync(factory);
        var hiddenFrom = await SignInDinerAsync(factory);
        var removed = await SignInDinerAsync(factory);
        var pending = await SignInDinerAsync(factory);

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

            // On the same tab, and none of them may see the table's bill: the host hid the total
            // from this guest, this one was approved and then taken off, and this one was never let on.
            var hidden = TabParticipant.Guest(tab.TabId, "Hidden", $"device-{Guid.NewGuid():N}", now, true, hiddenFrom.DinerUserId);
            hidden.Approve(now);
            db.TabParticipants.Add(hidden);

            var takenOff = TabParticipant.Guest(tab.TabId, "Wrong table", $"device-{Guid.NewGuid():N}", now, false, removed.DinerUserId);
            takenOff.Approve(now);
            takenOff.Remove(now);
            db.TabParticipants.Add(takenOff);

            db.TabParticipants.Add(
                TabParticipant.Guest(tab.TabId, "Waiting", $"device-{Guid.NewGuid():N}", now, false, pending.DinerUserId));

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

        // The whole-table order is the table's bill, and the tab view shows it only to somebody who
        // may see the table total. Neither route here may show more than that.
        foreach (var (who, token) in new[]
                 {
                     ("hidden from", hiddenFrom.AccessToken),
                     ("removed", removed.AccessToken),
                     ("pending", pending.AccessToken),
                 })
        {
            using var client = factory.CreateClientWithToken(token);
            Assert.True(
                (await client.GetFromJsonAsync<JsonElement>("/api/diner/orders")).GetArrayLength() == 0,
                $"a {who} guest must not get the table's orders");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/diner/orders/{tableOrderId}")).StatusCode);
        }

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
