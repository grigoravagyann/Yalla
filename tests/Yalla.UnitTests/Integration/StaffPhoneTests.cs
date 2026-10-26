using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Personal phones: a staff device bound to one person, paired by a code issued for them and opened only with
/// their own PIN - and every way that binding is meant to refuse.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StaffPhoneTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_manager_pairs_a_waiters_phone_and_the_waiter_is_signed_in_on_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var issued = await manager.PostAsJsonAsync(PairingsRoute(branch, branch.WaiterId), new { });

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("code").GetString()!;

        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code);
        Assert.Equal($"https://test.yalla.app/staff/pair#code={code}", body.GetProperty("pairingUrl").GetString());
        Assert.Equal(branch.WaiterId, body.GetProperty("staffMemberId").GetGuid());
        Assert.Equal(factory.Clock.UtcNow.AddMinutes(10), body.GetProperty("expiresAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));

        using var phone = factory.CreateClient();

        // "Is this you?" before the PIN, without spending the code - typed in lower case without its dash.
        var preview = await phone.PostAsJsonAsync(
            "/api/auth/staff/phone-pair/preview", new { code = code.Replace("-", string.Empty).ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var who = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(branch.WaiterId, who.GetProperty("staffMemberId").GetGuid());
        Assert.Equal("Test Waiter", who.GetProperty("fullName").GetString());
        Assert.Equal((int)StaffRole.Waiter, who.GetProperty("role").GetInt32());

        var paired = await PairAsync(phone, code, branch.WaiterPin);

        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);

        var result = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var device = result.GetProperty("device");
        var session = result.GetProperty("session");

        Assert.Equal(branch.BranchId, device.GetProperty("branchId").GetGuid());
        Assert.Equal("iPhone · Safari", device.GetProperty("deviceName").GetString());
        Assert.Equal(branch.WaiterId, session.GetProperty("staffMemberId").GetGuid());
        Assert.Equal((int)StaffRole.Waiter, session.GetProperty("role").GetInt32());

        // The session works on the floor like any PIN session.
        using var waiter = factory.CreateClientWithToken(session.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await waiter.GetAsync($"/api/branches/{branch.BranchId}/tables/floor")).StatusCode);

        // The device says what it is and whose.
        using var deviceClient = factory.CreateClientWithToken(device.GetProperty("deviceToken").GetString()!);
        var view = await (await deviceClient.GetAsync("/api/auth/staff/device")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)StaffDeviceKind.Phone, view.GetProperty("kind").GetInt32());
        Assert.Equal(branch.WaiterId, view.GetProperty("boundStaffMember").GetProperty("id").GetGuid());
        Assert.Equal("Test Waiter", view.GetProperty("boundStaffMember").GetProperty("fullName").GetString());

        // Its PIN screen is the waiter's alone.
        var roster = await (await deviceClient.GetAsync("/api/auth/staff/roster")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(branch.WaiterId, Assert.Single(roster.EnumerateArray()).GetProperty("staffMemberId").GetGuid());

        // And the waiter's PIN opens it again later.
        var again = await deviceClient.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [SkippableFact]
    public async Task A_phone_refuses_anybody_elses_pin_as_a_wrong_pin_and_counts_nothing_against_them()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var (deviceToken, _) = await PairWaiterAsync(factory, branch);

        using var phone = factory.CreateClientWithToken(deviceToken);

        // The manager's own, correct PIN, on the waiter's phone.
        var refused = await phone.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.ManagerId, pin = "9182" });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pin-invalid", refusal.GetProperty("code").GetString());

        // Exactly the wrong PIN's answer - and nothing written against the manager, so the phone cannot lock them out.
        var wrong = await (await phone.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = "0000" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrong.GetProperty("code").GetString(), refusal.GetProperty("code").GetString());
        Assert.Equal(wrong.GetProperty("detail").GetString(), refusal.GetProperty("detail").GetString());

        for (var i = 0; i < 6; i++)
        {
            await phone.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.ManagerId, pin = "0000" });
        }

        await using var db = fixture.CreateContext(factory.Clock);
        var manager = await db.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == branch.ManagerId);

        Assert.Equal(0, manager.PinFailedAttempts);
        Assert.Null(manager.PinLockedUntilUtc);
    }

    [SkippableFact]
    public async Task A_pairing_code_is_single_use_ten_minutes_long_and_replaced_by_a_newer_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var phone = factory.CreateClient();

        // Replaced: the first code dies the moment a second is shown.
        var first = await IssueAsync(manager, branch, branch.WaiterId);
        var second = await IssueAsync(manager, branch, branch.WaiterId);

        var stale = await PairAsync(phone, first, branch.WaiterPin);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal("pairing-code-expired", await CodeAsync(stale));

        // A wrong PIN leaves the code usable.
        var wrongPin = await PairAsync(phone, second, "0000");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPin.StatusCode);
        Assert.Equal("pin-invalid", await CodeAsync(wrongPin));

        Assert.Equal(HttpStatusCode.OK, (await PairAsync(phone, second, branch.WaiterPin)).StatusCode);

        // Used: a second redemption is a conflict, as a tablet's is.
        var reused = await PairAsync(factory.CreateClient(), second, branch.WaiterPin, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        // Ten minutes.
        var third = await IssueAsync(manager, branch, branch.WaiterId);
        factory.Clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));

        var expired = await PairAsync(phone, third, branch.WaiterPin);
        Assert.Equal("pairing-code-expired", await CodeAsync(expired));
    }

    [SkippableFact]
    public async Task A_code_superseded_while_it_is_being_redeemed_pairs_nothing_and_the_other_way_round()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var now = factory.Clock.UtcNow;

        // The phone has read the code and is checking the PIN; the manager taps "show a new code" and it saves first.
        await IssueAsync(manager, branch, branch.WaiterId);

        await using (var redeeming = fixture.CreateContext(factory.Clock))
        {
            var read = await redeeming.StaffEnrolmentCodes.SingleAsync(
                c => c.ForStaffMemberId == branch.WaiterId && c.RedeemedAtUtc == null && c.SupersededAtUtc == null);

            await using (var superseding = fixture.CreateContext(factory.Clock))
            {
                (await superseding.StaffEnrolmentCodes.SingleAsync(c => c.Id == read.Id)).Supersede(now);
                await superseding.SaveChangesAsync();
            }

            read.Redeem(Guid.NewGuid(), now);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => redeeming.SaveChangesAsync());
        }

        // And the other way: redeemed first, the supersession of the same code matches nothing.
        await IssueAsync(manager, branch, branch.WaiterId);

        await using (var superseding = fixture.CreateContext(factory.Clock))
        {
            var read = await superseding.StaffEnrolmentCodes.SingleAsync(
                c => c.ForStaffMemberId == branch.WaiterId && c.RedeemedAtUtc == null && c.SupersededAtUtc == null);

            await using (var redeeming = fixture.CreateContext(factory.Clock))
            {
                (await redeeming.StaffEnrolmentCodes.SingleAsync(c => c.Id == read.Id)).Redeem(Guid.NewGuid(), now);
                await redeeming.SaveChangesAsync();
            }

            read.Supersede(now);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => superseding.SaveChangesAsync());
        }

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.StaffEnrolmentCodes.AnyAsync(
            c => c.ForStaffMemberId == branch.WaiterId && c.RedeemedAtUtc != null && c.SupersededAtUtc != null));
    }

    [SkippableFact]
    public async Task A_tablet_code_pairs_no_phone_and_a_phone_code_enrols_no_tablet()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var anonymous = factory.CreateClient();

        var tabletCode = (await (await manager.PostAsJsonAsync($"/api/branches/{branch.BranchId}/devices/enrolment-codes", new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var asPhone = await PairAsync(anonymous, tabletCode, branch.WaiterPin);
        Assert.Equal("pairing-code-invalid", await CodeAsync(asPhone));

        var phoneCode = await IssueAsync(manager, branch, branch.WaiterId);
        var asTablet = await anonymous.PostAsJsonAsync(
            "/api/auth/staff/enrol", new { code = phoneCode, deviceId = $"browser-{Guid.NewGuid():N}", deviceName = "Sneaky tablet" });

        Assert.Equal(HttpStatusCode.Unauthorized, asTablet.StatusCode);
        Assert.Equal("enrolment-code-invalid", await CodeAsync(asTablet));
    }

    [SkippableFact]
    public async Task Kitchen_staff_get_no_phone()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var refused = await manager.PostAsJsonAsync(PairingsRoute(branch, cookId), new { });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("kitchen-phone-not-allowed", problem.GetProperty("code").GetString());
        Assert.Equal(cookId, problem.GetProperty("context").GetProperty("staffMemberId").GetGuid());
    }

    [SkippableFact]
    public async Task Pairing_follows_the_staff_admin_rules_of_rank_and_branch()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        PanelAccount owner, generalManager, siblingManager;
        Guid siblingBranchId, siblingWaiterId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
            siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);

            var siblingWaiter = new StaffMember(branch.VenueId, "Sibling Waiter", "+37455111222", StaffRole.Waiter, "hash", siblingBranchId);
            db.StaffMembers.Add(siblingWaiter);
            await db.SaveChangesAsync();
            siblingWaiterId = siblingWaiter.Id;
        }

        using var branchManager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var gm = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, generalManager));
        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // A branch manager pairs their own branch's waiter and their own phone, nobody above them.
        Assert.Equal(HttpStatusCode.Created, (await branchManager.PostAsJsonAsync(PairingsRoute(branch, branch.WaiterId), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await branchManager.PostAsJsonAsync(PairingsRoute(branch, branch.ManagerId), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await branchManager.PostAsJsonAsync(PairingsRoute(branch, generalManager.StaffMemberId), new { })).StatusCode);

        // A sibling branch's manager cannot reach this branch at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await sibling.PostAsJsonAsync(PairingsRoute(branch, branch.WaiterId), new { })).StatusCode);

        // Somebody of another branch is not paired here, by anybody: their phone belongs at their branch.
        Assert.Equal(HttpStatusCode.NotFound, (await gm.PostAsJsonAsync(PairingsRoute(branch, siblingWaiterId), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await gm.PostAsJsonAsync($"/api/branches/{siblingBranchId}/staff/{siblingWaiterId}/phone-pairings", new { })).StatusCode);

        // A general manager pairs a branch manager and themselves, never an owner.
        Assert.Equal(HttpStatusCode.Created, (await gm.PostAsJsonAsync(PairingsRoute(branch, branch.ManagerId), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await gm.PostAsJsonAsync(PairingsRoute(branch, generalManager.StaffMemberId), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gm.PostAsJsonAsync(PairingsRoute(branch, owner.StaffMemberId), new { })).StatusCode);

        // A waiter pairs nobody, themselves included.
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsJsonAsync(PairingsRoute(branch, branch.WaiterId), new { })).StatusCode);

        // Every successful issue left an audit row; none carries the code.
        await using var verify = fixture.CreateContext(factory.Clock);
        var rows = await verify.PlatformAuditLogs.AsNoTracking()
            .Where(a => a.Action == StaffAuthService.PhonePairingIssuedAuditAction && a.VenueId == branch.VenueId)
            .ToListAsync();

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain("code", r.ChangesJson, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Unpairing_a_phone_from_the_console_kills_its_session_now_and_the_list_shows_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var (deviceToken, sessionToken) = await PairWaiterAsync(factory, branch);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var phones = await (await manager.GetAsync($"/api/branches/{branch.BranchId}/devices?kind=2")).Content.ReadFromJsonAsync<JsonElement>();
        var listed = Assert.Single(phones.EnumerateArray());

        Assert.Equal((int)StaffDeviceKind.Phone, listed.GetProperty("kind").GetInt32());
        Assert.Equal(branch.WaiterId, listed.GetProperty("boundStaffMember").GetProperty("id").GetGuid());
        Assert.Equal("iPhone · Safari", listed.GetProperty("name").GetString());
        Assert.Equal(2, listed.GetProperty("platform").GetInt32());
        Assert.Equal(1, listed.GetProperty("browser").GetInt32());

        var tablets = await (await manager.GetAsync($"/api/branches/{branch.BranchId}/devices?kind=1")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(tablets.EnumerateArray());

        var deviceId = listed.GetProperty("id").GetGuid();

        // Subscribed to pushes: the endpoint goes with the phone.
        using (var signedIn = factory.CreateClientWithToken(sessionToken))
        {
            var (endpoint, p256dh, auth) = StaffPushTests.NewBrowserSubscription("fcm.googleapis.com");
            (await signedIn.PutAsJsonAsync(
                    "/api/staff/device/push-subscription", new { endpoint, keys = new { p256dh, auth } }))
                .EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"/api/branches/{branch.BranchId}/devices/{deviceId}/revoke", null)).StatusCode);

        using var session = factory.CreateClientWithToken(sessionToken);
        using var device = factory.CreateClientWithToken(deviceToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync($"/api/branches/{branch.BranchId}/tables/floor")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/auth/staff/device")).StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.True(await verify.PlatformAuditLogs.AnyAsync(
            a => a.Action == StaffAuthService.PhoneUnpairedAuditAction && a.TargetId == branch.WaiterId));
        Assert.False(await verify.StaffPushSubscriptions.AnyAsync(s => s.StaffDeviceId == deviceId));
    }

    [SkippableFact]
    public async Task Deactivating_somebody_unpairs_their_phone()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var (deviceToken, sessionToken) = await PairWaiterAsync(factory, branch);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var deactivated = await manager.PatchAsJsonAsync($"/api/venues/{branch.VenueId}/staff/{branch.WaiterId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        using var session = factory.CreateClientWithToken(sessionToken);
        using var device = factory.CreateClientWithToken(deviceToken);

        // On the next request, not at the next renewal.
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync($"/api/branches/{branch.BranchId}/tables/floor")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/auth/staff/device")).StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.StaffDevices.AnyAsync(d => d.BoundStaffMemberId == branch.WaiterId && d.RevokedAtUtc == null));
    }

    [SkippableFact]
    public async Task A_phone_unpairs_itself_but_a_tablet_does_not()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var (deviceToken, sessionToken) = await PairWaiterAsync(factory, branch);

        using var session = factory.CreateClientWithToken(sessionToken);

        // The bare device token is not enough: unpairing is the person's, signed in.
        using var bare = factory.CreateClientWithToken(deviceToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.PostAsync("/api/staff/device/unpair", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await session.PostAsync("/api/staff/device/unpair", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync("/api/auth/staff/device")).StatusCode);

        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var refused = await tablet.PostAsync("/api/staff/device/unpair", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [SkippableFact]
    public async Task The_same_browser_pairing_again_reuses_its_phone_and_a_tablets_browser_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var phone = factory.CreateClient();
        var browser = $"browser-{Guid.NewGuid():N}";

        var first = await (await PairAsync(phone, await IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin, browser))
            .Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await PairAsync(phone, await IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin, browser))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            first.GetProperty("device").GetProperty("deviceId").GetGuid(),
            second.GetProperty("device").GetProperty("deviceId").GetGuid());

        // The same browser now handed to the manager: the waiter's binding goes, the manager's comes.
        var managers = await PairAsync(phone, await IssueAsync(manager, branch, branch.ManagerId), "9182", browser);
        Assert.Equal(HttpStatusCode.OK, managers.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var live = await db.StaffDevices.AsNoTracking()
                .Where(d => d.BranchId == branch.BranchId && d.ClientDeviceId == browser && d.RevokedAtUtc == null)
                .SingleAsync();

            Assert.Equal(branch.ManagerId, live.BoundStaffMemberId);
        }

        // A browser that is one of the branch's tablets is not paired over.
        var tabletBrowser = $"browser-{Guid.NewGuid():N}";
        var tabletCode = (await (await manager.PostAsJsonAsync($"/api/branches/{branch.BranchId}/devices/enrolment-codes", new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        (await phone.PostAsJsonAsync("/api/auth/staff/enrol", new { code = tabletCode, deviceId = tabletBrowser, deviceName = "Bar" }))
            .EnsureSuccessStatusCode();

        var refused = await PairAsync(phone, await IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin, tabletBrowser);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// What the phone runs on is kept as parts for the console to word in its reader's language: pairing again replaces
    /// them whole, an app that predates them pairs with its name alone (listed without them), a number that names
    /// nothing is kept as nothing, and a tablet never has them.
    /// </summary>
    [SkippableFact]
    public async Task A_phone_says_what_it_runs_on_as_parts_beside_its_name()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var phone = factory.CreateClient();
        var browser = $"browser-{Guid.NewGuid():N}";

        (await PairAsync(phone, await IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin, browser))
            .EnsureSuccessStatusCode();
        var listed = await ListedPhoneAsync();
        Assert.Equal((2, 1), (listed.GetProperty("platform").GetInt32(), listed.GetProperty("browser").GetInt32()));

        // The same browser again, now an Android phone whose browser it did not recognise: replaced whole.
        (await PairWithAsync(new { platform = (int)StaffDevicePlatform.Android, deviceName = "Android" })).EnsureSuccessStatusCode();
        listed = await ListedPhoneAsync();
        Assert.Equal("Android", listed.GetProperty("name").GetString());
        Assert.Equal(4, listed.GetProperty("platform").GetInt32());
        Assert.False(listed.TryGetProperty("browser", out _));

        // An app that predates the parts: its name is all there is.
        (await PairWithAsync(new { deviceName = "Phone" })).EnsureSuccessStatusCode();
        listed = await ListedPhoneAsync();
        Assert.Equal("Phone", listed.GetProperty("name").GetString());
        Assert.False(listed.TryGetProperty("platform", out _));
        Assert.False(listed.TryGetProperty("browser", out _));

        // A number that names nothing - a newer app's platform, say - is kept as nothing rather than refused: the
        // sign-in routes stay outside the validation filter, and the phone is paired all the same.
        (await PairWithAsync(new { platform = 99, browser = 0, deviceName = "iPhone · Safari" })).EnsureSuccessStatusCode();
        listed = await ListedPhoneAsync();
        Assert.False(listed.TryGetProperty("platform", out _));
        Assert.False(listed.TryGetProperty("browser", out _));

        // A tablet is the manager's name and nothing else.
        await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        var tablet = Assert.Single(
            (await (await manager.GetAsync($"/api/branches/{branch.BranchId}/devices?kind=1")).Content
                .ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.False(tablet.TryGetProperty("platform", out _));
        Assert.False(tablet.TryGetProperty("browser", out _));

        async Task<HttpResponseMessage> PairWithAsync(object parts)
        {
            var body = JsonSerializer.SerializeToNode(parts)!.AsObject();
            body["code"] = await IssueAsync(manager, branch, branch.WaiterId);
            body["pin"] = branch.WaiterPin;
            body["clientDeviceId"] = browser;

            return await phone.PostAsJsonAsync("/api/auth/staff/phone-pair", body);
        }

        async Task<JsonElement> ListedPhoneAsync() =>
            Assert.Single(
                (await (await manager.GetAsync($"/api/branches/{branch.BranchId}/devices?kind=2")).Content
                    .ReadFromJsonAsync<JsonElement>()).EnumerateArray(),
                d => !d.GetProperty("isRevoked").GetBoolean());
    }

    // ------------------------------------------------------------------ helpers

    internal static string PairingsRoute(AuthBranch branch, Guid staffMemberId) =>
        $"/api/branches/{branch.BranchId}/staff/{staffMemberId}/phone-pairings";

    internal static async Task<string> IssueAsync(HttpClient manager, AuthBranch branch, Guid staffMemberId)
    {
        var issued = await manager.PostAsJsonAsync(PairingsRoute(branch, staffMemberId), new { });
        issued.EnsureSuccessStatusCode();

        return (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    /// <summary>Pairs as the staff app does: the English name, and the same as parts - an iPhone (2) in Safari (1).</summary>
    internal static Task<HttpResponseMessage> PairAsync(HttpClient phone, string code, string pin, string? browser = null) =>
        phone.PostAsJsonAsync(
            "/api/auth/staff/phone-pair",
            new
            {
                code,
                pin,
                clientDeviceId = browser ?? $"browser-{Guid.NewGuid():N}",
                deviceName = "iPhone · Safari",
                platform = (int)StaffDevicePlatform.IPhone,
                browser = (int)StaffDeviceBrowser.Safari,
            });

    /// <summary>Pairs the branch's waiter's phone, returning its device token and the waiter's session token.</summary>
    internal static async Task<(string DeviceToken, string SessionToken)> PairWaiterAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var phone = factory.CreateClient();

        var paired = await PairAsync(phone, await IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin);
        paired.EnsureSuccessStatusCode();

        var body = await paired.Content.ReadFromJsonAsync<JsonElement>();

        return (
            body.GetProperty("device").GetProperty("deviceToken").GetString()!,
            body.GetProperty("session").GetProperty("accessToken").GetString()!);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<AuthBranch> SeedBranchAsync(YallaApiFactory factory)
    {
        await using YallaDbContext db = fixture.CreateContext(factory.Clock);

        return await AuthTestData.CreateBranchAsync(db);
    }
}
