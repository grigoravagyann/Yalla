using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Venues;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue-admin surface through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The services behind these routes trust the branch or venue id they are handed - deliberately,
/// because scope is a pipeline concern. That makes the policy attachment the only thing standing
/// between a manager and another venue's menu, and "the policy was never applied to the endpoint"
/// is a failure no service-level test can see.
/// </para>
/// <para>
/// So every group is exercised three ways: the manager it is for, a manager from another venue,
/// and a waiter. Plus the platform admin, who passes every scope check by role.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class VenueAdminEndpointTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ branch-scoped routes

    [SkippableFact]
    public async Task Every_branch_scoped_admin_route_admits_its_own_manager_and_refuses_a_neighbour_and_a_waiter()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var routes = new[]
        {
            $"/api/branches/{mine.BranchId}/reservation-policy",
            $"/api/branches/{mine.BranchId}/opening-hours",
            $"/api/branches/{mine.BranchId}/floor-plan",
            // The admin menu read, not the diner one. GET .../menu is deliberately public - a
            // walk-in scanning a QR code has no account and must still be able to read the menu -
            // so the manager-only variant lives at /manage and is what belongs in this list.
            $"/api/branches/{mine.BranchId}/menu/manage",
            $"/api/branches/{mine.BranchId}/readiness",
        };

        foreach (var route in routes)
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync(route)).StatusCode);

            // A manager of another venue: the branch claim does not match and the venue widening
            // does not reach here either.
            Assert.Equal(HttpStatusCode.Forbidden, (await neighbour.GetAsync(route)).StatusCode);

            // A waiter is staff, but these are manager-and-above routes.
            Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(route)).StatusCode);
        }

        // And the diner-facing menu is the opposite of all of that: anonymous, and readable by
        // anybody at all. A guest who scanned the code on table 7 has no token and no account.
        using var anonymous = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/branches/{mine.BranchId}/menu")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await neighbour.GetAsync($"/api/branches/{mine.BranchId}/menu")).StatusCode);
    }

    [SkippableFact]
    public async Task The_menu_write_routes_carry_the_same_policies_as_the_read()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));

        var created = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/menu/categories", new { name = "Drinks", displayOrder = 0 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var categoryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The neighbour cannot create in, or delete from, a branch that is not theirs.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await neighbour.PostAsJsonAsync($"/api/branches/{mine.BranchId}/menu/categories", new { name = "Theirs", displayOrder = 0 })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await neighbour.DeleteAsync($"/api/branches/{mine.BranchId}/menu/categories/{categoryId}")).StatusCode);

        // A photo is required, and is now a row rather than a string - so one has to exist before a
        // menu item can point at it. Seeded here rather than uploaded, because this test is about
        // the menu routes' policies and the upload pipeline has its own suite.
        Guid photoId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            photoId = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        }

        var item = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/menu/categories/{categoryId}/items",
            new
            {
                name = "Flat white",
                description = "House blend",
                priceAmd = 1_400L,
                photoId,
                ingredients = "coffee, milk",
                allergens = "dairy",
                portionSize = "250 ml",
                prepMinutes = 3,
            });

        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = (await item.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // An item missing its allergens is now accepted and comes back flagged. That is the whole
        // of Prompt 10 Part 1 on the wire: the rule did not go away, it moved to the readiness
        // projection, the going-live gate and the diner-facing menu.
        var incomplete = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/menu/categories/{categoryId}/items",
            new
            {
                name = "Half-entered dish",
                priceAmd = 100L,
            });

        Assert.Equal(HttpStatusCode.Created, incomplete.StatusCode);

        var incompleteBody = await incomplete.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(incompleteBody.GetProperty("isComplete").GetBoolean());

        // Absent, not null: the API serialises with WhenWritingNull, so an unphotographed dish has
        // no `photo` member at all. A client must narrow on `isComplete` rather than on presence -
        // the same rule the hidden-total union already follows.
        Assert.False(incompleteBody.TryGetProperty("photo", out _));

        // What is still refused is what was never an item: a thing with no name.
        var nameless = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/menu/categories/{categoryId}/items",
            new { name = "   ", priceAmd = 100L });

        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);

        // ...and the refusal now names the field, which is the sweep in Part 3: every guard in the
        // domain already passed nameof(theParameter) and the mapper was throwing it away.
        var namelessBody = await nameless.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("name", namelessBody.GetProperty("context").GetProperty("field").GetString());

        // And the console's read shows the unfinished item while the diner's does not.
        var console = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/menu/manage");
        var consoleIds = console.EnumerateArray()
            .SelectMany(c => c.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(incompleteBody.GetProperty("id").GetGuid(), consoleIds);

        using var anonymousDiner = factory.CreateClient();
        var dinerMenu = await anonymousDiner.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/menu");
        var dinerIds = dinerMenu.GetProperty("categories").EnumerateArray()
            .SelectMany(c => c.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.DoesNotContain(incompleteBody.GetProperty("id").GetGuid(), dinerIds);

        var unavailable = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/menu/items/{itemId}/availability", new { isAvailable = false });

        Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
        Assert.False((await unavailable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isAvailable").GetBoolean());
    }

    [SkippableFact]
    public async Task Policy_and_opening_hours_writes_go_through_the_pipeline_with_their_rules_intact()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var current = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branch.BranchId}/reservation-policy");
        var policy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!;

        policy["turnTimeMinutes"] = JsonSerializer.SerializeToElement(5);
        var tooShort = await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/reservation-policy", policy);

        // 422 rather than 400 since Prompt 10: the request was understood and is semantically
        // wrong, and it comes back naming the field so a form can place the message. See
        // A_policy_bounds_refusal_carries_the_field_the_bound_and_the_value below.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);

        policy["turnTimeMinutes"] = JsonSerializer.SerializeToElement(75);
        var accepted = await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/reservation-policy", policy);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(75, body.GetProperty("policy").GetProperty("turnTimeMinutes").GetInt32());
        Assert.True(body.TryGetProperty("affectedExistingReservations", out _));

        var overlapping = await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/opening-hours", new[]
        {
            new { day = DayOfWeek.Monday, opensAt = "09:00:00", closesAt = "15:00:00" },
            new { day = DayOfWeek.Monday, opensAt = "14:00:00", closesAt = "23:00:00" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overlapping.StatusCode);

        var week = await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/opening-hours", new[]
        {
            new { day = DayOfWeek.Monday, opensAt = "09:00:00", closesAt = "15:00:00" },
            new { day = DayOfWeek.Friday, opensAt = "18:00:00", closesAt = "01:00:00" },
        });

        Assert.Equal(HttpStatusCode.OK, week.StatusCode);

        var hours = await week.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, hours.GetArrayLength());
        Assert.True(hours[1].GetProperty("closesNextDay").GetBoolean());
    }

    // ------------------------------------------------------------ floor areas and the QR code

    [SkippableFact]
    public async Task Floor_areas_and_qr_regeneration_are_reachable_scoped_and_audited()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));

        var area = await manager.PostAsJsonAsync(
            $"/api/branches/{mine.BranchId}/floor-areas", new { name = "Terrace", displayOrder = 1 });

        Assert.Equal(HttpStatusCode.Created, area.StatusCode);
        var areaId = (await area.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var renamed = await manager.PatchAsJsonAsync(
            $"/api/branches/{mine.BranchId}/floor-areas/{areaId}", new { name = "Garden", displayOrder = 2 });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Garden", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await neighbour.PostAsJsonAsync($"/api/branches/{mine.BranchId}/floor-areas", new { name = "Theirs", displayOrder = 0 })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/branches/{mine.BranchId}/floor-areas/{areaId}")).StatusCode);

        // ---- the QR code. Addressed by table, so BranchScoped resolves the branch from the table.
        var tableId = mine.FirstTableId;
        string before;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            before = await db.DiningTables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.QrToken).FirstAsync();
        }

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await neighbour.PostAsJsonAsync($"/api/tables/{tableId}/regenerate-qr", new { })).StatusCode);

        var regenerated = await manager.PostAsJsonAsync($"/api/tables/{tableId}/regenerate-qr", new { });
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);

        var after = (await regenerated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("qrToken").GetString()!;
        Assert.NotEqual(before, after);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var audit = await db.PlatformAuditLogs.AsNoTracking()
                .SingleAsync(l => l.TargetId == tableId && l.Action == "table.regenerate-qr");

            // The dead token is recorded so somebody can answer "which code stopped working".
            Assert.Contains(before, audit.ChangesJson, StringComparison.Ordinal);

            // The live one is not: anyone who can read the log would otherwise be able to open a
            // tab on that table anonymously.
            Assert.DoesNotContain(after, audit.ChangesJson, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------ venue-scoped staff routes

    [SkippableFact]
    public async Task The_staff_routes_are_venue_scoped_and_enforce_the_role_hierarchy_over_http()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var route = $"/api/venues/{mine.VenueId}/staff";

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await neighbour.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(route)).StatusCode);

        // A manager may add a waiter...
        var added = await manager.PostAsJsonAsync(route, new
        {
            fullName = "New Waiter",
            phone = $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
            role = StaffRole.Waiter,
            pin = "4321",
            branchId = mine.BranchId,
        });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var waiterId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // ...but not an owner, and not a manager.
        foreach (var above in new[] { StaffRole.Owner, StaffRole.Manager })
        {
            var refused = await manager.PostAsJsonAsync(route, new
            {
                fullName = $"Would-be {above}",
                phone = $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
                role = above,
                pin = "4321",
            });

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Nor change their own role.
        var ownRole = await manager.PatchAsJsonAsync($"{route}/{mine.ManagerId}", new { role = StaffRole.Owner });
        Assert.Equal(HttpStatusCode.Forbidden, ownRole.StatusCode);

        var pin = await manager.PostAsJsonAsync($"{route}/{waiterId}/pin", new { pin = "8765" });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);

        // The neighbouring manager cannot touch this venue's staff at all.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await neighbour.PatchAsJsonAsync($"{route}/{waiterId}", new { fullName = "Hijacked" })).StatusCode);
    }

    // ------------------------------------------------------------ the managed-venue read

    /// <summary>
    /// <b>B1.</b> The one read the console opens a venue with. Coverage comes from the caller's
    /// own staff row: an owner and a venue-wide manager see every branch, a manager with a home
    /// branch sees that branch only, the platform admin sees everything - and the platform's own
    /// venue read stays closed to all of them.
    /// </summary>
    [SkippableFact]
    public async Task The_managed_venue_read_lists_the_branches_the_callers_own_row_covers()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;
        Guid secondBranchId;
        PanelAccount owner;
        PanelAccount venueWideManager;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
            secondBranchId = await AuthTestData.AddBranchAsync(db, mine.VenueId, "Europe/London", tableCount: 1);
            owner = await AuthTestData.SeedOwnerAsync(db, mine.VenueId);
            venueWideManager = await AuthTestData.SeedManagerAsync(db, mine.VenueId);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        using var branchManager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var wideManager = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, venueWideManager));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var anonymous = factory.CreateClient();

        var route = $"/api/venues/{mine.VenueId}/manage";
        var both = new HashSet<Guid> { mine.BranchId, secondBranchId };

        var asOwner = await ownerClient.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        var ownerBody = await asOwner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mine.VenueId, ownerBody.GetProperty("venueId").GetGuid());
        Assert.Equal(both, BranchIds(ownerBody));
        Assert.Equal(
            new Dictionary<Guid, string> { [mine.BranchId] = "Asia/Yerevan", [secondBranchId] = "Europe/London" },
            ownerBody.GetProperty("branches").EnumerateArray()
                .ToDictionary(b => b.GetProperty("branchId").GetGuid(), b => b.GetProperty("timeZoneId").GetString()!));

        // The fixture manager's row names the first branch, so that is all the console shows them.
        var asBranchManager = await branchManager.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, asBranchManager.StatusCode);
        Assert.Equal([mine.BranchId], BranchIds(await asBranchManager.Content.ReadFromJsonAsync<JsonElement>()));

        var asWideManager = await wideManager.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, asWideManager.StatusCode);
        Assert.Equal(both, BranchIds(await asWideManager.Content.ReadFromJsonAsync<JsonElement>()));

        var asPlatform = await platform.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, asPlatform.StatusCode);
        Assert.Equal(both, BranchIds(await asPlatform.Content.ReadFromJsonAsync<JsonElement>()));

        // Only the platform tier can address a venue that is not its own, so only it can see a 404.
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/venues/{Guid.CreateVersion7()}/manage")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await neighbour.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync($"/api/venues/{theirs.VenueId}/manage")).StatusCode);

        // And nothing about this read opens the platform's own venue read to a venue user.
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync($"/api/platform/venues/{mine.VenueId}")).StatusCode);
    }

    /// <summary>
    /// The property names of the 200 body, exactly, because the console's mapper reads them by
    /// name off the generated schema and a rename here is a blank console there.
    /// </summary>
    [SkippableFact]
    public async Task The_managed_venue_body_carries_exactly_the_properties_the_console_reads()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        PanelAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            owner = await AuthTestData.SeedOwnerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var response = await ownerClient.GetAsync($"/api/venues/{mine.VenueId}/manage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            ["venueId", "name", "type", "slug", "isSuspended", "isDeleted", "branches"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(mine.VenueId, body.GetProperty("venueId").GetGuid());
        Assert.Equal(JsonValueKind.String, body.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("slug").ValueKind);
        Assert.Equal((int)VenueType.Restaurant, body.GetProperty("type").GetInt32());
        Assert.False(body.GetProperty("isSuspended").GetBoolean());
        Assert.False(body.GetProperty("isDeleted").GetBoolean());

        var branch = Assert.Single(body.GetProperty("branches").EnumerateArray());

        Assert.Equal(
            ["branchId", "venueId", "name", "slug", "timeZoneId", "isActive", "subscriptionTier", "tableCount"],
            branch.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(mine.BranchId, branch.GetProperty("branchId").GetGuid());
        Assert.Equal(mine.VenueId, branch.GetProperty("venueId").GetGuid());
        Assert.Equal(JsonValueKind.String, branch.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.String, branch.GetProperty("slug").ValueKind);
        Assert.Equal("Asia/Yerevan", branch.GetProperty("timeZoneId").GetString());
        Assert.True(branch.GetProperty("isActive").GetBoolean());
        Assert.Equal((int)SubscriptionTier.Paid, branch.GetProperty("subscriptionTier").GetInt32());
        Assert.Equal(2, branch.GetProperty("tableCount").GetInt32());
    }

    /// <summary>
    /// <b>B3.</b> The list is never wider than what the server allows: every branch the read
    /// offers a caller answers 200 on a per-branch read for that caller. For the owner and the
    /// venue-wide manager it is also never narrower than the venue.
    /// </summary>
    [SkippableFact]
    public async Task Every_branch_the_managed_venue_read_offers_answers_a_branch_read_for_the_same_caller()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        Guid secondBranchId;
        PanelAccount owner;
        PanelAccount venueWideManager;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            // A neighbour exists so that a query which forgot to filter by venue has something to leak.
            _ = await AuthTestData.CreateBranchAsync(db);
            secondBranchId = await AuthTestData.AddBranchAsync(db, mine.VenueId, "Europe/London");
            owner = await AuthTestData.SeedOwnerAsync(db, mine.VenueId);
            venueWideManager = await AuthTestData.SeedManagerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        using var branchManager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var wideManager = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, venueWideManager));

        var both = new HashSet<Guid> { mine.BranchId, secondBranchId };

        foreach (var (client, wholeVenue) in new[] { (ownerClient, true), (branchManager, false), (wideManager, true) })
        {
            var response = await client.GetAsync($"/api/venues/{mine.VenueId}/manage");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var offered = BranchIds(await response.Content.ReadFromJsonAsync<JsonElement>());
            Assert.NotEmpty(offered);

            foreach (var branchId in offered)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/branches/{branchId}/floor-plan")).StatusCode);
            }

            if (wholeVenue)
            {
                Assert.Equal(both, offered);
            }
        }
    }

    /// <summary>
    /// The route's own policies, pinned with the query out of the picture. The query refuses a
    /// neighbour too, so with the real one in place the test above stays green whether or not the
    /// route carries <c>VenueScoped</c>. Here the query says yes to everyone, and the route alone
    /// has to say no.
    /// </summary>
    [SkippableFact]
    public async Task The_managed_venue_route_refuses_a_neighbour_and_a_waiter_before_the_query_is_asked()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var stub = new AlwaysYesManagedVenueQuery();

        await using var factory = NewFactory().WithServices(services =>
        {
            services.RemoveAll<IManagedVenueQuery>();
            services.AddSingleton<IManagedVenueQuery>(stub);
        });

        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));

        var route = $"/api/venues/{mine.VenueId}/manage";

        // The stub is really the one answering: the venue's own manager gets its sentinel back.
        var asManager = await manager.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, asManager.StatusCode);
        Assert.Equal(
            AlwaysYesManagedVenueQuery.Sentinel,
            (await asManager.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal([mine.VenueId], stub.Asked);

        // VenueScoped: another venue's manager never reaches the query.
        Assert.Equal(HttpStatusCode.Forbidden, (await neighbour.GetAsync(route)).StatusCode);
        // ManagerOrAbove: neither does a waiter of this venue.
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(route)).StatusCode);
        Assert.Equal([mine.VenueId], stub.Asked);
    }

    /// <summary>A query with no lock of its own, so that only the route's policies can refuse.</summary>
    private sealed class AlwaysYesManagedVenueQuery : IManagedVenueQuery
    {
        public const string Sentinel = "stubbed venue";

        public List<Guid> Asked { get; } = [];

        public Task<ManagedVenueView> GetAsync(Guid venueId, CancellationToken cancellationToken = default)
        {
            Asked.Add(venueId);

            return Task.FromResult(new ManagedVenueView(
                venueId, Sentinel, VenueType.Restaurant, "stubbed", IsSuspended: false, IsDeleted: false, []));
        }
    }

    private static HashSet<Guid> BranchIds(JsonElement body) =>
        body.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchId").GetGuid()).ToHashSet();

    // ------------------------------------------------------------ 7 and 8, on the wire

    /// <summary>
    /// <b>Tests 7 and 8, end to end.</b> A bounds refusal arrives as 422 carrying
    /// <c>context.field</c>, the bound and the value - and a request that breaks three bounds
    /// carries three.
    /// </summary>
    /// <remarks>
    /// The unit tests pin the rules; this pins the <i>response</i>, because the gap being closed
    /// was in the mapper rather than in the rules. A refusal that names its field in an exception
    /// and drops it on the way out is exactly as useless to the console as one that never named it.
    /// </remarks>
    [SkippableFact]
    public async Task A_policy_bounds_refusal_carries_the_field_the_bound_and_the_value()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var route = $"/api/branches/{mine.BranchId}/reservation-policy";

        var current = await (await manager.GetAsync(route)).Content.ReadFromJsonAsync<JsonElement>();

        // One bad field first: the shape of a single refusal is what a form usually meets.
        var one = await manager.PutAsJsonAsync(route, PolicyBody(current, turnTimeMinutes: 5));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, one.StatusCode);

        // The media type every endpoint's OpenAPI declaration promises for a failure. It was being
        // overwritten with application/json by WriteAsJsonAsync, so the schema and the wire
        // disagreed about the one thing generic tooling reads first.
        Assert.Equal("application/problem+json", one.Content.Headers.ContentType?.MediaType);

        var body = await one.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("validation-failed", body.GetProperty("code").GetString());
        Assert.Equal("turnTimeMinutes", body.GetProperty("context").GetProperty("field").GetString());

        var field = body.GetProperty("context").GetProperty("fields").EnumerateArray().Single();

        Assert.Equal("turnTimeMinutes", field.GetProperty("field").GetString());
        Assert.Equal("min", field.GetProperty("bound").GetString());
        Assert.Equal(5, field.GetProperty("value").GetInt32());
        Assert.Equal(15, field.GetProperty("min").GetInt32());
        Assert.Equal(360, field.GetProperty("max").GetInt32());

        // The standard errors map carries the same complaint, keyed by the same name, for anything
        // that already understands RFC 7807 and knows nothing about Yalla.
        Assert.True(body.GetProperty("errors").TryGetProperty("turnTimeMinutes", out var messages));
        Assert.NotEmpty(messages.EnumerateArray());

        // Test 8: three bad fields, three entries. Not the first one and a hidden two.
        var three = await manager.PutAsJsonAsync(route, PolicyBody(
            current, turnTimeMinutes: 5, bookingWindowDays: 0, serviceChargePercent: 150m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, three.StatusCode);

        var threeBody = await three.Content.ReadFromJsonAsync<JsonElement>();

        var names = threeBody.GetProperty("context").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("field").GetString())
            .ToList();

        Assert.Equal(["turnTimeMinutes", "bookingWindowDays", "serviceChargePercent"], names);
        Assert.Equal(3, threeBody.GetProperty("errors").EnumerateObject().Count());

        // And the hours endpoint, whose payload is an array, names the offending block by index.
        var hours = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/opening-hours",
            new[]
            {
                new { day = DayOfWeek.Monday, opensAt = "09:00:00", closesAt = "15:00:00" },
                new { day = DayOfWeek.Monday, opensAt = "14:00:00", closesAt = "23:00:00" },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, hours.StatusCode);

        var hoursBody = await hours.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("validation-failed", hoursBody.GetProperty("code").GetString());
        Assert.Equal("[1].opensAt", hoursBody.GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>The policy as it stands, with the named fields overridden.</summary>
    private static object PolicyBody(
        JsonElement current,
        int? turnTimeMinutes = null,
        int? bookingWindowDays = null,
        decimal? serviceChargePercent = null) =>
        new
        {
            turnTimeMinutes = turnTimeMinutes ?? current.GetProperty("turnTimeMinutes").GetInt32(),
            bufferMinutes = current.GetProperty("bufferMinutes").GetInt32(),
            graceMinutes = current.GetProperty("graceMinutes").GetInt32(),
            lateNudgeAfterMinutes = current.GetProperty("lateNudgeAfterMinutes").GetInt32(),
            graceExtensionMinutes = current.GetProperty("graceExtensionMinutes").GetInt32(),
            minLeadMinutes = current.GetProperty("minLeadMinutes").GetInt32(),
            bookingWindowDays = bookingWindowDays ?? current.GetProperty("bookingWindowDays").GetInt32(),
            cancellationDeadlineMinutes = current.GetProperty("cancellationDeadlineMinutes").GetInt32(),
            autoConfirm = current.GetProperty("autoConfirm").GetBoolean(),
            serviceChargePercent = serviceChargePercent ?? current.GetProperty("serviceChargePercent").GetDecimal(),
            pricesIncludeVat = current.GetProperty("pricesIncludeVat").GetBoolean(),
            maxSeatOverhang = (int?)null,
            approvalRequiredAbovePartySize = (int?)null,
            walkInHoldbackMinutes = current.GetProperty("walkInHoldbackMinutes").GetInt32(),
        };

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
