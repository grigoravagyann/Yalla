using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Who may call the venue console's branch routes, the review moderation routes and the diner's own
/// routes - every kind of token against every route, one theory row per route.
/// </summary>
/// <remarks>
/// <para>
/// Each row builds one world: a venue with the target branch and a sibling branch, a second venue, and a
/// signed-in caller of every kind. Then it sends the route once as each of them and compares every answer
/// with the matrix, reporting all the mismatches at once rather than stopping at the first - so a row that
/// fails says which callers got through, not just that somebody did.
/// </para>
/// <para>
/// The refused callers go first and the admitted ones last. That order matters for the routes that
/// change something: the admitted calls must each be valid on their own, and nothing a refused call sends
/// is allowed to have changed the state they run against.
/// </para>
/// <para>
/// The venue rows: anonymous 401; a tab participant, a diner, a waiter's and a cook's tablet session,
/// another venue's manager, and a manager of this venue whose home branch is the sibling all 403; the
/// branch's own manager, the owner and a platform admin admitted. The platform rows admit only the
/// platform admin. The diner rows admit only the diner.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class AuthorizationMatrixTests(SqlServerFixture fixture) : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-authz-matrix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private enum Caller
    {
        Anonymous,
        TabParticipant,
        Diner,
        WaiterSession,
        KitchenSession,
        OtherVenueManager,
        SiblingBranchManager,
        BranchManager,
        Owner,
        PlatformAdmin,
    }

    public static TheoryData<string> AdminRouteNames => Names(AdminRoutes);

    public static TheoryData<string> DinerRouteNames => Names(DinerRoutes);

    [SkippableTheory]
    [MemberData(nameof(AdminRouteNames))]
    public Task A_venue_or_platform_route_answers_every_caller_as_the_matrix_says(string route) =>
        RunAsync(route, AdminRoutes[route]);

    [SkippableTheory]
    [MemberData(nameof(DinerRouteNames))]
    public Task A_diner_route_admits_the_diner_and_nobody_else(string route) =>
        RunAsync(route, DinerRoutes[route]);

    // ------------------------------------------------------------ the routes

    private sealed record MatrixRoute(
        Func<World, HttpClient, Task<HttpResponseMessage>> Send,
        Func<Caller, HttpStatusCode> Expected,
        Func<SqlServerFixture, World, Task>? Arrange = null);

    private static readonly Dictionary<string, MatrixRoute> AdminRoutes = new(StringComparer.Ordinal)
    {
        ["GET /api/branches/{branchId}/listing"] = new(
            (w, c) => c.GetAsync($"/api/branches/{w.Branch.BranchId}/listing"),
            caller => Venue(caller, HttpStatusCode.OK)),

        // No location in the body, so it is not a relocation (K5) and the branch's manager may save it.
        ["PUT /api/branches/{branchId}/listing"] = new(
            (w, c) => c.PutAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/listing", new { cuisine = "Matrix kitchen", amenities = new[] { "wifi" } }),
            caller => Venue(caller, HttpStatusCode.OK)),

        ["PUT /api/branches/{branchId}/floor-plan"] = new(
            PutFloorPlanAsync,
            caller => Venue(caller, HttpStatusCode.OK)),

        ["PUT /api/branches/{branchId}/public-profile"] = new(
            (w, c) => c.PutAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/public-profile",
                new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId = w.CoverPhotoId }),
            caller => Venue(caller, HttpStatusCode.OK)),

        ["PUT /api/branches/{branchId}/table-photo-positions"] = new(
            (w, c) => c.PutAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/table-photo-positions",
                new
                {
                    coverPhotoId = w.CoverPhotoId,
                    positions = new[] { new { tableId = w.Branch.FirstTableId, photoX = 0.5, photoY = 0.5 } },
                }),
            caller => Venue(caller, HttpStatusCode.OK),
            SetCoverAsync),

        ["POST /api/branches/{branchId}/photos"] = new(
            (w, c) => UploadBranchPhotoAsync(c, w.Branch.BranchId),
            caller => Venue(caller, HttpStatusCode.Created)),

        ["GET /api/branches/{branchId}/reviews"] = new(
            (w, c) => c.GetAsync($"/api/branches/{w.Branch.BranchId}/reviews?filter=all"),
            caller => Venue(caller, HttpStatusCode.OK)),

        ["PUT /api/branches/{branchId}/reviews/{reviewId}/visibility"] = new(
            (w, c) => c.PutAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/reviews/{w.ReviewId}/visibility",
                new { hidden = true, reason = "Names a member of staff." }),
            caller => Venue(caller, HttpStatusCode.OK)),

        ["GET /api/platform/branches/{branchId}/reviews"] = new(
            (w, c) => c.GetAsync($"/api/platform/branches/{w.Branch.BranchId}/reviews"),
            Platform),

        ["PUT /api/platform/reviews/{reviewId}/visibility"] = new(
            (w, c) => c.PutAsJsonAsync(
                $"/api/platform/reviews/{w.ReviewId}/visibility", new { hidden = true, reason = "Names a member of staff." }),
            Platform),
    };

    private static readonly Dictionary<string, MatrixRoute> DinerRoutes = new(StringComparer.Ordinal)
    {
        ["GET /api/diner/me"] = new(
            (_, c) => c.GetAsync("/api/diner/me"),
            caller => DinerOnly(caller, HttpStatusCode.OK)),

        ["GET /api/diner/branches/{branchId}/review"] = new(
            (w, c) => c.GetAsync($"/api/diner/branches/{w.Branch.BranchId}/review"),
            caller => DinerOnly(caller, HttpStatusCode.OK),
            SeedOwnReviewAsync),

        // A first review needs a visit (K8), so the diner has sat at one of the branch's tables.
        ["POST /api/diner/branches/{branchId}/review"] = new(
            (w, c) => c.PostAsJsonAsync($"/api/diner/branches/{w.Branch.BranchId}/review", new { rating = 5, text = "Warm lavash." }),
            caller => DinerOnly(caller, HttpStatusCode.Created),
            SeedVisitAsync),

        ["PUT /api/diner/branches/{branchId}/review"] = new(
            (w, c) => c.PutAsJsonAsync($"/api/diner/branches/{w.Branch.BranchId}/review", new { rating = 2, text = "Cold lavash." }),
            caller => DinerOnly(caller, HttpStatusCode.OK),
            SeedOwnReviewAsync),

        // Somebody else's review, which is the only kind a diner may report.
        ["POST /api/diner/reviews/{reviewId}/report"] = new(
            (w, c) => c.PostAsJsonAsync($"/api/diner/reviews/{w.ReviewId}/report", new { reason = "spam", note = (string?)null }),
            caller => DinerOnly(caller, HttpStatusCode.NoContent)),

        ["GET /api/diner/orders"] = new(
            (_, c) => c.GetAsync("/api/diner/orders?status=active"),
            caller => DinerOnly(caller, HttpStatusCode.OK)),

        ["GET /api/diner/orders/{orderId}"] = new(
            (w, c) => c.GetAsync($"/api/diner/orders/{w.OrderId}"),
            caller => DinerOnly(caller, HttpStatusCode.OK),
            SeedOrderAsync),

        // Last in its row by construction: only the diner is admitted, and the diner goes last.
        ["DELETE /api/diner/me"] = new(
            (w, c) => c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me")
            {
                Content = JsonContent.Create(new { code = w.DeletionCode }),
            }),
            caller => DinerOnly(caller, HttpStatusCode.NoContent),
            RequestDeletionCodeAsync),

        ["GET /api/diner/favorites"] = new(
            (_, c) => c.GetAsync("/api/diner/favorites"),
            caller => DinerOnly(caller, HttpStatusCode.OK)),

        ["PUT /api/diner/favorites"] = new(
            (w, c) => c.PutAsJsonAsync("/api/diner/favorites", new { branchIds = new[] { w.Branch.BranchId } }),
            caller => DinerOnly(caller, HttpStatusCode.OK)),

        ["PUT /api/diner/favorites/{branchId}"] = new(
            (w, c) => c.PutAsync($"/api/diner/favorites/{w.Branch.BranchId}", content: null),
            caller => DinerOnly(caller, HttpStatusCode.NoContent)),

        ["DELETE /api/diner/favorites/{branchId}"] = new(
            (w, c) => c.DeleteAsync($"/api/diner/favorites/{w.Branch.BranchId}"),
            caller => DinerOnly(caller, HttpStatusCode.NoContent)),

        ["GET /api/diner/notifications"] = new(
            (_, c) => c.GetAsync("/api/diner/notifications"),
            caller => DinerOnly(caller, HttpStatusCode.OK)),

        ["POST /api/diner/notifications/read"] = new(
            (_, c) => c.PostAsJsonAsync("/api/diner/notifications/read", new { upTo = (Guid?)null, ids = (Guid[]?)null }),
            caller => DinerOnly(caller, HttpStatusCode.NoContent)),
    };

    private static HttpStatusCode Venue(Caller caller, HttpStatusCode admitted) => caller switch
    {
        Caller.Anonymous => HttpStatusCode.Unauthorized,
        Caller.BranchManager or Caller.Owner or Caller.PlatformAdmin => admitted,
        _ => HttpStatusCode.Forbidden,
    };

    private static HttpStatusCode Platform(Caller caller) => caller switch
    {
        Caller.Anonymous => HttpStatusCode.Unauthorized,
        Caller.PlatformAdmin => HttpStatusCode.OK,
        _ => HttpStatusCode.Forbidden,
    };

    private static HttpStatusCode DinerOnly(Caller caller, HttpStatusCode admitted) => caller switch
    {
        Caller.Anonymous => HttpStatusCode.Unauthorized,
        Caller.Diner => admitted,
        _ => HttpStatusCode.Forbidden,
    };

    // ------------------------------------------------------------ running a row

    private async Task RunAsync(string name, MatrixRoute route)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

        var world = await ArrangeWorldAsync(factory);

        try
        {
            if (route.Arrange is not null)
            {
                await route.Arrange(fixture, world);
            }

            var failures = new List<string>();
            var admitted = 0;

            // Refused first, admitted last. OrderBy is stable, so the enum order holds within each half.
            foreach (var caller in Enum.GetValues<Caller>().OrderBy(c => IsSuccess(route.Expected(c)) ? 1 : 0))
            {
                var expected = route.Expected(caller);
                admitted += IsSuccess(expected) ? 1 : 0;

                using var response = await route.Send(world, world.Clients[caller]);

                if (response.StatusCode != expected)
                {
                    var body = await response.Content.ReadAsStringAsync();

                    failures.Add(
                        $"  {caller}: expected {(int)expected} {expected}, got {(int)response.StatusCode} {response.StatusCode}"
                        + (body.Length == 0 ? string.Empty : $" - {body[..Math.Min(body.Length, 300)]}"));
                }
            }

            Assert.True(admitted > 0, $"{name} admits nobody in the matrix, so it proves nothing about who gets in.");
            Assert.True(failures.Count == 0, $"{name}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            foreach (var client in world.Clients.Values)
            {
                client.Dispose();
            }
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static TheoryData<string> Names(Dictionary<string, MatrixRoute> routes)
    {
        var data = new TheoryData<string>();

        foreach (var name in routes.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    // ------------------------------------------------------------ the world

    private sealed class World
    {
        public required YallaApiFactory Factory { get; init; }

        public required AuthBranch Branch { get; init; }

        /// <summary>An externally hosted photo of the branch, usable as its cover.</summary>
        public required Guid CoverPhotoId { get; init; }

        /// <summary>A published review of the branch by a diner who is not <see cref="DinerUserId"/>.</summary>
        public required Guid ReviewId { get; init; }

        public required Guid DinerUserId { get; init; }

        public required string DinerPhone { get; init; }

        public required IReadOnlyDictionary<Caller, HttpClient> Clients { get; init; }

        public Guid OrderId { get; set; }

        public string? DeletionCode { get; set; }
    }

    private async Task<World> ArrangeWorldAsync(YallaApiFactory factory)
    {
        AuthBranch branch;
        AuthBranch otherVenue;
        PanelAccount owner;
        PanelAccount siblingManager;
        PlatformAdminAccount admin;
        AuthTab tab;
        Guid cookId;
        Guid coverPhotoId;
        Guid reviewId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;

            // The branch every route targets, with its own manager and waiter; a sibling branch of the
            // same venue whose manager must not reach it; and a different venue altogether.
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            otherVenue = await AuthTestData.CreateBranchAsync(db);

            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);

            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);
            coverPhotoId = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);

            var author = await ReviewTestData.SeedDinerAsync(db, "Narek Matrix", now);
            reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, author, 2, "Slow service.", now.AddMinutes(-5));
        }

        var (dinerToken, dinerUserId, dinerPhone) = await SignInDinerAsync(factory);

        var clients = new Dictionary<Caller, HttpClient>
        {
            [Caller.Anonymous] = factory.CreateClient(),
            [Caller.TabParticipant] = factory.CreateClientWithToken(await AuthTestData.JoinTabAsync(factory, tab.JoinToken)),
            [Caller.Diner] = factory.CreateClientWithToken(dinerToken),
            [Caller.WaiterSession] = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch)),
            [Caller.KitchenSession] = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId)),
            [Caller.OtherVenueManager] = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, otherVenue)),
            [Caller.SiblingBranchManager] = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager)),
            [Caller.BranchManager] = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch)),
            [Caller.Owner] = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner)),
            [Caller.PlatformAdmin] = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin)),
        };

        return new World
        {
            Factory = factory,
            Branch = branch,
            CoverPhotoId = coverPhotoId,
            ReviewId = reviewId,
            DinerUserId = dinerUserId,
            DinerPhone = dinerPhone,
            Clients = clients,
        };
    }

    // ------------------------------------------------------------ per-route preconditions

    /// <summary>Pins are placed against the cover, so the branch needs one first.</summary>
    private static async Task SetCoverAsync(SqlServerFixture fixture, World w)
    {
        var saved = await w.Clients[Caller.Owner].PutAsJsonAsync(
            $"/api/branches/{w.Branch.BranchId}/public-profile",
            new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId = w.CoverPhotoId });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private static async Task SeedOwnReviewAsync(SqlServerFixture fixture, World w)
    {
        await using var db = fixture.CreateContext(w.Factory.Clock);
        await ReviewTestData.SeedReviewAsync(db, w.Branch.BranchId, w.DinerUserId, 4, "Good.", w.Factory.Clock.UtcNow.AddMinutes(-1));
    }

    private static async Task SeedVisitAsync(SqlServerFixture fixture, World w)
    {
        await using var db = fixture.CreateContext(w.Factory.Clock);
        await ReviewTestData.SeedTabVisitAsync(db, w.Branch, w.Factory.Clock.UtcNow, w.DinerUserId);
    }

    /// <summary>An order the diner placed from their own phone at the branch's second table.</summary>
    private static async Task SeedOrderAsync(SqlServerFixture fixture, World w)
    {
        await using var db = fixture.CreateContext(w.Factory.Clock);

        var now = w.Factory.Clock.UtcNow;
        var menu = await TestMenuBuilder.CreateAsync(db, w.Branch.BranchId);
        var tab = await AuthTestData.CreateOpenTabAsync(db, w.Branch, w.Branch.TableIds[1], now);

        var place = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, hideTotalFromGuests: false, w.DinerUserId);
        place.Approve(now);
        db.TabParticipants.Add(place);

        var order = TabOrder.PlacedByDiner(tab.TabId, place.Id, now);
        order.AddLine(menu.Coffee, "Flat white", TestMenu.CoffeeAmd, 1);
        db.TabOrders.Add(order);

        await db.SaveChangesAsync();
        w.OrderId = order.Id;
    }

    /// <summary>
    /// A code-only account deletes with a code sent to its number (K2). Asked for once, before anybody
    /// calls: the refused callers never reach the handler, so they spend no attempt on it.
    /// </summary>
    private static async Task RequestDeletionCodeAsync(SqlServerFixture fixture, World w)
    {
        var requested = await w.Clients[Caller.Anonymous].PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = w.DinerPhone });

        requested.EnsureSuccessStatusCode();

        w.DeletionCode = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The floor plan saved back as it stands, against the version read just before.</summary>
    /// <remarks>
    /// Read with the owner's token every time, because each admitted save bumps the version and the next
    /// admitted caller needs the new one. A refused caller sends the same well-formed body.
    /// </remarks>
    private static async Task<HttpResponseMessage> PutFloorPlanAsync(World w, HttpClient client)
    {
        var plan = await w.Clients[Caller.Owner].GetFromJsonAsync<JsonElement>($"/api/branches/{w.Branch.BranchId}/floor-plan");
        var body = FloorPlanBody.From(plan, plan.GetProperty("version").GetString());

        return await client.PutAsJsonAsync($"/api/branches/{w.Branch.BranchId}/floor-plan", body);
    }

    private static async Task<(string AccessToken, Guid DinerUserId, string Phone)> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = ReviewTestData.NextPhone();

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid(), phone);
    }

    private static async Task<HttpResponseMessage> UploadBranchPhotoAsync(HttpClient client, Guid branchId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png((byte)Random.Shared.Next(256)));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "matrix.png");

        return await client.PostAsync($"/api/branches/{branchId}/photos", form);
    }

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
