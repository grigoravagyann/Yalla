using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Identity type 3: an enrolled tablet plus a per-person PIN, and the audit trail that only works
/// because the PIN is per person.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class StaffAuthTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_device_enrols_once_and_a_second_redemption_of_the_code_fails()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);

        var managerToken = await SignInManagerAsync(factory, branch);
        using var manager = factory.CreateClientWithToken(managerToken);

        var created = await manager.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/devices/enrolment-codes", new { });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var code = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString();

        Assert.False(string.IsNullOrWhiteSpace(code));

        using var tablet = factory.CreateClient();

        var enrolled = await tablet.PostAsJsonAsync(
            "/api/auth/staff/enrol", new { code, deviceName = "Bar tablet" });

        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);

        var device = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(branch.BranchId, device.GetProperty("branchId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(device.GetProperty("deviceToken").GetString()));

        // Enrolment codes get read out across a bar and will be overheard. Single use is what
        // makes that survivable.
        var second = await tablet.PostAsJsonAsync(
            "/api/auth/staff/enrol", new { code, deviceName = "Somebody else's tablet" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [SkippableFact]
    public async Task A_pin_exchanges_the_device_token_for_a_staff_session()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var deviceToken = await EnrolDeviceAsync(factory, branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);

        var response = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin",
            new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(branch.WaiterId, session.GetProperty("staffMemberId").GetGuid());
        Assert.Equal(branch.BranchId, session.GetProperty("branchId").GetGuid());

        // Roles are integers on the wire, like every other enum: 3 is Waiter.
        Assert.Equal((int)StaffRole.Waiter, session.GetProperty("role").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("renewalToken").GetString()));
    }

    [SkippableFact]
    public async Task Wrong_pins_lock_the_staff_member_out_and_a_manager_can_clear_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var deviceToken = await EnrolDeviceAsync(factory, branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);

        // Five is the configured PinMaxAttempts. The fifth failure is the one that locks.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var wrong = await SendPinAsync(tablet, branch.WaiterId, "0000");

            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal("pin-invalid", await ErrorCodeAsync(wrong));
        }

        var locking = await SendPinAsync(tablet, branch.WaiterId, "0000");

        // 403 with its own code, not another 401. Reporting a lockout as a wrong PIN leaves
        // someone standing at a tablet retyping digits that were correct all along.
        Assert.Equal(HttpStatusCode.Forbidden, locking.StatusCode);
        Assert.Equal("account-locked", await ErrorCodeAsync(locking));

        // The correct PIN is refused too, which is the whole point of a lockout.
        var correct = await SendPinAsync(tablet, branch.WaiterId, branch.WaiterPin);
        Assert.Equal(HttpStatusCode.Forbidden, correct.StatusCode);

        // A manager clears it. This is the path that actually gets used mid-service: a waiter who
        // fat-fingered their PIN during a rush cannot be made to wait out a timer.
        var managerToken = await SignInManagerAsync(factory, branch);
        using var manager = factory.CreateClientWithToken(managerToken);

        var cleared = await manager.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/staff/{branch.WaiterId}/clear-pin-lockout", new { });

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        var afterClear = await SendPinAsync(tablet, branch.WaiterId, branch.WaiterPin);
        Assert.Equal(HttpStatusCode.OK, afterClear.StatusCode);
    }

    [SkippableFact]
    public async Task A_revoked_device_token_is_rejected()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var deviceToken = await EnrolDeviceAsync(factory, branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);

        // It works before revocation, so the assertion afterwards means something.
        var before = await SendPinAsync(tablet, branch.WaiterId, branch.WaiterPin);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var sessionToken = (await before.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;

        Guid deviceId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            deviceId = await db.StaffDevices
                .Where(d => d.BranchId == branch.BranchId)
                .Select(d => d.Id)
                .SingleAsync();
        }

        var managerToken = await SignInManagerAsync(factory, branch);
        using var manager = factory.CreateClientWithToken(managerToken);

        var revoked = await manager.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/devices/{deviceId}/revoke", new { });

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // A tablet in a taxi stops working on its next request, not when its year-long token
        // expires.
        var after = await SendPinAsync(tablet, branch.WaiterId, branch.WaiterPin);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // And so does the session token already minted on it, because the session is the thing
        // that can act.
        using var staff = factory.CreateClientWithToken(sessionToken);
        var floor = await staff.GetAsync($"/api/branches/{branch.BranchId}/tables/floor");

        Assert.Equal(HttpStatusCode.Unauthorized, floor.StatusCode);
    }

    /// <summary>
    /// The reason per-person PINs are worth the extra taps: the audit log names a person.
    /// </summary>
    [SkippableFact]
    public async Task A_table_state_change_after_a_real_sign_in_names_the_staff_member()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        var sessionToken = await SignInWaiterAsync(factory, branch);

        using var staff = factory.CreateClientWithToken(sessionToken);

        var commandId = Guid.CreateVersion7();

        var seated = await staff.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.FirstTableId}/seat-walk-in",
            new { partySize = 2, clientCommandId = commandId, reason = "seated walk-in" });

        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);

        var change = await db.TableStateChanges
            .AsNoTracking()
            .SingleAsync(c => c.ClientCommandId == commandId);

        // Not the development stub's seeded waiter, and not null: the staff member whose PIN was
        // tapped on the tablet.
        Assert.Equal(ActorType.Staff, change.ActorType);
        Assert.Equal(branch.WaiterId, change.ActorId);
        Assert.Equal(TableStatus.Occupied, change.ToStatus);
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<AuthBranch> SeedBranchAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await AuthTestData.CreateBranchAsync(db);
    }

    /// <summary>Signs the manager in to the admin panel and returns their access token.</summary>
    internal static async Task<string> SignInManagerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in",
            new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    /// <summary>Enrols a tablet the way a manager would, and returns its device token.</summary>
    internal static async Task<string> EnrolDeviceAsync(YallaApiFactory factory, AuthBranch branch)
    {
        var managerToken = await SignInManagerAsync(factory, branch);

        using var manager = factory.CreateClientWithToken(managerToken);
        var created = await manager.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/devices/enrolment-codes", new { });

        created.EnsureSuccessStatusCode();

        var code = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString();

        using var tablet = factory.CreateClient();
        var enrolled = await tablet.PostAsJsonAsync(
            "/api/auth/staff/enrol", new { code, deviceName = "Test tablet" });

        enrolled.EnsureSuccessStatusCode();

        return (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceToken").GetString()!;
    }

    /// <summary>Enrols a tablet and taps the waiter's PIN, returning the session access token.</summary>
    internal static async Task<string> SignInWaiterAsync(YallaApiFactory factory, AuthBranch branch)
    {
        var deviceToken = await EnrolDeviceAsync(factory, branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);
        var response = await SendPinAsync(tablet, branch.WaiterId, branch.WaiterPin);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> SendPinAsync(HttpClient tablet, Guid staffMemberId, string pin) =>
        tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId, pin });

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        return problem.GetProperty("code").GetString();
    }
}
