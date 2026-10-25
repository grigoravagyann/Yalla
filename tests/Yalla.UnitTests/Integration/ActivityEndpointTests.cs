using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The History, the staff results and the transfer over HTTP: who may read what, the device each entry names, the feed's
/// filters and cursor, the CSV, and handing a table over from the tablet. See <c>docs/activity.md</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ActivityEndpointTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Manager or above at the branch reads the History and everybody's results; a waiter reads only their own (403 on the
    /// rest), the kitchen nothing, a manager of the venue's other branch nothing of this one. A venue-wide owner names a
    /// branch for their own numbers; somebody from another venue is not found.
    /// </summary>
    [SkippableFact]
    public async Task Only_managers_at_the_branch_read_history_and_results_and_a_waiter_reads_only_their_own()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PanelAccount owner;
        PanelAccount siblingManager;
        Guid cookId;
        AuthBranch otherVenue;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
            otherVenue = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));

        var b = $"/api/branches/{branch.BranchId}";
        string[] managersOnly =
        [
            $"{b}/activity",
            $"{b}/activity.csv",
            $"{b}/staff-performance",
            $"{b}/staff-performance.csv",
            $"{b}/staff/{branch.WaiterId}/performance",
            $"{b}/staff/{branch.ManagerId}/performance",
        ];

        foreach (var url in managersOnly)
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(url)).StatusCode);

            // A waiter cannot read the team - their colleagues' numbers included - nor the kitchen, nor the other branch.
            Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await sibling.GetAsync(url)).StatusCode);
        }

        // A timeline too: refused for who may not read the branch before anything is looked up, so a waiter cannot even
        // learn whether a bill exists.
        var timeline = $"{b}/activity/subjects/tab/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(timeline)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync(timeline)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sibling.GetAsync(timeline)).StatusCode);

        // A waiter's own, from their own session - and nobody else's through it.
        var mine = await OkAsync(await waiter.GetAsync("/api/staff/me/performance"));
        Assert.Equal(branch.WaiterId, mine.GetProperty("kpis").GetProperty("staffMemberId").GetGuid());
        Assert.Equal(branch.BranchId, mine.GetProperty("branchId").GetGuid());
        Assert.Equal(7, mine.GetProperty("dailySales").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync($"/api/staff/me/performance?branchId={otherVenue.BranchId}")).StatusCode);

        // The kitchen has no tables to report on.
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync("/api/staff/me/performance")).StatusCode);

        // The owner works across the venue: which branch, then.
        var unnamed = await ProblemAsync(await ownerClient.GetAsync("/api/staff/me/performance"), HttpStatusCode.UnprocessableEntity, "validation-failed");
        Assert.Equal("branchId", unnamed.GetProperty("context").GetProperty("field").GetString());
        await OkAsync(await ownerClient.GetAsync($"/api/staff/me/performance?branchId={branch.BranchId}"));

        // Somebody who does not work at this venue is not found, and nothing about a tab that is not here either.
        await ProblemAsync(await manager.GetAsync($"{b}/staff/{otherVenue.WaiterId}/performance"), HttpStatusCode.NotFound, "not-found");
        await ProblemAsync(await manager.GetAsync($"{b}/activity/subjects/tab/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "not-found");

        // Every bad field in one refusal.
        var refused = await ProblemAsync(
            await manager.GetAsync($"{b}/activity?date=today&category=everything&limit=500"),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        Assert.Equal(
            ["date", "category", "limit"],
            refused.GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray());
    }

    /// <summary>
    /// The waiter seats two tables from the tablet and the manager marks a dish sold out from the console. The feed says
    /// who, from which device ("Test tablet", "Console"), filters by category, person, table and dish, pages by cursor
    /// without repeating or skipping, and exports the same rows as CSV.
    /// </summary>
    [SkippableFact]
    public async Task The_feed_says_who_and_from_which_device_filters_and_pages()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var b = $"/api/branches/{branch.BranchId}";

        foreach (var tableId in branch.TableIds)
        {
            await OkAsync(await waiter.PostAsJsonAsync(
                $"{b}/tables/{tableId}/seat-walk-in", new { partySize = 2, clientCommandId = Guid.NewGuid() }));
            factory.Clock.UtcNow = factory.Clock.UtcNow.AddSeconds(1);
        }

        await OkAsync(await manager.PostAsJsonAsync($"{b}/menu/items/{menu.Wine}/availability", new { isAvailable = false }));

        // The sign-in helpers' enrolment code is on it too - a device entry from the console - and the tablet's
        // enrolment with it, the system's.
        var everything = (await OkAsync(await manager.GetAsync($"{b}/activity"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(everything, i => i.GetProperty("action").GetString() == ActivityActions.DeviceEnrolmentCodeCreated);
        Assert.Equal(
            "system",
            Assert.Single(everything, i => i.GetProperty("action").GetString() == ActivityActions.DeviceEnrolled)
                .GetProperty("actorKind").GetString());

        var all = everything.Where(i => i.GetProperty("category").GetString() != "device").ToList();
        Assert.Equal(
            [ActivityActions.SettingsMenuAvailability, ActivityActions.TableSeated, ActivityActions.TableSeated],
            all.Select(i => i.GetProperty("action").GetString()!).ToArray());

        var sold = all[0];
        Assert.Equal("settings", sold.GetProperty("category").GetString());
        Assert.Equal("staff", sold.GetProperty("actorKind").GetString());
        Assert.Equal(branch.ManagerId, sold.GetProperty("staffMemberId").GetGuid());
        Assert.Equal("Console", sold.GetProperty("deviceLabel").GetString());
        Assert.False(sold.TryGetProperty("deviceId", out _));
        Assert.True(sold.GetProperty("before").GetProperty("isAvailable").GetBoolean());
        Assert.False(sold.GetProperty("after").GetProperty("isAvailable").GetBoolean());

        foreach (var seat in all.Skip(1))
        {
            Assert.Equal("Test Waiter", seat.GetProperty("actorName").GetString());
            Assert.Equal("Test tablet", seat.GetProperty("deviceLabel").GetString());
            Assert.Equal(JsonValueKind.String, seat.GetProperty("deviceId").ValueKind);
            Assert.Equal(2, seat.GetProperty("summary").GetProperty("partySize").GetInt32());
        }

        async Task<string[]> ActionsAsync(string query) =>
            [.. (await OkAsync(await manager.GetAsync($"{b}/activity?{query}"))).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("action").GetString()!)];

        Assert.Equal([ActivityActions.SettingsMenuAvailability], await ActionsAsync("category=settings"));
        Assert.Equal(2, (await ActionsAsync($"staffMemberId={branch.WaiterId}")).Length);
        Assert.Equal([ActivityActions.TableSeated], await ActionsAsync("q=2"));
        Assert.Equal([ActivityActions.SettingsMenuAvailability], await ActionsAsync("q=areni"));

        // A page at a time: the same rows, in the same order, no repeats.
        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var page = await OkAsync(await manager.GetAsync($"{b}/activity?limit=1{(cursor is null ? string.Empty : $"&cursor={cursor}")}"));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(everything.Select(i => i.GetProperty("id").GetGuid()), seen);

        // The CSV: a byte-order mark, the header, the same rows.
        var csv = await manager.GetAsync($"{b}/activity.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);

        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);

        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(l => l.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.StartsWith("local,actorKind,actorName,deviceLabel,category,action", lines[0]);
        Assert.Equal(everything.Count + 1, lines.Count);
        Assert.Contains(ActivityActions.SettingsMenuAvailability, lines[1]);

        // The results table lists the waiter and the manager, with nothing yet.
        var team = await OkAsync(await manager.GetAsync($"{b}/staff-performance"));
        var ids = team.GetProperty("staff").EnumerateArray().Select(r => r.GetProperty("staffMemberId").GetGuid()).ToList();
        Assert.Contains(branch.WaiterId, ids);
        Assert.Contains(branch.ManagerId, ids);
        Assert.Equal(0, team.GetProperty("totals").GetProperty("tables").GetInt32());
    }

    /// <summary>
    /// From the tablet: the waiter seats a table, opens its bill and hands it to the manager; the floor names the new
    /// waiter; the History says who handed what to whom from which device. A body without a target is a 422, and a manager
    /// of the other branch cannot reach the tab at all.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_hands_a_table_over_from_the_tablet_and_the_floor_shows_who_has_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PanelAccount siblingManager;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));
        var b = $"/api/branches/{branch.BranchId}";

        await OkAsync(await waiter.PostAsJsonAsync(
            $"{b}/tables/{branch.FirstTableId}/seat-walk-in", new { partySize = 3, clientCommandId = Guid.NewGuid() }));
        var tabId = (await OkAsync(await waiter.PostAsJsonAsync(
            $"{b}/tables/{branch.FirstTableId}/open-tab", new { clientCommandId = Guid.NewGuid() }))).GetProperty("tabId").GetGuid();

        var before = (await OkAsync(await waiter.GetAsync($"{b}/tables/floor"))).GetProperty("tables")[0];
        Assert.Equal(branch.WaiterId, before.GetProperty("serverStaffMemberId").GetGuid());

        await ProblemAsync(await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/transfer", new { }), HttpStatusCode.UnprocessableEntity, "validation-failed");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await sibling.PostAsJsonAsync($"/api/tabs/{tabId}/transfer", new { toStaffMemberId = branch.ManagerId })).StatusCode);

        var moved = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/transfer", new { toStaffMemberId = branch.ManagerId, clientCommandId = Guid.NewGuid() }));
        Assert.Equal(branch.WaiterId, moved.GetProperty("previousServerStaffMemberId").GetGuid());
        Assert.Equal(branch.ManagerId, moved.GetProperty("serverStaffMemberId").GetGuid());
        Assert.Equal("Test Manager", moved.GetProperty("serverName").GetString());
        Assert.False(moved.GetProperty("wasReplay").GetBoolean());

        var after = (await OkAsync(await waiter.GetAsync($"{b}/tables/floor"))).GetProperty("tables")[0];
        Assert.Equal(branch.ManagerId, after.GetProperty("serverStaffMemberId").GetGuid());
        Assert.Equal("Test Manager", after.GetProperty("serverName").GetString());

        // The waiter's table is no longer theirs to hand on.
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/transfer", new { toStaffMemberId = branch.WaiterId }),
            HttpStatusCode.Forbidden,
            "forbidden");

        var timeline = await OkAsync(await manager.GetAsync($"{b}/activity/subjects/tab/{tabId}"));
        var transferred = timeline.GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == ActivityActions.TableTransferred);
        Assert.Equal("Test tablet", transferred.GetProperty("deviceLabel").GetString());
        Assert.Equal("Test Manager", transferred.GetProperty("summary").GetProperty("toName").GetString());
        Assert.Equal(
            [branch.WaiterId, branch.ManagerId],
            timeline.GetProperty("tab").GetProperty("servers").EnumerateArray().Select(s => s.GetProperty("staffMemberId").GetGuid()).ToArray());

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.True(await verify.ActivityEntries.AnyAsync(e => e.SubjectId == tabId && e.Action == ActivityActions.TableTabOpened));
    }

    /// <summary>
    /// The transfer sheet's list, read from the waiter's own phone (whose roster names only them): active people at the
    /// branch or venue-wide who serve, not the table's waiter, on shift first and then by name, each with the tables
    /// they serve now - and nothing personal. Only whoever may transfer the table may read it: another waiter of the
    /// branch, the kitchen and another branch's manager are 403.
    /// </summary>
    [SkippableFact]
    public async Task The_transfer_targets_are_who_can_take_the_table_on_shift_first_for_whoever_may_hand_it_over()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PanelAccount owner;
        PanelAccount generalManager;
        PanelAccount siblingManager;
        Guid cookId;
        Guid zed;
        Guid aaa;
        const string aaaPin = "1357";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);

            var hasher = new SecretHasher();
            StaffMember Person(string name, StaffRole role, Guid? at, string pin = "hash") =>
                new(branch.VenueId, name, $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}", role,
                    pin == "hash" ? pin : hasher.Hash(pin), at);

            var onShift = Person("Zed Waiter", StaffRole.Waiter, branch.BranchId);
            var offShift = Person("aaa Waiter", StaffRole.Waiter, branch.BranchId, aaaPin);
            var gone = Person("Gone Waiter", StaffRole.Waiter, branch.BranchId);
            gone.SetActive(false);
            var elsewhere = Person("Elsewhere Waiter", StaffRole.Waiter, siblingBranchId);
            db.StaffMembers.AddRange(onShift, offShift, gone, elsewhere);

            // On shift at 15:00 Yerevan, the factory's start; and a shift at the other branch counts for nothing here.
            var today = BranchZone.For("Asia/Yerevan").LocalDateAt(factory.Clock.UtcNow);
            db.StaffShifts.Add(new StaffShift(onShift.Id, branch.BranchId, today, new TimeOnly(14, 0), new TimeOnly(18, 0)));
            db.StaffShifts.Add(new StaffShift(elsewhere.Id, siblingBranchId, today, new TimeOnly(14, 0), new TimeOnly(18, 0)));
            await db.SaveChangesAsync();

            zed = onShift.Id;
            aaa = offShift.Id;
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var b = $"/api/branches/{branch.BranchId}";

        // The waiter's table, and one the manager seated from the console - the manager's.
        await OkAsync(await tablet.PostAsJsonAsync(
            $"{b}/tables/{branch.TableIds[0]}/seat-walk-in", new { partySize = 2, clientCommandId = Guid.NewGuid() }));
        var tabId = (await OkAsync(await tablet.PostAsJsonAsync(
            $"{b}/tables/{branch.TableIds[0]}/open-tab", new { clientCommandId = Guid.NewGuid() }))).GetProperty("tabId").GetGuid();
        await OkAsync(await manager.PostAsJsonAsync(
            $"{b}/tables/{branch.TableIds[1]}/seat-walk-in", new { partySize = 2, clientCommandId = Guid.NewGuid() }));

        var route = $"/api/tabs/{tabId}/transfer-targets";

        // From the waiter's own phone.
        var (_, phoneSession) = await StaffPhoneTests.PairWaiterAsync(factory, branch);
        using var phone = factory.CreateClientWithToken(phoneSession);

        var items = (await OkAsync(await phone.GetAsync(route))).GetProperty("items").EnumerateArray().ToList();

        // Zed is on shift; then by name, case aside. Not the table's waiter, the inactive, the other branch, the kitchen.
        Assert.Equal(
            [zed, aaa, owner.StaffMemberId, generalManager.StaffMemberId, branch.ManagerId],
            items.Select(i => i.GetProperty("staffMemberId").GetGuid()).ToArray());
        Assert.Equal(
            [true, false, false, false, false],
            items.Select(i => i.GetProperty("onShiftNow").GetBoolean()).ToArray());
        Assert.Equal(
            [0, 0, 0, 0, 1],
            items.Select(i => i.GetProperty("openTables").GetInt32()).ToArray());
        Assert.Equal((int)StaffRole.Owner, items[2].GetProperty("role").GetInt32());

        // Nothing else about anybody.
        Assert.All(items, i => Assert.Equal(
            ["name", "onShiftNow", "openTables", "role", "staffMemberId"],
            i.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));

        // A manager reads it too, and does not see the waiter whose table it is.
        var forManager = (await OkAsync(await manager.GetAsync(route))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("staffMemberId").GetGuid()).ToList();
        Assert.DoesNotContain(branch.WaiterId, forManager);
        Assert.Contains(branch.ManagerId, forManager);

        // Another waiter of the branch may not hand this table over, so may not list who could take it.
        var enrolled = await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        using var otherTablet = factory.CreateClientWithToken(enrolled);
        var aaaSignIn = await otherTablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = aaa, pin = aaaPin });
        aaaSignIn.EnsureSuccessStatusCode();
        using var otherWaiter = factory.CreateClientWithToken(
            (await aaaSignIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        await ProblemAsync(await otherWaiter.GetAsync(route), HttpStatusCode.Forbidden, "forbidden");

        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync(route)).StatusCode);

        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));
        Assert.Equal(HttpStatusCode.Forbidden, (await sibling.GetAsync(route)).StatusCode);

        // A tab that does not exist resolves to no branch: refused like the wrong branch, revealing nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/tabs/{Guid.NewGuid()}/transfer-targets")).StatusCode);
    }

    /// <summary>The middle of the test branches' day on today's Yerevan date: nothing here depends on the hour it runs.</summary>
    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString).StartingAtYerevan(new TimeOnly(15, 0));
}
