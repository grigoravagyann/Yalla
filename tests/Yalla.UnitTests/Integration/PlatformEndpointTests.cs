using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.Domain.Venues;

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

    // ------------------------------------------------------------ 7. the table-service gate

    [SkippableFact]
    public async Task Tab_endpoints_answer_not_enabled_without_table_service_and_work_with_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        TestBranch branch;
        PlatformAdminAccount admin;
        string qrToken;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Basic);
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
        Assert.Equal((int)BranchFeature.TableService, problem.GetProperty("context").GetProperty("feature").GetInt32());
        Assert.Equal(branch.BranchId, problem.GetProperty("context").GetProperty("branchId").GetGuid());
        Assert.False(problem.GetProperty("context").TryGetProperty("currentTier", out _));

        // No tab, no session, no seating: the refusal happened before anything was written.
        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(0, await verify.Tabs.CountAsync(t => t.BranchId == branch.BranchId));
            Assert.Equal(0, await verify.TableSessions.CountAsync(s => s.BranchId == branch.BranchId));
        }

        // The platform admin allows table service there.
        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));
        var upgraded = await platform.PutAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}/features", new { features = BranchFeatures.Full });
        Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);
        Assert.Equal(
            Enumerable.Range(1, 10),
            (await upgraded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("features").EnumerateArray().Select(f => f.GetInt32()));

        var opened = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken, deviceId = "phone-a", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var body = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var tabId = body.GetProperty("tab").GetProperty("tabId").GetGuid();
        var token = body.GetProperty("token").GetProperty("accessToken").GetString()!;

        using var diner = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/tabs/{tabId}")).StatusCode);

        // Blocking it now would switch the tab endpoints off under a party that is still eating,
        // hiding a live bill from the people who owe it - so it waits, and says which table.
        var strand = await platform.PutAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}/features", new { features = BranchFeatures.Basic });

        Assert.Equal(HttpStatusCode.Conflict, strand.StatusCode);
        var stranded = await strand.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("open tab", stranded.GetProperty("detail").GetString()!);

        // The slug every state refusal has, with the branch and its open tabs, so the console can name which held it up.
        Assert.Equal("conflicting-state", stranded.GetProperty("code").GetString());
        Assert.Equal(branch.BranchId, stranded.GetProperty("context").GetProperty("branchId").GetGuid());
        Assert.Equal(1, stranded.GetProperty("context").GetProperty("openTabCount").GetInt32());

        // The refusal changed nothing: the tab is still readable and the branch still has table service.
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/tabs/{tabId}")).StatusCode);

        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                BranchFeatures.FullBits,
                await verify.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.AllowedFeatureBits).FirstAsync());
        }

        // A branch with nothing outstanding loses it, and the gate applies from then on.
        await using (var settle = fixture.CreateContext(factory.Clock))
        {
            var tab = await settle.Tabs.FirstAsync(t => t.Id == tabId);
            tab.Close(factory.Clock.UtcNow);
            await settle.SaveChangesAsync();
        }

        var downgraded = await platform.PutAsJsonAsync(
            $"/api/platform/branches/{branch.BranchId}/features", new { features = BranchFeatures.Basic });
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
                features = BranchFeatures.Full,
            },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var venue = await created.Content.ReadFromJsonAsync<JsonElement>();
        var venueId = venue.GetProperty("venue").GetProperty("venueId").GetGuid();
        var branchId = venue.GetProperty("branches")[0].GetProperty("branchId").GetGuid();

        // The platform admin passes the branch-scoped admin surface for a branch they do not belong to.
        var plan = await platform.GetAsync($"/api/branches/{branchId}/floor-plan");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var loadedVersion = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString();

        var put = await platform.PutAsJsonAsync($"/api/branches/{branchId}/floor-plan", new
        {
            expectedVersion = loadedVersion,
            areas = new[] { new { id = (Guid?)null, name = "Windows", displayOrder = 0, photoIds = Array.Empty<Guid>() } },
            tables = new[]
            {
                new { id = (Guid?)null, label = "1", seats = 2, floorAreaName = (string?)"Windows", isBookable = true },
                new { id = (Guid?)null, label = "2", seats = 4, floorAreaName = (string?)null, isBookable = true },
            },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var applied = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, applied.GetProperty("plan").GetProperty("tables").GetArrayLength());

        // A label used twice is a 422 naming it.
        var repeated = await platform.PutAsJsonAsync($"/api/branches/{branchId}/floor-plan", new
        {
            expectedVersion = applied.GetProperty("plan").GetProperty("version").GetString(),
            areas = Array.Empty<object>(),
            tables = new[]
            {
                new { id = (Guid?)null, label = "9", seats = 2, floorAreaName = (string?)null, isBookable = true },
                new { id = (Guid?)null, label = "9", seats = 4, floorAreaName = (string?)null, isBookable = true },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);
        var problem = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("floor-plan-invalid", problem.GetProperty("code").GetString());
        Assert.Equal("9", problem.GetProperty("context").GetProperty("duplicateLabels")[0].GetString());

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

        // Seeding on too: the stub reports the seeded waiter, and the demo venue is what is listed.
        await using var factory = NewFactory()
            .With("DevActor:Enabled", "true")
            .With("DevActor:Role", "Waiter")
            .With("DevSeed:Enabled", "true");
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await SignInPlatformAdminAsync(factory, admin));

        var listed = await platform.GetAsync("/api/platform/venues?search=yalla-demo");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        // The platform admin sees what a platform admin sees - the seeded demo venue - rather than
        // a refusal addressed to the waiter.
        Assert.Equal(1, (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32());

        // Proves the stub really was switched on for this host rather than the test passing
        // vacuously. Seeding no longer implies it, so the demo venue alone cannot say so.
        using (var scope = factory.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<DevCurrentActor>());
        }

        // And the stub still cannot get an untokened caller past a policy.
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/venues")).StatusCode);
    }

    // ------------------------------------------------------------ 21. features are per branch

    /// <summary>
    /// Features are per branch, so one branch of a chain can pilot ordering while the others do not.
    /// A venue-level flag would make that impossible, which is why "apply to every branch" is a
    /// convenience over the branches and the set lives on each one.
    /// </summary>
    [SkippableFact]
    public async Task One_branch_of_a_venue_can_have_table_service_while_another_does_not()
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
            firstBranch = NewBranch("paid", BranchFeatures.Full),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var venue = await created.Content.ReadFromJsonAsync<JsonElement>();
        var venueId = venue.GetProperty("venue").GetProperty("venueId").GetGuid();
        var paidBranchId = venue.GetProperty("branches")[0].GetProperty("branchId").GetGuid();

        // No features sent: the Basic preset, every feature but table service.
        var second = await platform.PostAsJsonAsync(
            $"/api/platform/venues/{venueId}/branches", NewBranch("free", features: null));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var freeBranchId = secondBody.GetProperty("branchId").GetGuid();
        Assert.Equal(BranchFeatures.Basic.Select(f => (int)f), Features(secondBody));

        // The venue's list row carries each branch's own set; there is no venue-level rollup.
        var detail = await platform.GetFromJsonAsync<JsonElement>($"/api/platform/venues/{venueId}");
        Assert.False(detail.GetProperty("venue").TryGetProperty("subscriptionTier", out _));
        var rows = detail.GetProperty("venue").GetProperty("branches").EnumerateArray()
            .ToDictionary(b => b.GetProperty("branchId").GetGuid(), Features);
        Assert.Equal(Enumerable.Range(1, 10), rows[paidBranchId]);
        Assert.DoesNotContain((int)BranchFeature.TableService, rows[freeBranchId]);

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

        // Apply to every branch: one set, both branches, one transaction. The second branch has no menu, so it has no
        // unfinished dishes to refuse table service for.
        var applied = await platform.PutAsJsonAsync(
            $"/api/platform/venues/{venueId}/features",
            new { features = new[] { BranchFeature.WebBookings, BranchFeature.PreOrders, BranchFeature.TableService } });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        var after = (await applied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("branches").EnumerateArray().ToList();
        Assert.Equal(2, after.Count);
        Assert.All(after, b => Assert.Equal([2, 3, 5], Features(b)));

        var onceFree = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken = freeQr, deviceId = "phone-b", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, onceFree.StatusCode);

        // Pre-orders ride on bookings: without a booking channel the set is refused on `features`, and nothing moves.
        var orphan = await platform.PutAsJsonAsync(
            $"/api/platform/venues/{venueId}/features", new { features = new[] { BranchFeature.PreOrders } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, orphan.StatusCode);
        var orphanProblem = await orphan.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", orphanProblem.GetProperty("code").GetString());
        Assert.Equal(
            "features",
            Assert.Single(orphanProblem.GetProperty("context").GetProperty("fields").EnumerateArray()).GetProperty("field").GetString());

        var unknown = await platform.PutAsJsonAsync(
            $"/api/platform/branches/{freeBranchId}/features", new { features = new[] { 11 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        // The set is required: a body without it is not "block everything".
        var missing = await platform.PutAsJsonAsync($"/api/platform/branches/{freeBranchId}/features", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        // Both the branch and the venue route were audited, the branch rows once per branch that changed.
        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            var actions = await verify.PlatformAuditLogs
                .Where(a => a.TargetId == venueId || a.TargetId == paidBranchId || a.TargetId == freeBranchId)
                .Select(a => a.Action)
                .ToListAsync();

            Assert.Equal(2, actions.Count(a => a == "branch.features-change"));
            Assert.Single(actions, "venue.features-apply");
            Assert.Equal([2, 3, 5], (await verify.Branches.FirstAsync(b => b.Id == freeBranchId)).AllowedFeatures.Select(f => (int)f));
        }
    }

    private static IEnumerable<int> Features(JsonElement branch) =>
        branch.GetProperty("features").EnumerateArray().Select(f => f.GetInt32()).ToList();

    // ------------------------------------------------------------ the missing first branch

    /// <summary>
    /// A body with no firstBranch object is bad input, and was answered as a 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service guarded it with <c>ArgumentNullException.ThrowIfNull</c>, which the mapper sends
    /// back as a 500 and logs as an error - correct for the null objects the domain builds for
    /// itself, wrong for this one, which arrives over HTTP. The endpoint had always documented a
    /// refusal here, so the API was contradicting its own contract and filing every malformed
    /// request as a server fault.
    /// </para>
    /// <para>
    /// It now names <b>every</b> missing field rather than the first, so an empty body reports its
    /// name, its slug and its branch together. Out-of-range values are still one at a time, as a
    /// 400 - see <c>CreateVenueCommand.Validate</c> for why the two halves differ.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_create_venue_body_with_missing_fields_names_all_of_them_and_is_not_a_server_fault()
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

        // Nothing at all. Not a 500, and not one field at a time.
        var empty = await platform.PostAsJsonAsync("/api/platform/venues", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);

        var emptyBody = await empty.Content.ReadFromJsonAsync<JsonElement>();

        var fields = emptyBody.GetProperty("context").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("field").GetString())
            .ToList();

        // All three, in one answer. Reporting them one at a time makes somebody submit three times
        // to find out there were three problems.
        Assert.Contains("name", fields);
        Assert.Contains("slug", fields);
        Assert.Contains("firstBranch", fields);

        // And context.field still carries the first, for a form that can only highlight one.
        Assert.Equal("name", emptyBody.GetProperty("context").GetProperty("field").GetString());

        // The branch fields sent flat, where the nested object should be. Name and slug are fine;
        // only the branch is missing, and it is the only thing reported.
        var flat = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Api Venue {marker}",
            type = VenueType.Cafe,
            slug = $"api-venue-{marker}",
            address = "1 Test Street, Yerevan",
            timeZoneId = "Asia/Yerevan",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, flat.StatusCode);

        var flatBody = await flat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("firstBranch", flatBody.GetProperty("context").GetProperty("field").GetString());

        // A nested field left out is named with its path, the way the opening-hours refusal
        // already names [1].opensAt.
        var namelessBranch = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Api Venue {marker}",
            type = VenueType.Cafe,
            slug = $"api-venue-{marker}",
            firstBranch = new
            {
                slug = "main",
                address = "1 Test Street, Yerevan",
                latitude = 40.18,
                longitude = 44.51,
                timeZoneId = "Asia/Yerevan",
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, namelessBranch.StatusCode);
        Assert.Equal(
            "firstBranch.name",
            (await namelessBranch.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());

        // A scalar where the object goes never reaches the validator - the deserialiser refuses it
        // first, as a 400. That half was always correct and stays that way.
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

    // ------------------------------------------------------------ bars

    [SkippableFact]
    public async Task A_bar_is_created_and_patched_as_type_3_ships_the_two_hour_turn_and_is_what_category_3_finds()
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

        async Task<JsonElement> CreateAsync(string name, int type)
        {
            var response = await platform.PostAsJsonAsync("/api/platform/venues", new
            {
                name = $"{name} {marker}",
                type,
                slug = $"{name.ToLowerInvariant()}-{marker}",
                firstBranch = NewBranch("main", BranchFeatures.Basic),
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        // Created as a bar: the wire carries 3, and the first branch ships a cafe's two-hour turn.
        var bar = await CreateAsync("Taproom", 3);
        Assert.Equal(3, bar.GetProperty("venue").GetProperty("type").GetInt32());
        var barBranchId = bar.GetProperty("branches")[0].GetProperty("branchId").GetGuid();

        var policy = await platform.GetFromJsonAsync<JsonElement>($"/api/branches/{barBranchId}/reservation-policy");
        Assert.Equal(120, policy.GetProperty("turnTimeMinutes").GetInt32());

        // Turned into a bar later: the patch takes 3 like any other type.
        var converted = await CreateAsync("Corner", (int)VenueType.Cafe);
        var convertedVenueId = converted.GetProperty("venue").GetProperty("venueId").GetGuid();

        var patched = await platform.PatchAsJsonAsync($"/api/platform/venues/{convertedVenueId}", new { type = 3 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal(3, (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("venue").GetProperty("type").GetInt32());

        var cafe = await CreateAsync("Espresso", (int)VenueType.Cafe);

        // A venue made through the platform has no tables, hours or tablet yet; diners see it once it has.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            foreach (var created in new[] { bar, converted, cafe })
            {
                await TestBranchBuilder.TakeGuestsAsync(db, created.GetProperty("branches")[0].GetProperty("branchId").GetGuid());
            }
        }

        // The diner's Bars tile: category=3 is the two bars and not the cafe, and category=1 the reverse.
        using var anyone = factory.CreateClient();

        var bars = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/search?q={marker}&category=3");
        Assert.Equal(
            new[] { barBranchId, converted.GetProperty("branches")[0].GetProperty("branchId").GetGuid() }.Order(),
            bars.EnumerateArray().Select(b => b.GetProperty("branchId").GetGuid()).Order());
        Assert.All(bars.EnumerateArray(), b => Assert.Equal(3, b.GetProperty("venueType").GetInt32()));

        var cafes = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/search?q={marker}&category=1");
        Assert.Equal(
            cafe.GetProperty("branches")[0].GetProperty("branchId").GetGuid(),
            Assert.Single(cafes.EnumerateArray()).GetProperty("branchId").GetGuid());

        // A number past the last member is still refused, and the message now lists them in order.
        var unknown = await platform.PostAsJsonAsync("/api/platform/venues", new
        {
            name = $"Nowhere {marker}",
            type = 4,
            slug = $"nowhere-{marker}",
            firstBranch = NewBranch("main", BranchFeatures.Basic),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        var field = Assert.Single(
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("fields").EnumerateArray());
        Assert.Equal("type", field.GetProperty("field").GetString());
        Assert.Contains("1 Cafe, 2 Restaurant, 3 Bar", field.GetProperty("message").GetString());
    }

    private static object NewBranch(string slugPart, IReadOnlyList<BranchFeature>? features) => new
    {
        name = $"Branch {slugPart}",
        slug = $"{slugPart}-{Guid.NewGuid():N}"[..24],
        address = "1 Test Street, Yerevan",
        latitude = 40.18,
        longitude = 44.51,
        timeZoneId = "Asia/Yerevan",
        features,
    };

    private static async Task<string> QrTokenAsync(Yalla.Infrastructure.Persistence.YallaDbContext db, Guid branchId)
    {
        // A branch created through the API has no tables, so give it one to scan.
        var table = new Yalla.Domain.Venues.DiningTable(branchId, label: "1", seats: 2);

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
