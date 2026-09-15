using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K11: favourites kept on the account - hearting, the list with the public card, the merge at sign-in,
/// the limit, and one diner's hearts being nobody else's business.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DinerFavoriteTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_diner_hearts_places_once_each_lists_them_newest_first_and_takes_hearts_off_idempotently()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var diner = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);

        AuthBranch home;
        Guid sibling;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, home.BranchId)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, sibling)).StatusCode);

        // Again: still the same place, once.
        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, home.BranchId)).StatusCode);

        var list = await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites");
        Assert.Equal(new[] { sibling, home.BranchId }, BranchIds(list));

        // Each entry carries the public card, and no distance without a position.
        var card = list.GetProperty("items")[0].GetProperty("listing");
        Assert.Equal(sibling, card.GetProperty("branchId").GetGuid());
        Assert.False(string.IsNullOrEmpty(card.GetProperty("venueName").GetString()));
        Assert.False(card.TryGetProperty("distanceKm", out _));

        var near = await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites?lat=40.1843&lng=44.5129");
        Assert.True(near.GetProperty("items")[0].GetProperty("listing").TryGetProperty("distanceKm", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await diner.GetAsync("/api/diner/favorites?lat=40.1843")).StatusCode);

        await ReviewIntegrityTests.AssertProblemAsync(
            await HeartAsync(diner, Guid.CreateVersion7()), HttpStatusCode.NotFound, "not-found");

        // Taking a heart off is idempotent too, including for a place that was never kept.
        Assert.Equal(HttpStatusCode.NoContent, (await diner.DeleteAsync($"/api/diner/favorites/{home.BranchId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await diner.DeleteAsync($"/api/diner/favorites/{home.BranchId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await diner.DeleteAsync($"/api/diner/favorites/{Guid.CreateVersion7()}")).StatusCode);
        Assert.Equal(new[] { sibling }, BranchIds(await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));

        // A place that closes drops out of the list and keeps its heart; a new heart for it is 404.
        await SetActiveAsync(factory, sibling, active: false);

        Assert.Empty(BranchIds(await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));
        await ReviewIntegrityTests.AssertProblemAsync(await HeartAsync(diner, sibling), HttpStatusCode.NotFound, "not-found");

        // And it comes back when the place reopens.
        await SetActiveAsync(factory, sibling, active: true);
        Assert.Equal(new[] { sibling }, BranchIds(await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));
    }

    [SkippableFact]
    public async Task Hearts_made_signed_out_merge_in_adding_what_is_missing_and_removing_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch home;
        Guid sibling;
        Guid closed;
        Guid later;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);
            closed = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", isActive: false, tableCount: 1);
            later = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, home.BranchId)).StatusCode);

        // A duplicate, an unknown place and a closed one: skipped, not refused.
        var merged = await diner.PutAsJsonAsync(
            "/api/diner/favorites",
            new { branchIds = new[] { sibling, home.BranchId, Guid.CreateVersion7(), closed, sibling } });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        var expected = new[] { home.BranchId, sibling }.Order().ToArray();
        Assert.Equal(expected, BranchIds(await merged.Content.ReadFromJsonAsync<JsonElement>()).Order().ToArray());

        // The same merge again, and an empty one, change nothing and remove nothing.
        var again = await diner.PutAsJsonAsync("/api/diner/favorites", new { branchIds = new[] { sibling } });
        Assert.Equal(expected, BranchIds(await again.Content.ReadFromJsonAsync<JsonElement>()).Order().ToArray());

        var empty = await diner.PutAsJsonAsync("/api/diner/favorites", new { branchIds = Array.Empty<Guid>() });
        Assert.Equal(expected, BranchIds(await empty.Content.ReadFromJsonAsync<JsonElement>()).Order().ToArray());

        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PutAsJsonAsync("/api/diner/favorites", new { }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "branchIds");

        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PutAsJsonAsync(
                "/api/diner/favorites",
                new { branchIds = Enumerable.Range(0, DinerFavorite.MaxPerDiner + 1).Select(_ => Guid.CreateVersion7()).ToArray() }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "branchIds");

        // A bad position is refused before anything is written.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await diner.PutAsJsonAsync("/api/diner/favorites?lat=40.18", new { branchIds = new[] { later } })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(2, await db.DinerFavorites.CountAsync(f => f.DinerUserId == dinerUserId));
            Assert.False(await db.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId && f.BranchId == later));
        }
    }

    [SkippableFact]
    public async Task Hearts_are_the_accounts_own_and_any_diner_account_may_keep_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var ani = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);

        // An account whose number was never proved: favourites do not need one.
        using var narek = factory.CreateClientWithToken((await DinerFeedTestData.RegisterUnprovedAsync(factory)).AccessToken);

        AuthBranch home;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(ani, home.BranchId)).StatusCode);

        // Narek sees none of Ani's, and taking "the" heart off touches only his own list.
        Assert.Empty(BranchIds(await narek.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));
        Assert.Equal(HttpStatusCode.NoContent, (await narek.DeleteAsync($"/api/diner/favorites/{home.BranchId}")).StatusCode);
        Assert.Equal(new[] { home.BranchId }, BranchIds(await ani.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));

        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(narek, home.BranchId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ani.DeleteAsync($"/api/diner/favorites/{home.BranchId}")).StatusCode);
        Assert.Equal(new[] { home.BranchId }, BranchIds(await narek.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));

        // Only a diner account.
        using var anyone = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.GetAsync("/api/diner/favorites")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartAsync(anyone, home.BranchId)).StatusCode);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, home));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/diner/favorites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await HeartAsync(manager, home.BranchId)).StatusCode);
    }

    [SkippableFact]
    public async Task An_account_keeps_at_most_500_and_nothing_past_that_is_written()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch home;
        Guid sibling;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);

            // 499 hearts for places that are no longer found: they count, and the list leaves them out.
            db.DinerFavorites.AddRange(
                Enumerable.Range(0, DinerFavorite.MaxPerDiner - 1)
                    .Select(_ => new DinerFavorite(dinerUserId, Guid.CreateVersion7(), factory.Clock.UtcNow.AddDays(-1))));

            await db.SaveChangesAsync();
        }

        // The five-hundredth fits.
        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, home.BranchId)).StatusCode);

        // The five-hundred-and-first does not, by either door, and a kept one can still be re-sent.
        await ReviewIntegrityTests.AssertProblemAsync(
            await HeartAsync(diner, sibling), HttpStatusCode.Conflict, "conflicting-state");
        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PutAsJsonAsync("/api/diner/favorites", new { branchIds = new[] { sibling, home.BranchId } }),
            HttpStatusCode.Conflict, "conflicting-state");
        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, home.BranchId)).StatusCode);

        Assert.Equal(new[] { home.BranchId }, BranchIds(await diner.GetFromJsonAsync<JsonElement>("/api/diner/favorites")));

        Guid oneOfThem;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId && f.BranchId == sibling));

            oneOfThem = await db.DinerFavorites
                .Where(f => f.DinerUserId == dinerUserId && f.BranchId != home.BranchId)
                .Select(f => f.BranchId)
                .FirstAsync();
        }

        // Room made, and the heart goes in.
        Assert.Equal(HttpStatusCode.NoContent, (await diner.DeleteAsync($"/api/diner/favorites/{oneOfThem}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await HeartAsync(diner, sibling)).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static Task<HttpResponseMessage> HeartAsync(HttpClient client, Guid branchId) =>
        client.PutAsync($"/api/diner/favorites/{branchId}", content: null);

    private static Guid[] BranchIds(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("branchId").GetGuid())];

    private async Task SetActiveAsync(YallaApiFactory factory, Guid branchId, bool active)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        await db.Branches
            .Where(b => b.Id == branchId)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.IsActive, active));
    }
}
