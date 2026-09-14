using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K4: a manager whose account names a home branch manages that branch, not the venue.
/// </summary>
/// <remarks>
/// <para>
/// Until now <c>BranchScoped</c> widened every admin-panel manager to the whole venue, and only the
/// console's branch list (and the booking list) narrowed them back - so a manager of branch A could
/// rewrite branch B's listing, floor plan, cover and pins by typing its id. The refusal now happens
/// twice: in the handler from the token's branch claim, and in the services from the stored row.
/// </para>
/// <para>
/// The venue is the one <c>AuthTestData</c> builds: branch A with its manager, branch B added beside
/// it. Every branch route is exercised against B (refused) and A (admitted), so a route that forgot
/// either layer shows up by name.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchScopeEnforcementTests(SqlServerFixture fixture) : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-branch-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_manager_with_a_home_branch_is_refused_every_branch_route_on_a_sibling_branch_and_admitted_at_home()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var venue = await SeedAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, venue.Home));

        foreach (var (route, status) in await EveryBranchRouteAsync(manager, venue.OtherBranchId, venue.OtherCover))
        {
            Assert.True(status == HttpStatusCode.Forbidden, $"{route} on the sibling branch answered {(int)status}, not 403.");
        }

        foreach (var (route, status) in await EveryBranchRouteAsync(manager, venue.Home.BranchId, venue.HomeCover))
        {
            Assert.True(
                status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"{route} on the manager's own branch answered {(int)status}.");
        }

        // Nothing on the sibling branch moved.
        await using var db = fixture.CreateContext(factory.Clock);
        var other = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == venue.OtherBranchId);
        Assert.Null(other.Cuisine);
        Assert.Null(other.CoverPhotoId);
        Assert.Equal(0, other.FloorPlanVersion);
        Assert.False(await db.Photos.AnyAsync(p => p.BranchId == venue.OtherBranchId && p.Id != venue.OtherCover));
    }

    [SkippableFact]
    public async Task The_owner_and_a_platform_admin_are_admitted_on_both_branches()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var venue = await SeedAsync(factory);

        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, venue.Owner));
        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, venue.Admin));

        foreach (var (who, client) in new[] { ("owner", owner), ("platform admin", platform) })
        {
            foreach (var (branchId, cover) in new[] { (venue.Home.BranchId, venue.HomeCover), (venue.OtherBranchId, venue.OtherCover) })
            {
                foreach (var (route, status) in await EveryBranchRouteAsync(client, branchId, cover))
                {
                    Assert.True(
                        status is HttpStatusCode.OK or HttpStatusCode.Created,
                        $"{route} for the {who} on branch {branchId} answered {(int)status}.");
                }
            }
        }
    }

    [SkippableFact]
    public async Task A_manager_moved_to_another_branch_is_refused_at_the_old_one_before_their_token_expires()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var venue = await SeedAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, venue.Home));
        var home = venue.Home.BranchId;

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/branches/{home}/listing")).StatusCode);

        // Reassigned in the database. No new token: the one in hand still names branch A.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.StaffMembers
                .Where(s => s.Id == venue.Home.ManagerId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.BranchId, (Guid?)venue.OtherBranchId));
        }

        // The route policy still lets the token through - the readiness read has no service check, so
        // it proves that - and every service underneath refuses from the stored row.
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/branches/{home}/readiness")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/branches/{home}/listing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/branches/{home}/floor-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/branches/{home}/public-profile")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PutAsJsonAsync($"/api/branches/{home}/listing", new { cuisine = "Not any more" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(manager, home, Png(9))).StatusCode);
    }

    [SkippableFact]
    public async Task A_manager_on_a_tablet_PIN_session_is_confined_to_the_tablets_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var venue = await SeedAsync(factory);

        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, venue.Home);
        using var tablet = factory.CreateClientWithToken(deviceToken);

        var pin = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = venue.Home.ManagerId, pin = "9182" });
        pin.EnsureSuccessStatusCode();

        using var session = factory.CreateClientWithToken(
            (await pin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync($"/api/branches/{venue.Home.BranchId}/floor-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync($"/api/branches/{venue.OtherBranchId}/floor-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync($"/api/branches/{venue.OtherBranchId}/listing")).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private sealed record SeededVenue(
        AuthBranch Home,
        Guid OtherBranchId,
        Guid HomeCover,
        Guid OtherCover,
        PanelAccount Owner,
        PlatformAdminAccount Admin);

    private async Task<SeededVenue> SeedAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        // The fixture manager's row names branch A.
        var home = await AuthTestData.CreateBranchAsync(db);
        var other = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 2);

        return new SeededVenue(
            home,
            other,
            await TestMenuBuilder.AddPhotoAsync(db, home.BranchId),
            await TestMenuBuilder.AddPhotoAsync(db, other),
            await AuthTestData.SeedOwnerAsync(db, home.VenueId),
            await AuthTestData.CreatePlatformAdminAsync(db));
    }

    /// <summary>
    /// Every branch-scoped route this package guards, in an order that works on a real branch: the
    /// cover is set before the pins that need it.
    /// </summary>
    private static async Task<List<(string Route, HttpStatusCode Status)>> EveryBranchRouteAsync(
        HttpClient client, Guid branchId, Guid coverPhotoId)
    {
        var results = new List<(string, HttpStatusCode)>();
        var prefix = $"/api/branches/{branchId}";

        results.Add(("GET /listing", (await client.GetAsync($"{prefix}/listing")).StatusCode));

        results.Add(("PUT /listing", (await client.PutAsJsonAsync(
            $"/api/branches/{branchId}/listing", new { cuisine = "Scope test" })).StatusCode));

        var planResponse = await client.GetAsync($"{prefix}/floor-plan");
        results.Add(("GET /floor-plan", planResponse.StatusCode));

        Guid tableId;
        Dictionary<string, object?> planBody;

        if (planResponse.IsSuccessStatusCode)
        {
            var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
            tableId = plan.GetProperty("tables")[0].GetProperty("id").GetGuid();
            planBody = FloorPlanBody.From(plan, plan.GetProperty("version").GetString());
        }
        else
        {
            // Refused anyway; the body only has to be well formed.
            tableId = Guid.CreateVersion7();
            planBody = new Dictionary<string, object?>
            {
                ["floorWidth"] = 1000,
                ["floorHeight"] = 700,
                ["areas"] = Array.Empty<object>(),
                ["tables"] = Array.Empty<object>(),
                ["expectedVersion"] = "0",
            };
        }

        results.Add(("PUT /floor-plan", (await client.PutAsJsonAsync($"{prefix}/floor-plan", planBody)).StatusCode));

        results.Add(("PUT /public-profile", (await client.PutAsJsonAsync(
            $"/api/branches/{branchId}/public-profile",
            new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId })).StatusCode));

        results.Add(("PUT /table-photo-positions", (await client.PutAsJsonAsync(
            $"/api/branches/{branchId}/table-photo-positions",
            new
            {
                coverPhotoId,
                positions = new[] { new { tableId, photoX = 0.5, photoY = 0.5 } },
            })).StatusCode));

        results.Add(("POST /photos", (await UploadAsync(client, branchId, Png((byte)Random.Shared.Next(256)))).StatusCode));

        // Policy only - no service check underneath - so this row is the handler's alone.
        results.Add(("GET /readiness", (await client.GetAsync($"{prefix}/readiness")).StatusCode));

        return results;
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid branchId, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "scope.png");

        return await client.PostAsync($"/api/branches/{branchId}/photos", form);
    }

    /// <summary>A small PNG, different per seed.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(48, 48);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), (byte)Random.Shared.Next(256)));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
