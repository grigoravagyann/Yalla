using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K7: <c>PUT /api/branches/{branchId}/table-photo-positions</c>, the pins' own route.
/// </summary>
/// <remarks>
/// Only the listed tables move, only on the photo, only on the cover they were placed on - and a cover
/// change through the public profile still takes every pin off in the same save.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TablePhotoPositionsTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Only_the_listed_tables_move_on_the_photo_and_nothing_else_about_the_plan_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory, tableCount: 3);
        using var manager = await ManagerAsync(factory, seeded);
        var branchId = seeded.Branch.BranchId;

        var before = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branchId}/floor-plan");
        var ids = before.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToArray();

        var placed = await PlaceAsync(manager, branchId, seeded.Cover, (ids[0], 0.42, 0.61), (ids[1], 0.1, 0.2));
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);

        var body = await placed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seeded.Cover, body.GetProperty("coverPhotoId").GetGuid());
        var tables = body.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("tableId").GetGuid());
        Assert.Equal(3, tables.Count);
        Assert.Equal(0.42, tables[ids[0]].GetProperty("photoX").GetDouble());
        Assert.Equal(0.61, tables[ids[0]].GetProperty("photoY").GetDouble());
        Assert.False(tables[ids[2]].TryGetProperty("photoX", out _), "an unlisted, unplaced table has no position");
        Assert.True(tables[ids[2]].TryGetProperty("label", out _));

        // Both null takes a table off; the others stay where they were.
        var off = await PlaceAsync(manager, branchId, seeded.Cover, (ids[1], null, null));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        using var anyone = factory.CreateClient();
        var markers = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}/table-markers");
        var marker = Assert.Single(markers.GetProperty("tables").EnumerateArray());
        Assert.Equal(ids[0], marker.GetProperty("tableId").GetGuid());
        Assert.Equal(0.42, marker.GetProperty("photoX").GetDouble());

        // The floor plan, ignoring the photo fields, is exactly as it was - version included.
        var after = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branchId}/floor-plan");
        Assert.Equal(WithoutPhotoFields(before), WithoutPhotoFields(after));
    }

    [SkippableFact]
    public async Task Positions_placed_on_a_cover_the_branch_no_longer_has_are_refused_and_nothing_is_written()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);
        using var manager = await ManagerAsync(factory, seeded);
        var branchId = seeded.Branch.BranchId;
        var table = seeded.Branch.FirstTableId;

        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(manager, branchId, seeded.Cover, (table, 0.5, 0.5))).StatusCode);

        var stale = await PlaceAsync(manager, branchId, seeded.OtherPhoto, (table, 0.9, 0.9));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cover-changed", problem.GetProperty("code").GetString());
        Assert.Equal(seeded.Cover, problem.GetProperty("context").GetProperty("currentCoverPhotoId").GetGuid());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(0.5, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == table)).PhotoX);
        }

        // No cover at all: every save is refused, and the context says there is none.
        Assert.Equal(HttpStatusCode.OK, (await SetCoverAsync(manager, branchId, null)).StatusCode);

        var uncovered = await PlaceAsync(manager, branchId, seeded.Cover, (table, 0.3, 0.3));
        Assert.Equal(HttpStatusCode.Conflict, uncovered.StatusCode);
        var none = await uncovered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cover-changed", none.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("context").GetProperty("currentCoverPhotoId").ValueKind);

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.DiningTables.AnyAsync(t => t.BranchId == branchId && t.PhotoX != null));
    }

    [SkippableFact]
    public async Task A_table_that_is_not_active_here_is_not_found_and_nothing_in_the_save_is_written()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);
        using var manager = await ManagerAsync(factory, seeded);
        var branchId = seeded.Branch.BranchId;

        AuthBranch theirs;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            theirs = await AuthTestData.CreateBranchAsync(db);

            await db.DiningTables
                .Where(t => t.Id == seeded.Branch.TableIds[1])
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.IsActive, false));
        }

        var foreign = await PlaceAsync(
            manager, branchId, seeded.Cover, (seeded.Branch.FirstTableId, 0.5, 0.5), (theirs.FirstTableId, 0.5, 0.5));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var inactive = await PlaceAsync(
            manager, branchId, seeded.Cover, (seeded.Branch.FirstTableId, 0.5, 0.5), (seeded.Branch.TableIds[1], 0.5, 0.5));
        Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.DiningTables.AnyAsync(t => (t.BranchId == branchId || t.BranchId == theirs.BranchId) && t.PhotoX != null));
    }

    [SkippableFact]
    public async Task Bad_positions_are_refused_naming_each_field()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);
        using var manager = await ManagerAsync(factory, seeded);
        var branchId = seeded.Branch.BranchId;
        var table = seeded.Branch.FirstTableId;
        var url = $"/api/branches/{branchId}/table-photo-positions";

        var half = await manager.PutAsJsonAsync(url, new
        {
            coverPhotoId = seeded.Cover,
            positions = new[] { new { tableId = table, photoX = 0.4 } },
        });
        await AssertNamesAsync(half, "positions[0].photoY", "required");

        var outside = await PlaceAsync(manager, branchId, seeded.Cover, (table, 1.5, 0.5));
        await AssertNamesAsync(outside, "positions[0].photoX", "range");

        var twice = await PlaceAsync(manager, branchId, seeded.Cover, (table, 0.1, 0.1), (table, 0.2, 0.2));
        await AssertNamesAsync(twice, "positions", "conflict");

        var noCover = await manager.PutAsJsonAsync(url, new
        {
            positions = new[] { new { tableId = table, photoX = 0.4, photoY = 0.4 } },
        });
        await AssertNamesAsync(noCover, "coverPhotoId", "required");

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.DiningTables.AnyAsync(t => t.BranchId == branchId && t.PhotoX != null));
    }

    [SkippableFact]
    public async Task A_cover_change_takes_every_pin_off_in_the_same_save_and_leaves_the_plan_version_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);
        using var manager = await ManagerAsync(factory, seeded);
        var branchId = seeded.Branch.BranchId;
        var ids = seeded.Branch.TableIds;

        Assert.Equal(
            HttpStatusCode.OK,
            (await PlaceAsync(manager, branchId, seeded.Cover, (ids[0], 0.25, 0.25), (ids[1], 0.75, 0.75))).StatusCode);

        var version = (await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branchId}/floor-plan"))
            .GetProperty("version").GetString();

        Assert.Equal(HttpStatusCode.OK, (await SetCoverAsync(manager, branchId, seeded.OtherPhoto)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.DiningTables.AnyAsync(t => t.BranchId == branchId && (t.PhotoX != null || t.PhotoY != null)));
        }

        using var anyone = factory.CreateClient();
        var markers = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}/table-markers");
        Assert.Equal(seeded.OtherPhoto, markers.GetProperty("photo").GetProperty("photoId").GetGuid());
        Assert.Empty(markers.GetProperty("tables").EnumerateArray());

        Assert.Equal(
            version,
            (await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branchId}/floor-plan")).GetProperty("version").GetString());

        // Placed again on the new cover.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(manager, branchId, seeded.OtherPhoto, (ids[0], 0.6, 0.4))).StatusCode);
    }

    [SkippableFact]
    public async Task A_waiter_or_another_venues_manager_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);
        var branchId = seeded.Branch.BranchId;

        AuthBranch theirs;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, seeded.Branch));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));

        foreach (var client in new[] { waiter, neighbour })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await PlaceAsync(client, branchId, seeded.Cover, (seeded.Branch.FirstTableId, 0.5, 0.5))).StatusCode);
        }
    }

    // ------------------------------------------------------------ helpers

    private sealed record Seeded(AuthBranch Branch, Guid Cover, Guid OtherPhoto);

    /// <summary>A branch with its cover set, and a second photo of the same branch to change it to.</summary>
    private async Task<Seeded> SeedAsync(YallaApiFactory factory, int tableCount = 2)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db, tableCount);

        return new Seeded(
            branch,
            await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId),
            await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId));
    }

    private static async Task<HttpClient> ManagerAsync(YallaApiFactory factory, Seeded seeded)
    {
        var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, seeded.Branch));

        var cover = await SetCoverAsync(manager, seeded.Branch.BranchId, seeded.Cover);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);

        return manager;
    }

    private static Task<HttpResponseMessage> SetCoverAsync(HttpClient client, Guid branchId, Guid? coverPhotoId) =>
        client.PutAsJsonAsync(
            $"/api/branches/{branchId}/public-profile",
            new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId });

    private static Task<HttpResponseMessage> PlaceAsync(
        HttpClient client,
        Guid branchId,
        Guid? coverPhotoId,
        params (Guid TableId, double? X, double? Y)[] positions) =>
        client.PutAsJsonAsync($"/api/branches/{branchId}/table-photo-positions", new
        {
            coverPhotoId,
            positions = positions.Select(p => new { tableId = p.TableId, photoX = p.X, photoY = p.Y }).ToArray(),
        });

    private static async Task AssertNamesAsync(HttpResponseMessage response, string field, string bound)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
        Assert.Contains(
            problem.GetProperty("context").GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("field").GetString() == field && f.GetProperty("bound").GetString() == bound);
    }

    /// <summary>The floor-plan read with each table's photo position removed, as comparable text.</summary>
    private static string WithoutPhotoFields(JsonElement plan)
    {
        var node = JsonNode.Parse(plan.GetRawText())!;

        foreach (var table in node["tables"]!.AsArray())
        {
            table!.AsObject().Remove("photoX");
            table.AsObject().Remove("photoY");
        }

        return node.ToJsonString();
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
