using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform tier through the real pipeline: the policy on the group, the scope passthrough,
/// suspension as diners and owners see it, and the paid-branch gate on the tab endpoints.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PlatformEndpointTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 6. suspension

    [SkippableFact]
    public async Task A_suspended_venue_disappears_from_diner_browsing_but_is_still_readable_by_its_owner()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var anonymous = factory.CreateClient();
        var before = await anonymous.GetAsync($"/api/branches/{branch.BranchId}/availability");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var suspended = await platform.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/suspend", new { });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.True((await suspended.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("venue").GetProperty("isSuspended").GetBoolean());

        // Gone for diners.
        var after = await anonymous.GetAsync($"/api/branches/{branch.BranchId}/availability");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        // Still there for the owner's manager, with every table intact.
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var floorPlan = await manager.GetAsync($"/api/branches/{branch.BranchId}/floor-plan");
        Assert.Equal(HttpStatusCode.OK, floorPlan.StatusCode);
        Assert.Equal(2, (await floorPlan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tables").GetArrayLength());

        // And the manager cannot touch the platform tier.
        var forbidden = await manager.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/reactivate", new { });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var reactivated = await platform.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/reactivate", new { });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/branches/{branch.BranchId}/availability")).StatusCode);
    }

    // ------------------------------------------------------------ 7. the paid-branch gate

    [SkippableFact]
    public async Task Tab_endpoints_answer_not_enabled_for_a_Free_branch_and_work_for_a_Paid_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        TestBranch branch;
        PlatformAdminAccount admin;
        string qrToken;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Free);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            qrToken = await db.DiningTables.Where(t => t.Id == branch.FirstTableId).Select(t => t.QrToken).FirstAsync();
        }

        using var anonymous = factory.CreateClient();

        var refused = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken, deviceId = "phone-a", clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("feature-not-enabled", problem.GetProperty("code").GetString());
        Assert.Contains("not enabled for this branch", problem.GetProperty("detail").GetString());

        // No tab, no session, no seating: the refusal happened before anything was written.
        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(0, await verify.Tabs.CountAsync(t => t.BranchId == branch.BranchId));
            Assert.Equal(0, await verify.TableSessions.CountAsync(s => s.BranchId == branch.BranchId));
        }

        // The platform admin upgrades the branch.
        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var upgraded = await platform.PatchAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}", new { subscriptionTier = SubscriptionTier.Paid });
        Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);

        var opened = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken, deviceId = "phone-a", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var body = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var tabId = body.GetProperty("tab").GetProperty("tabId").GetGuid();
        var token = body.GetProperty("token").GetProperty("accessToken").GetString()!;

        using var diner = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/tabs/{tabId}")).StatusCode);

        // Downgrading now would switch the tab endpoints off under a party that is still eating,
        // hiding a live bill from the people who owe it - so it waits, and says which table.
        var strand = await platform.PatchAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}", new { subscriptionTier = SubscriptionTier.Free });

        Assert.Equal(HttpStatusCode.Conflict, strand.StatusCode);
        Assert.Contains("open tab", (await strand.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!);

        // The refusal changed nothing: the tab is still readable and the branch is still Paid.
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/tabs/{tabId}")).StatusCode);

        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                SubscriptionTier.Paid,
                await verify.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.SubscriptionTier).FirstAsync());
        }

        // A branch with nothing outstanding downgrades, and the gate applies from then on.
        await using (var settle = fixture.CreateContext(factory.Clock))
        {
            var tab = await settle.Tabs.FirstAsync(t => t.Id == tabId);
            tab.Close(factory.Clock.UtcNow);
            await settle.SaveChangesAsync();
        }

        var downgraded = await platform.PatchAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}", new { subscriptionTier = SubscriptionTier.Free });
        Assert.Equal(HttpStatusCode.OK, downgraded.StatusCode);

        var gated = await diner.GetAsync($"/api/tabs/{tabId}");
        Assert.Equal(HttpStatusCode.Conflict, gated.StatusCode);
        Assert.Equal("feature-not-enabled", (await gated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ------------------------------------------------------------ the platform surface end to end

    [SkippableFact]
    public async Task A_platform_admin_can_create_configure_and_delete_a_venue_through_the_api()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var marker = Guid.NewGuid().ToString("N")[..8];

        var created = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Api Venue {marker}",
            type = VenueType.Cafe,
            slug = $"api-venue-{marker}",
            firstBranch = new
            {
                name = "Main",
                slug = "main",
                address = "1 Test Street, Yerevan",
                latitude = 40.18,
                longitude = 44.51,
                timeZoneId = "Asia/Yerevan",
                floorWidth = 1000,
                floorHeight = 700,
                subscriptionTier = SubscriptionTier.Paid,
            },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var venue = await created.Content.ReadFromJsonAsync<JsonElement>();
        var venueId = venue.GetProperty("venue").GetProperty("venueId").GetGuid();
        var branchId = venue.GetProperty("branches")[0].GetProperty("branchId").GetGuid();

        // The platform admin passes the branch-scoped admin surface for a branch they do not belong to.
        var plan = await platform.GetAsync($"/api/branches/{branchId}/floor-plan");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);

        var put = await platform.PutAsJsonAsync($"/api/branches/{branchId}/floor-plan", new
        {
            floorWidth = 1000,
            floorHeight = 700,
            areas = new[] { new { id = (Guid?)null, name = "Windows", displayOrder = 0 } },
            tables = new[]
            {
                new { id = (Guid?)null, label = "1", seats = 2, x = 50, y = 50, width = 90, height = 90, rotationDegrees = 0d, shape = TableShape.Round, floorAreaName = (string?)"Windows", isBookable = true },
                new { id = (Guid?)null, label = "2", seats = 4, x = 300, y = 50, width = 120, height = 90, rotationDegrees = 0d, shape = TableShape.Rectangle, floorAreaName = (string?)null, isBookable = true },
            },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var applied = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, applied.GetProperty("plan").GetProperty("tables").GetArrayLength());

        // A table off the canvas is a 422 naming it.
        var outside = await platform.PutAsJsonAsync($"/api/branches/{branchId}/floor-plan", new
        {
            floorWidth = 1000,
            floorHeight = 700,
            areas = Array.Empty<object>(),
            tables = new[]
            {
                new { id = (Guid?)null, label = "9", seats = 2, x = 990, y = 50, width = 90, height = 90, rotationDegrees = 0d, shape = TableShape.Round, floorAreaName = (string?)null, isBookable = true },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, outside.StatusCode);
        var problem = await outside.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("floor-plan-invalid", problem.GetProperty("code").GetString());
        Assert.Equal("9", problem.GetProperty("context").GetProperty("tablesOutsideCanvas")[0].GetString());

        var list = await platform.GetAsync($"/api/platform/venues?search={marker}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(1, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32());

        var deleted = await platform.DeleteAsync($"/api/platform/venues/{venueId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("venue").GetProperty("isDeleted").GetBoolean());

        // Anonymous and non-platform callers never reach the tier.
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/venues")).StatusCode);
    }

    /// <summary>
    /// The development actor stub must never contradict a token.
    /// </summary>
    /// <remarks>
    /// It used to replace <c>ICurrentActor</c> outright, so a platform admin's own request passed
    /// every policy and then reached a service that had been told they were the seeded waiter. The
    /// error - "List venues requires the PlatformAdmin role; the caller is a Waiter" - named a role
    /// the caller never chose and pointed at the wrong layer entirely.
    /// </remarks>
    [SkippableFact]
    public async Task The_development_actor_stub_does_not_override_a_real_token()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory().With("DevActor:Enabled", "true").With("DevActor:Role", "Waiter");
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));

        var listed = await platform.GetAsync("/api/platform/venues?search=yalla-demo");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        // Proves the stub really was switched on for this host rather than the test passing
        // vacuously: the demo venue exists only because enabling it also runs the dev seeder.
        Assert.Equal(1, (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32());

        // And the stub still cannot get an untokened caller past a policy.
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/venues")).StatusCode);
    }

    // ------------------------------------------------------------ 21. the tier is per branch

    /// <summary>
    /// Billing is per branch, so one branch of a chain can pilot ordering while the others do not.
    /// A venue-level flag would make that impossible, which is why the rollup is a read and the
    /// flag lives on the branch.
    /// </summary>
    [SkippableFact]
    public async Task One_branch_of_a_venue_can_be_paid_while_another_stays_free()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var marker = Guid.NewGuid().ToString("N")[..8];

        var created = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Chain {marker}",
            type = VenueType.Cafe,
            slug = $"chain-{marker}",
            firstBranch = NewBranch("paid", subscriptionTier: SubscriptionTier.Paid),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var venue = await created.Content.ReadFromJsonAsync<JsonElement>();
        var venueId = venue.GetProperty("venue").GetProperty("venueId").GetGuid();
        var paidBranchId = venue.GetProperty("branches")[0].GetProperty("branchId").GetGuid();

        var second = await platform.PostAsJsonAsync(
            $"/api/platform/venues/{venueId}/branches", NewBranch("free", SubscriptionTier.Free));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var freeBranchId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("branchId").GetGuid();

        // The venue rolls up to Free, because not every branch is paid - and says how many are.
        var detail = await platform.GetFromJsonAsync<JsonElement>($"/api/platform/venues/{venueId}");
        Assert.Equal((int)SubscriptionTier.Free, detail.GetProperty("venue").GetProperty("subscriptionTier").GetInt32());
        Assert.Equal(1, detail.GetProperty("venue").GetProperty("paidBranchCount").GetInt32());

        // What actually matters: the gate follows the branch, not the venue.
        string paidQr;
        string freeQr;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            paidQr = await QrTokenAsync(db, paidBranchId);
            freeQr = await QrTokenAsync(db, freeBranchId);
        }

        using var anonymous = factory.CreateClient();

        var onPaid = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken = paidQr, deviceId = "phone-a", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, onPaid.StatusCode);

        var onFree = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken = freeQr, deviceId = "phone-b", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, onFree.StatusCode);
        Assert.Equal(
            "feature-not-enabled",
            (await onFree.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ------------------------------------------------------------ the missing first branch

    /// <summary>
    /// A body with no firstBranch object is bad input, and was answered as a 500.
    /// </summary>
    /// <remarks>
    /// The service guarded it with <c>ArgumentNullException.ThrowIfNull</c>, which the mapper
    /// sends back as a 500 and logs as an error - correct for the null objects the domain builds
    /// for itself, wrong for this one, which arrives over HTTP. The endpoint has always documented
    /// a 400 here, so the API was contradicting its own contract and filing every malformed
    /// request as a server fault. The sibling cases below already answered 400 and must keep to it.
    /// </remarks>
    [SkippableFact]
    public async Task A_create_venue_body_with_no_first_branch_is_refused_as_bad_input_not_a_server_fault()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var marker = Guid.NewGuid().ToString("N")[..8];

        // Nothing at all.
        var empty = await platform.PostAsJsonAsync("/api/platform/venues", new { });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        // And it names the field, so the console can put the message against the input rather
        // than asking the owner to quote a traceId.
        var emptyBody = await empty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("firstBranch", emptyBody.GetProperty("context").GetProperty("field").GetString());

        // The branch fields sent flat, where the nested object should be. Same null, same answer.
        var flat = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Api Venue {marker}",
            type = VenueType.Cafe,
            slug = $"api-venue-{marker}",
            address = "1 Test Street, Yerevan",
            timeZoneId = "Asia/Yerevan",
            floorWidth = 1000,
            floorHeight = 700,
        });

        Assert.Equal(HttpStatusCode.BadRequest, flat.StatusCode);

        // The neighbouring refusals, which the deserializer already answered correctly: a scalar
        // where the object goes, and an undefined venue type.
        var scalar = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Api Venue {marker}",
            type = VenueType.Cafe,
            slug = $"api-venue-{marker}",
            firstBranch = "main",
        });

        Assert.Equal(HttpStatusCode.BadRequest, scalar.StatusCode);

        // No venue survived any of it.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Venues.AnyAsync(v => v.Slug == $"api-venue-{marker}"));
        }
    }

    private static object NewBranch(string slugPart, SubscriptionTier subscriptionTier) => new
    {
        name = $"Branch {slugPart}",
        slug = $"{slugPart}-{Guid.NewGuid():N}"[..24],
        address = "1 Test Street, Yerevan",
        latitude = 40.18,
        longitude = 44.51,
        timeZoneId = "Asia/Yerevan",
        floorWidth = 1000,
        floorHeight = 700,
        subscriptionTier,
    };

    private static async Task<string> QrTokenAsync(Yalla.Infrastructure.Persistence.YallaDbContext db, Guid branchId)
    {
        // A branch created through the API has no tables, so give it one to scan.
        var table = new Yalla.Domain.Venues.DiningTable(
            branchId, label: "1", seats: 2, x: 10, y: 10, width: 90, height: 90,
            shape: Yalla.Domain.Enums.TableShape.Round);

        db.DiningTables.Add(table);
        await db.SaveChangesAsync();

        return table.QrToken;
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>The platform admin signs in where every admin-panel user does. No venue behind the token.</summary>
    internal static async Task<string> SignInPlatformAdminAsync(YallaApiFactory factory, PlatformAdminAccount admin)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in", new { email = admin.Email, password = admin.Password });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("venueId", out var venueId) && venueId.ValueKind != JsonValueKind.Null);

        return body.GetProperty("accessToken").GetString()!;
    }
}
