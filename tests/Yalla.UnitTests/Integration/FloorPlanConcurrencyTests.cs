using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K6: the floor plan carries a version, a stale save is refused - and the rooms, their photos and
/// every pin ride on the same save, so an editor echoing back what it loaded changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// The plan is replaced whole. Two managers editing in two tabs both loaded the same revision, and
/// without a version the second save quietly threw away the first - tables somebody had just drawn,
/// gone, with a 200 for both.
/// </para>
/// <para>
/// Pins are part of the plan now: a table is pinned on one of its room's photos, and the rooms and
/// their photos are on this form. There is no second writer of pins to keep out of its way.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class FloorPlanConcurrencyTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_save_against_a_version_somebody_already_replaced_is_refused_and_the_first_save_stands()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var mine = await SeedAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var url = $"/api/branches/{mine.BranchId}/floor-plan";

        // Two editors open the plan.
        var first = await manager.GetFromJsonAsync<JsonElement>(url);
        var second = await manager.GetFromJsonAsync<JsonElement>(url);
        var version = first.GetProperty("version").GetString()!;
        Assert.Equal(version, second.GetProperty("version").GetString());

        var tableId = first.GetProperty("tables")[0].GetProperty("id").GetGuid();

        var saveA = await manager.PutAsJsonAsync(url, FloorPlanBody.From(first, version, SetSeats(tableId, seats: 6)));
        Assert.Equal(HttpStatusCode.OK, saveA.StatusCode);
        var current = (await saveA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("version").GetString();
        Assert.NotEqual(version, current);

        var saveB = await manager.PutAsJsonAsync(url, FloorPlanBody.From(second, version, SetSeats(tableId, seats: 8)));
        Assert.Equal(HttpStatusCode.Conflict, saveB.StatusCode);

        var problem = await saveB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("floor-plan-changed", problem.GetProperty("code").GetString());
        Assert.Equal(current, problem.GetProperty("context").GetProperty("currentVersion").GetString());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(6, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).Seats);
        }

        // Reloaded, the second editor's change goes through.
        var reloaded = await manager.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(current, reloaded.GetProperty("version").GetString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(url, FloorPlanBody.From(reloaded, current, SetSeats(tableId, seats: 8)))).StatusCode);
    }

    [SkippableFact]
    public async Task A_save_without_an_expected_version_is_refused_naming_the_field()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var mine = await SeedAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var url = $"/api/branches/{mine.BranchId}/floor-plan";

        var plan = await manager.GetFromJsonAsync<JsonElement>(url);
        var refused = await manager.PutAsJsonAsync(url, FloorPlanBody.From(plan, expectedVersion: null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
        Assert.Contains(
            problem.GetProperty("context").GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("field").GetString() == "expectedVersion" && f.GetProperty("bound").GetString() == "required");

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == mine.BranchId)).FloorPlanVersion);
    }

    /// <summary>
    /// Two saves from the same revision at the same instant, over two connections: the branch lock
    /// lets one in, and the other sees the version it moved.
    /// </summary>
    [SkippableFact]
    public async Task Two_saves_racing_from_the_same_version_let_exactly_one_through()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        TestBranch branch;
        FloorPlanView plan;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            plan = await fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId))
                .GetFloorPlanAsync(branch.BranchId);
        }

        async Task<Exception?> SaveAsync(int seats)
        {
            await using var db = fixture.CreateContext(clock);
            var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

            var tables = plan.Tables
                .Select((t, i) => new FloorTableInput(t.Id, t.Label, i == 0 ? seats : t.Seats, null, t.IsBookable))
                .ToList();

            try
            {
                await service.ReplaceFloorPlanAsync(
                    branch.BranchId, new ReplaceFloorPlanCommand([], tables, plan.Version));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var outcomes = await Task.WhenAll(SaveAsync(5), SaveAsync(7));

        Assert.Single(outcomes, o => o is null);
        Assert.Single(outcomes, o => o is FloorPlanChangedException);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, (await verify.Branches.AsNoTracking().SingleAsync(b => b.Id == branch.BranchId)).FloorPlanVersion);
    }

    /// <summary>
    /// The rooms editor end to end over HTTP: a room gets a photo, tables are pinned on it, the
    /// public rooms read shows them, and echoing the loaded plan back changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task Rooms_photos_and_pins_round_trip_through_the_plan_and_show_on_the_public_rooms_read()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var mine = await SeedAsync(factory, tableCount: 3);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var planUrl = $"/api/branches/{mine.BranchId}/floor-plan";

        Guid terracePhoto;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            terracePhoto = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        }

        var loaded = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        var t3 = loaded.GetProperty("tables")[2].GetProperty("id").GetGuid();

        // A second room with its own photo, and table 3 moved into it and pinned there.
        var body = FloorPlanBody.From(loaded, loaded.GetProperty("version").GetString(), table =>
        {
            if ((Guid)table["id"]! == t3)
            {
                table["floorAreaName"] = "Terrace";
                table["photoId"] = terracePhoto;
                table["photoX"] = 0.25;
                table["photoY"] = 0.75;
            }
        });

        ((List<Dictionary<string, object?>>)body["areas"]!).Add(new Dictionary<string, object?>
        {
            ["name"] = "Terrace",
            ["displayOrder"] = 1,
            ["photoIds"] = new[] { terracePhoto },
        });

        var saved = await manager.PutAsJsonAsync(planUrl, body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var plan = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        var terrace = plan.GetProperty("areas").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Terrace");
        Assert.Equal(terracePhoto, Assert.Single(terrace.GetProperty("photos").EnumerateArray()).GetProperty("photoId").GetGuid());

        var moved = plan.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == t3);
        Assert.Equal(terracePhoto, moved.GetProperty("photoId").GetGuid());
        Assert.Equal(0.25, moved.GetProperty("photoX").GetDouble());

        using var anyone = factory.CreateClient();
        var pinsBefore = await PinsAsync(anyone, mine.BranchId);
        Assert.Equal(3, pinsBefore.Count);
        Assert.Contains((t3, terracePhoto, 0.25, 0.75), pinsBefore);

        // The plan as loaded, sent straight back: every room, photo and pin stays.
        var current = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(planUrl, FloorPlanBody.From(current, current.GetProperty("version").GetString()))).StatusCode);
        Assert.Equal(pinsBefore, await PinsAsync(anyone, mine.BranchId));
    }

    /// <summary>The published schema: rooms carry photo ids, tables carry a pin and no geometry, the plan has a version.</summary>
    [Fact]
    public async Task The_published_floor_plan_schema_has_rooms_with_photos_pins_on_tables_and_no_geometry()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        JsonElement Schema(string name) =>
            schemas.EnumerateObject().Single(s => s.Name == name || s.Name.EndsWith("." + name, StringComparison.Ordinal)).Value;

        var tableInput = Schema(nameof(FloorTableInput)).GetProperty("properties");

        foreach (var pin in new[] { "photoId", "photoX", "photoY", "label", "floorAreaName" })
        {
            Assert.True(tableInput.TryGetProperty(pin, out _), $"FloorTableInput does not publish {pin}.");
        }

        foreach (var gone in new[] { "x", "y", "width", "height", "rotationDegrees", "shape" })
        {
            Assert.False(tableInput.TryGetProperty(gone, out _), $"FloorTableInput still publishes {gone}.");
        }

        Assert.True(Schema(nameof(FloorAreaInput)).GetProperty("properties").TryGetProperty("photoIds", out _));
        Assert.True(Schema(nameof(FloorAreaView)).GetProperty("properties").TryGetProperty("photos", out _));

        var command = Schema(nameof(ReplaceFloorPlanCommand)).GetProperty("properties");
        Assert.True(command.TryGetProperty("expectedVersion", out _));
        Assert.False(command.TryGetProperty("floorWidth", out _));
        Assert.True(Schema(nameof(FloorPlanView)).GetProperty("properties").TryGetProperty("version", out _));
    }

    // ------------------------------------------------------------ helpers

    private static Action<Dictionary<string, object?>> SetSeats(Guid tableId, int seats) =>
        table =>
        {
            if ((Guid)table["id"]! == tableId)
            {
                table["seats"] = seats;
            }
        };

    /// <summary>Every pin on the public rooms read, as comparable tuples.</summary>
    private static async Task<List<(Guid TableId, Guid PhotoId, double X, double Y)>> PinsAsync(HttpClient anyone, Guid branchId)
    {
        var rooms = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}/rooms");

        return rooms.GetProperty("rooms").EnumerateArray()
            .SelectMany(r => r.GetProperty("photos").EnumerateArray())
            .SelectMany(p => p.GetProperty("tables").EnumerateArray().Select(t => (
                t.GetProperty("tableId").GetGuid(),
                p.GetProperty("photo").GetProperty("photoId").GetGuid(),
                t.GetProperty("photoX").GetDouble(),
                t.GetProperty("photoY").GetDouble())))
            .OrderBy(t => t.Item1)
            .ToList();
    }

    private async Task<AuthBranch> SeedAsync(YallaApiFactory factory, int tableCount = 2)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await AuthTestData.CreateBranchAsync(db, tableCount);
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}

/// <summary>
/// A floor-plan PUT body echoing a loaded plan, the way the editor sends one back.
/// </summary>
/// <remarks>
/// Dictionaries rather than anonymous objects, so a test can change one table's pin or room, add a
/// room, or leave <c>expectedVersion</c> null, which is what those tests are about.
/// </remarks>
internal static class FloorPlanBody
{
    public static Dictionary<string, object?> From(
        JsonElement plan,
        string? expectedVersion,
        Action<Dictionary<string, object?>>? eachTable = null)
    {
        var areaNames = plan.GetProperty("areas").EnumerateArray()
            .ToDictionary(a => a.GetProperty("id").GetGuid(), a => a.GetProperty("name").GetString());

        var tables = plan.GetProperty("tables").EnumerateArray()
            .Select(t =>
            {
                var table = new Dictionary<string, object?>
                {
                    ["id"] = t.GetProperty("id").GetGuid(),
                    ["label"] = t.GetProperty("label").GetString(),
                    ["seats"] = t.GetProperty("seats").GetInt32(),
                    ["floorAreaName"] = t.TryGetProperty("floorAreaId", out var area) && area.ValueKind == JsonValueKind.String
                        ? areaNames[area.GetGuid()]
                        : null,
                    ["isBookable"] = t.GetProperty("isBookable").GetBoolean(),
                    ["photoId"] = Nullable(t, "photoId", p => p.GetGuid()),
                    ["photoX"] = Nullable(t, "photoX", p => p.GetDouble()),
                    ["photoY"] = Nullable(t, "photoY", p => p.GetDouble()),
                };

                eachTable?.Invoke(table);

                return table;
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["areas"] = plan.GetProperty("areas").EnumerateArray()
                .Select(a => new Dictionary<string, object?>
                {
                    ["id"] = a.GetProperty("id").GetGuid(),
                    ["name"] = a.GetProperty("name").GetString(),
                    ["displayOrder"] = a.GetProperty("displayOrder").GetInt32(),
                    ["photoIds"] = a.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("photoId").GetGuid()).ToArray(),
                })
                .ToList(),
            ["tables"] = tables,
            ["expectedVersion"] = expectedVersion,
        };
    }

    private static object? Nullable(JsonElement table, string name, Func<JsonElement, object> read) =>
        table.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? read(value) : null;
}
