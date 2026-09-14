using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K6: the floor plan carries a version, a stale save is refused, and a plan save never touches pins.
/// </summary>
/// <remarks>
/// <para>
/// The plan is replaced whole. Two managers editing in two tabs both loaded the same revision, and
/// without a version the second save quietly threw away the first - tables somebody had just drawn,
/// gone, with a 200 for both.
/// </para>
/// <para>
/// And table pins used to ride on this form, so any editor that did not know about them - or an
/// older console - took every pin off the cover photo on its next save. Pins now have their own route
/// (K7); what this form sends about them is ignored.
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

        var saveA = await manager.PutAsJsonAsync(url, FloorPlanBody.From(first, version, MoveTable(tableId, x: 200)));
        Assert.Equal(HttpStatusCode.OK, saveA.StatusCode);
        var current = (await saveA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("version").GetString();
        Assert.NotEqual(version, current);

        var saveB = await manager.PutAsJsonAsync(url, FloorPlanBody.From(second, version, MoveTable(tableId, x: 400)));
        Assert.Equal(HttpStatusCode.Conflict, saveB.StatusCode);

        var problem = await saveB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("floor-plan-changed", problem.GetProperty("code").GetString());
        Assert.Equal(current, problem.GetProperty("context").GetProperty("currentVersion").GetString());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(200, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).X);
        }

        // Reloaded, the second editor's change goes through.
        var reloaded = await manager.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(current, reloaded.GetProperty("version").GetString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(url, FloorPlanBody.From(reloaded, current, MoveTable(tableId, x: 400)))).StatusCode);
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

        async Task<Exception?> SaveAsync(int x)
        {
            await using var db = fixture.CreateContext(clock);
            var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

            var tables = plan.Tables
                .Select((t, i) => new FloorTableInput(
                    t.Id, t.Label, t.Seats, i == 0 ? x : t.X, t.Y, t.Width, t.Height, t.RotationDegrees, t.Shape,
                    null, t.IsBookable))
                .ToList();

            try
            {
                await service.ReplaceFloorPlanAsync(
                    branch.BranchId, new ReplaceFloorPlanCommand(plan.FloorWidth, plan.FloorHeight, [], tables, plan.Version));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var outcomes = await Task.WhenAll(SaveAsync(120), SaveAsync(240));

        Assert.Single(outcomes, o => o is null);
        Assert.Single(outcomes, o => o is FloorPlanChangedException);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, (await verify.Branches.AsNoTracking().SingleAsync(b => b.Id == branch.BranchId)).FloorPlanVersion);
    }

    [SkippableFact]
    public async Task A_plan_save_leaves_every_pin_where_it_was_even_one_that_sends_photo_coordinates()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var mine = await SeedAsync(factory, tableCount: 3);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var planUrl = $"/api/branches/{mine.BranchId}/floor-plan";

        Guid cover;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cover = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(
                $"/api/branches/{mine.BranchId}/public-profile",
                new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId = cover })).StatusCode);

        var loaded = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        var t1 = loaded.GetProperty("tables")[0].GetProperty("id").GetGuid();
        var t2 = loaded.GetProperty("tables")[1].GetProperty("id").GetGuid();

        var pinned = await manager.PutAsJsonAsync($"/api/branches/{mine.BranchId}/table-photo-positions", new
        {
            coverPhotoId = cover,
            positions = new[]
            {
                new { tableId = t1, photoX = 0.2, photoY = 0.3 },
                new { tableId = t2, photoX = 0.7, photoY = 0.8 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

        using var anyone = factory.CreateClient();
        var markersBefore = await MarkersAsync(anyone, mine.BranchId);
        Assert.Equal(2, markersBefore.Count);

        // Pins and a cover save do not move the version: an open editor must not be refused for them.
        var afterPins = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        Assert.Equal(loaded.GetProperty("version").GetString(), afterPins.GetProperty("version").GetString());

        // A plan with no photo keys at all.
        var plain = await manager.PutAsJsonAsync(
            planUrl, FloorPlanBody.From(afterPins, afterPins.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(markersBefore, await MarkersAsync(anyone, mine.BranchId));

        // A plan that sends photo coordinates on every table - an older editor. Ignored.
        var current = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        var carrying = await manager.PutAsJsonAsync(planUrl, FloorPlanBody.From(
            current,
            current.GetProperty("version").GetString(),
            table =>
            {
                table["photoX"] = 0.9;
                table["photoY"] = 0.9;
            }));
        Assert.Equal(HttpStatusCode.OK, carrying.StatusCode);
        Assert.Equal(markersBefore, await MarkersAsync(anyone, mine.BranchId));

        // A table added by the plan has no pin, whatever it sends.
        current = await manager.GetFromJsonAsync<JsonElement>(planUrl);
        var withNewTable = FloorPlanBody.From(current, current.GetProperty("version").GetString());
        ((List<Dictionary<string, object?>>)withNewTable["tables"]!).Add(new Dictionary<string, object?>
        {
            ["label"] = "B2-new",
            ["seats"] = 2,
            ["x"] = 10,
            ["y"] = 10,
            ["width"] = 40,
            ["height"] = 40,
            ["rotationDegrees"] = 0d,
            ["shape"] = 1,
            ["isBookable"] = true,
            ["photoX"] = 0.5,
            ["photoY"] = 0.5,
        });

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(planUrl, withNewTable)).StatusCode);
        Assert.Equal(markersBefore, await MarkersAsync(anyone, mine.BranchId));

        await using var verify = fixture.CreateContext(factory.Clock);
        var added = await verify.DiningTables.AsNoTracking().SingleAsync(t => t.BranchId == mine.BranchId && t.Label == "B2-new");
        Assert.Null(added.PhotoX);
        Assert.Null(added.PhotoY);
    }

    /// <summary>The published schema: the table input has no photo position; the plan has a version.</summary>
    [Fact]
    public async Task The_published_floor_plan_schema_has_a_version_and_no_photo_position_on_the_table_input()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        JsonElement Schema(string name) =>
            schemas.EnumerateObject().Single(s => s.Name == name || s.Name.EndsWith("." + name, StringComparison.Ordinal)).Value;

        var tableInput = Schema(nameof(FloorTableInput)).GetProperty("properties");
        Assert.False(tableInput.TryGetProperty("photoX", out _), "FloorTableInput still publishes photoX.");
        Assert.False(tableInput.TryGetProperty("photoY", out _), "FloorTableInput still publishes photoY.");
        Assert.True(tableInput.TryGetProperty("label", out _));

        Assert.True(Schema(nameof(ReplaceFloorPlanCommand)).GetProperty("properties").TryGetProperty("expectedVersion", out _));
        Assert.True(Schema(nameof(FloorPlanView)).GetProperty("properties").TryGetProperty("version", out _));
    }

    // ------------------------------------------------------------ helpers

    private static Action<Dictionary<string, object?>> MoveTable(Guid tableId, int x) =>
        table =>
        {
            if ((Guid)table["id"]! == tableId)
            {
                table["x"] = x;
            }
        };

    /// <summary>The placed tables on the public marker read, as comparable tuples.</summary>
    private static async Task<List<(Guid TableId, double X, double Y)>> MarkersAsync(HttpClient anyone, Guid branchId)
    {
        var markers = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}/table-markers");

        return markers.GetProperty("tables").EnumerateArray()
            .Select(t => (t.GetProperty("tableId").GetGuid(), t.GetProperty("photoX").GetDouble(), t.GetProperty("photoY").GetDouble()))
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
/// Dictionaries rather than anonymous objects, so a test can add a key the contract does not declare
/// (<c>photoX</c> on a table) or leave <c>expectedVersion</c> null, which is what those tests are about.
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
                    ["x"] = t.GetProperty("x").GetInt32(),
                    ["y"] = t.GetProperty("y").GetInt32(),
                    ["width"] = t.GetProperty("width").GetInt32(),
                    ["height"] = t.GetProperty("height").GetInt32(),
                    ["rotationDegrees"] = t.GetProperty("rotationDegrees").GetDouble(),
                    ["shape"] = t.GetProperty("shape").GetInt32(),
                    ["floorAreaName"] = t.TryGetProperty("floorAreaId", out var area) && area.ValueKind == JsonValueKind.String
                        ? areaNames[area.GetGuid()]
                        : null,
                    ["isBookable"] = t.GetProperty("isBookable").GetBoolean(),
                };

                eachTable?.Invoke(table);

                return table;
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["floorWidth"] = plan.GetProperty("floorWidth").GetInt32(),
            ["floorHeight"] = plan.GetProperty("floorHeight").GetInt32(),
            ["areas"] = plan.GetProperty("areas").EnumerateArray()
                .Select(a => new Dictionary<string, object?>
                {
                    ["id"] = a.GetProperty("id").GetGuid(),
                    ["name"] = a.GetProperty("name").GetString(),
                    ["displayOrder"] = a.GetProperty("displayOrder").GetInt32(),
                })
                .ToList(),
            ["tables"] = tables,
            ["expectedVersion"] = expectedVersion,
        };
    }
}
