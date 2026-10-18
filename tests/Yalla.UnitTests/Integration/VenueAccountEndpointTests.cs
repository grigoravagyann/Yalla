using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue console's own account - <c>/api/venue/me</c> - through the real pipeline with real
/// tokens: an owner, general manager or branch manager reads and renames themself, changes their
/// password after proving the current one, sees where they are signed in and signs every other
/// sign-in out, and sets their own tablet PIN.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VenueAccountEndpointTests(SqlServerFixture fixture)
{
    private const string NewPassword = "a-much-better-password-now";

    [SkippableFact]
    public async Task Each_venue_rank_reads_and_renames_their_own_account_and_the_rename_is_audited()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PanelAccount owner;
        PanelAccount generalManager;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
        }

        var people = new (Guid Id, string Email, string Password, int Role, Guid? BranchId)[]
        {
            (owner.StaffMemberId, owner.Email, owner.Password, (int)StaffRole.Owner, null),
            (generalManager.StaffMemberId, generalManager.Email, generalManager.Password, (int)StaffRole.GeneralManager, null),
            (branch.ManagerId, branch.ManagerEmail, branch.ManagerPassword, (int)StaffRole.Manager, branch.BranchId),
        };

        foreach (var person in people)
        {
            using var client = factory.CreateClientWithToken(
                await AuthTestData.SignInAsync(factory, person.Email, person.Password));

            var read = await client.GetAsync("/api/venue/me");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var me = await read.Content.ReadFromJsonAsync<JsonElement>();
            // A null is left out on this API's wire, so the venue-wide ranks carry no branchId at all.
            Assert.Equal(
                person.BranchId is null
                    ? ["staffMemberId", "fullName", "email", "role", "venueId"]
                    : ["staffMemberId", "fullName", "email", "role", "venueId", "branchId"],
                me.EnumerateObject().Select(p => p.Name));
            Assert.Equal(person.Id, me.GetProperty("staffMemberId").GetGuid());
            Assert.Equal(person.Email, me.GetProperty("email").GetString());
            Assert.Equal(person.Role, me.GetProperty("role").GetInt32());
            Assert.Equal(branch.VenueId, me.GetProperty("venueId").GetGuid());
            Assert.Equal(person.BranchId, me.TryGetProperty("branchId", out var home) ? home.GetGuid() : null);

            // Blank and too long are named under the field.
            foreach (var bad in new[] { "   ", new string('x', 201) })
            {
                var refused = await client.PatchAsJsonAsync("/api/venue/me", new { fullName = bad });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                Assert.Equal(
                    "fullName",
                    (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
            }

            var name = $"  Renamed {person.Role}  ";
            var renamed = await client.PatchAsJsonAsync("/api/venue/me", new { fullName = name });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal(name.Trim(), (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fullName").GetString());

            // The same name again writes nothing.
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/api/venue/me", new { fullName = name.Trim() })).StatusCode);
        }

        await using var verify = fixture.CreateContext(factory.Clock);

        foreach (var person in people)
        {
            Assert.Equal($"Renamed {person.Role}", await verify.StaffMembers.Where(s => s.Id == person.Id).Select(s => s.FullName).FirstAsync());

            var audit = await verify.PlatformAuditLogs.AsNoTracking().SingleAsync(l => l.TargetId == person.Id);
            Assert.Equal("account.rename", audit.Action);
            Assert.Equal(person.Id, audit.ActorStaffMemberId);
            Assert.Equal(branch.VenueId, audit.VenueId);
            Assert.Equal($"Renamed {person.Role}", audit.TargetDisplay);
        }
    }

    [SkippableFact]
    public async Task A_password_change_needs_the_current_one_ends_every_session_and_answers_this_one_a_fresh_pair()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        var (hereToken, hereRefresh) = await SignInAsync(factory, branch.ManagerEmail, branch.ManagerPassword);
        var (elsewhereToken, elsewhereRefresh) = await SignInAsync(factory, branch.ManagerEmail, branch.ManagerPassword);

        using var here = factory.CreateClientWithToken(hereToken);
        using var elsewhere = factory.CreateClientWithToken(elsewhereToken);

        // Wrong current password and a short new one: both named, in one answer, and still signed in.
        var wrong = await here.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = "not-the-password-at-all", newPassword = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);

        var fields = (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(["currentPassword", "newPassword"], fields.Select(f => f.GetProperty("field").GetString()));
        Assert.Equal(["conflict", "min"], fields.Select(f => f.GetProperty("bound").GetString()));
        Assert.Equal(12, fields[1].GetProperty("min").GetInt32());

        var missing = await here.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = "", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(
            "required",
            (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("fields")[0].GetProperty("bound").GetString());

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/venue/me")).StatusCode);

        var changed = await here.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = branch.ManagerPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var pair = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["accessToken", "refreshToken", "expiresInSeconds"], pair.EnumerateObject().Select(p => p.Name));

        // Every access token from before is dead - this one and the other sign-in's.
        foreach (var stale in new[] { here, elsewhere })
        {
            var refused = await stale.GetAsync("/api/venue/me");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("session-revoked", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // The fresh pair carries on; neither old refresh token mints anything.
        using var fresh = factory.CreateClientWithToken(pair.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/venue/me")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = pair.GetProperty("refreshToken").GetString() })).StatusCode);

        foreach (var stale in new[] { hereRefresh, elsewhereRefresh })
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = stale })).StatusCode);
        }

        // The old password no longer signs in; the new one does.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword })).StatusCode);
        await SignInAsync(factory, branch.ManagerEmail, NewPassword);

        // The platform's activity feed shows the change and not the wrong guess, which is the lockout's counter.
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        var feed = await platform.GetFromJsonAsync<JsonElement>($"/api/platform/activity?venueId={branch.VenueId}&limit=100");
        var actions = feed.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("account.password-change", actions);
        Assert.DoesNotContain("account.password-change-failed", actions);

        await using var verify = fixture.CreateContext(factory.Clock);
        var rows = await verify.PlatformAuditLogs.AsNoTracking().Where(l => l.ActorStaffMemberId == branch.ManagerId).ToListAsync();
        Assert.Single(rows, l => l.Action == "account.password-change-failed");
        Assert.All(rows, l => Assert.DoesNotContain(branch.ManagerPassword, l.ChangesJson, StringComparison.Ordinal));
        Assert.All(rows, l => Assert.DoesNotContain(NewPassword, l.ChangesJson, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Ten_wrong_current_passwords_lock_the_route_until_the_oldest_leaves_the_window()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await manager.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = $"wrong-{i}-password", newPassword = NewPassword })).StatusCode);
        }

        // Right password or not.
        var locked = await manager.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = branch.ManagerPassword, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 and <= 900 }, $"Retry-After was {locked.Headers.RetryAfter}");

        var problem = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too-many-attempts", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("context").GetProperty("retryAfterSeconds").GetInt32() > 0);

        // Fifteen minutes on, the oldest guess has left the window and the right password works.
        factory.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = branch.ManagerPassword, newPassword = NewPassword })).StatusCode);
    }

    [SkippableFact]
    public async Task Sessions_list_every_live_sign_in_and_signing_the_others_out_ends_them_and_keeps_this_refresh_token()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();

        // Three sign-ins, a minute apart: this browser, a phone that later renews its token, and a laptop
        // that signs out by itself. Started three minutes back, so that no token is minted later than the
        // real clock the bearer handler checks "not before" against.
        factory.Clock.Advance(TimeSpan.FromMinutes(-3));
        var start = factory.Clock.UtcNow;
        var (hereToken, hereRefresh) = await SignInAsync(factory, branch.ManagerEmail, branch.ManagerPassword);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var (phoneToken, phoneRefresh) = await SignInAsync(factory, branch.ManagerEmail, branch.ManagerPassword);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var (_, laptopRefresh) = await SignInAsync(factory, branch.ManagerEmail, branch.ManagerPassword);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        var renewed = await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = phoneRefresh });
        renewed.EnsureSuccessStatusCode();
        var phoneRefreshNow = (await renewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString()!;

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/sign-out", new { refreshToken = laptopRefresh })).StatusCode);

        using var here = factory.CreateClientWithToken(hereToken);

        var listed = await here.GetAsync("/api/venue/me/sessions");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var sessions = (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessions").EnumerateArray().ToList();

        // This one first, then the others by when they were last active. The laptop signed out.
        Assert.Equal(2, sessions.Count);
        Assert.Equal(["id", "signedInAtUtc", "lastActiveAtUtc", "isCurrent"], sessions[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([true, false], sessions.Select(s => s.GetProperty("isCurrent").GetBoolean()));

        Assert.Equal(start, sessions[0].GetProperty("signedInAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(start, sessions[0].GetProperty("lastActiveAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));

        // The phone signed in a minute in and renewed at three.
        Assert.Equal(start.AddMinutes(1), sessions[1].GetProperty("signedInAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddMinutes(3), sessions[1].GetProperty("lastActiveAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));

        // ---- sign the others out
        var ended = await here.PostAsync("/api/venue/me/sessions/sign-out-others", content: null);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal(1, (await ended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endedSessions").GetInt32());

        // The phone is out now, not in fifteen minutes: its access token and its refresh token both.
        using var phone = factory.CreateClientWithToken(phoneToken);
        var phoneRefused = await phone.GetAsync("/api/venue/me");
        Assert.Equal(HttpStatusCode.Unauthorized, phoneRefused.StatusCode);
        Assert.Equal("session-revoked", (await phoneRefused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = phoneRefreshNow })).StatusCode);

        // This access token ended with the generation, but this sign-in's refresh token carries on.
        Assert.Equal(HttpStatusCode.Unauthorized, (await here.GetAsync("/api/venue/me")).StatusCode);

        var carriedOn = await anonymous.PostAsJsonAsync("/api/auth/venue/refresh", new { refreshToken = hereRefresh });
        Assert.Equal(HttpStatusCode.OK, carriedOn.StatusCode);

        using var again = factory.CreateClientWithToken(
            (await carriedOn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        var after = (await again.GetFromJsonAsync<JsonElement>("/api/venue/me/sessions")).GetProperty("sessions").EnumerateArray().ToList();
        var only = Assert.Single(after);
        Assert.True(only.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(sessions[0].GetProperty("id").GetGuid(), only.GetProperty("id").GetGuid());

        // Nobody else left: a second press ends nothing.
        var none = await again.PostAsync("/api/venue/me/sessions/sign-out-others", content: null);
        Assert.Equal(0, (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endedSessions").GetInt32());

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.Contains(
            await verify.PlatformAuditLogs.AsNoTracking().Where(l => l.ActorStaffMemberId == branch.ManagerId).Select(l => l.Action).ToListAsync(),
            a => a == "account.sign-out-others");
    }

    [SkippableFact]
    public async Task Each_venue_rank_sets_their_own_tablet_pin_through_the_staff_route()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        PanelAccount owner;
        PanelAccount generalManager;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
        }

        var people = new (Guid Id, string Email, string Password, string Pin)[]
        {
            (owner.StaffMemberId, owner.Email, owner.Password, "5101"),
            (generalManager.StaffMemberId, generalManager.Email, generalManager.Password, "5202"),
            (branch.ManagerId, branch.ManagerEmail, branch.ManagerPassword, "5303"),
        };

        foreach (var person in people)
        {
            using var client = factory.CreateClientWithToken(
                await AuthTestData.SignInAsync(factory, person.Email, person.Password));

            var me = await client.GetFromJsonAsync<JsonElement>("/api/venue/me");
            var set = await client.PostAsJsonAsync(
                $"/api/venues/{me.GetProperty("venueId").GetGuid()}/staff/{me.GetProperty("staffMemberId").GetGuid()}/pin",
                new { pin = person.Pin });

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        // The waiter's PIN is on this branch's tablet, so nobody whose PIN reaches it may take it.
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var taken = await manager.PostAsJsonAsync($"/api/venues/{branch.VenueId}/staff/{branch.ManagerId}/pin", new { pin = branch.WaiterPin });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("pin-taken", (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var verify = fixture.CreateContext(factory.Clock);
        var hasher = new SecretHasher();

        foreach (var person in people)
        {
            var hash = await verify.StaffMembers.Where(s => s.Id == person.Id).Select(s => s.PinHash).FirstAsync();
            Assert.True(hasher.Verify(hash, person.Pin).Matches);
        }
    }

    [SkippableFact]
    public async Task A_managers_pin_session_on_a_tablet_has_no_account_screen()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        // The same manager, by PIN on the floor rather than by password in the panel. Their row has a
        // password, so only the policy - the admin panel's identity type - stands between this token and
        // changing it.
        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.EnrolDeviceAsync(factory, branch));
        var session = await tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.ManagerId, pin = "9182" });
        session.EnsureSuccessStatusCode();

        using var floor = factory.CreateClientWithToken(
            (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.Forbidden, (await floor.GetAsync("/api/venue/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await floor.GetAsync("/api/venue/me/sessions")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await floor.PostAsJsonAsync("/api/venue/me/password", new { currentPassword = branch.ManagerPassword, newPassword = NewPassword })).StatusCode);
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(YallaApiFactory factory, string email, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/venue/sign-in", new { email, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }
}
