using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A staff member's credential and their rank, across the two surfaces that own them.
/// </summary>
/// <remarks>
/// <para>
/// <c>MenuAndStaffManagementTests</c> proves the role hierarchy against the service, and
/// <c>VenueAdminEndpointTests</c> proves the staff routes carry their policies. Neither can reach
/// what these are about, because every one of them <b>spans two surfaces</b>: a manager changes
/// something in the admin panel, and the question is what the tablet can do next. A PIN reset that
/// answers 200 and leaves the old PIN working is a green service test and a locked-out waiter -
/// or, worse, a former one who still gets in.
/// </para>
/// <para>
/// The last of these is the reason the file exists. Deactivating somebody refused their PIN and
/// did nothing whatever to the session already open on the tablet, which renewed every thirty
/// minutes indefinitely. Nothing below the endpoints could see it: the PIN path and the renewal
/// path are different methods, and only a test that signs in, deactivates, and then renews puts
/// the two together.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class StaffLifecycleEndpointTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A reset PIN is the PIN. The old one stops working in the same moment.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they fail in opposite directions. A reset that does not take leaves
    /// a waiter typing a PIN their manager just told them and being refused; a reset that leaves
    /// the old one live means a PIN reset is not a way to take a credential back.
    /// </remarks>
    [SkippableFact]
    public async Task A_reset_pin_signs_in_and_the_one_it_replaced_stops_working()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var tablet = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, branch));

        // The PIN they had works before anything changes, so a later refusal means the reset and
        // not something wrong with the fixture.
        Assert.Equal(HttpStatusCode.OK, (await PinAsync(tablet, branch.WaiterId, branch.WaiterPin)).StatusCode);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        const string Fresh = "570193";

        var reset = await manager.PostAsJsonAsync(
            $"/api/venues/{branch.VenueId}/staff/{branch.WaiterId}/pin", new { pin = Fresh });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var old = await PinAsync(tablet, branch.WaiterId, branch.WaiterPin);

        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal("pin-invalid", await CodeAsync(old));

        var now = await PinAsync(tablet, branch.WaiterId, Fresh);

        Assert.Equal(HttpStatusCode.OK, now.StatusCode);

        var session = await now.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(branch.WaiterId, session.GetProperty("staffMemberId").GetGuid());
        Assert.Equal((int)StaffRole.Waiter, session.GetProperty("role").GetInt32());
    }

    /// <summary>
    /// A PIN outside four to eight digits is refused by name, and the credential it would have
    /// replaced is left alone.
    /// </summary>
    /// <remarks>
    /// <c>SetPinRequest</c> carries no validation attributes at all - the bound lives in
    /// <c>ValidPin</c>, below the endpoint - so what arrives at a caller is whatever the exception
    /// mapper makes of an <c>ArgumentException</c>. The second half is the assertion that is easy
    /// to leave out: a refused reset that had already cleared the stored hash would lock somebody
    /// out with a 400 that said the request failed.
    /// </remarks>
    [SkippableFact]
    public async Task A_pin_outside_the_declared_bounds_is_refused_and_leaves_the_old_one_standing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var route = $"/api/venues/{branch.VenueId}/staff/{branch.WaiterId}/pin";

        // Too short, too long, not digits, and absent altogether - the last of which binds null and
        // used to be the shape that reached a lookup rather than a refusal.
        foreach (var body in new object[]
                 {
                     new { pin = "123" },
                     new { pin = "1234567890" },
                     new { pin = "abcd" },
                     new { },
                 })
        {
            var refused = await manager.PostAsJsonAsync(route, body);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("invalid-request", problem.GetProperty("code").GetString());

            // Named, so the console can put the message under the field rather than at the top.
            Assert.Contains(
                "PIN is 4 to 8 digits",
                problem.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
        }

        using var tablet = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.OK,
            (await PinAsync(tablet, branch.WaiterId, branch.WaiterPin)).StatusCode);
    }

    /// <summary>
    /// Deactivating somebody takes them off the floor: the PIN is refused <b>and</b> the session
    /// already open on the tablet stops renewing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half is what this file was written for. <c>SignInWithPinAsync</c> has always
    /// checked <c>IsActive</c>; <c>RenewSessionAsync</c> did not, and a staff session renews every
    /// thirty minutes. So deactivating a waiter refused a PIN nobody was going to type - their
    /// tablet was already signed in - while that tablet went on renewing for as long as anyone
    /// kept using it, with a fresh access token each time and full access to the branch floor.
    /// </para>
    /// <para>
    /// Both halves are asserted because either alone is satisfied by a system that is still
    /// broken: the PIN check was passing throughout.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Deactivating_a_staff_member_refuses_their_pin_and_ends_the_session_they_already_have()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var tablet = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, branch));

        var signedIn = await PinAsync(tablet, branch.WaiterId, branch.WaiterPin);

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        var session = await signedIn.Content.ReadFromJsonAsync<JsonElement>();
        var renewalToken = session.GetProperty("renewalToken").GetString();

        // The session works before anything changes.
        using var waiter = factory.CreateClientWithToken(session.GetProperty("accessToken").GetString()!);

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.GetAsync($"/api/branches/{branch.BranchId}/tables/floor")).StatusCode);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var deactivated = await manager.PatchAsJsonAsync(
            $"/api/venues/{branch.VenueId}/staff/{branch.WaiterId}", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False((await deactivated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isActive").GetBoolean());

        // The PIN, which was always refused.
        var pin = await PinAsync(tablet, branch.WaiterId, branch.WaiterPin);

        Assert.Equal(HttpStatusCode.Unauthorized, pin.StatusCode);

        // And the session behind it, which was not.
        using var anonymous = factory.CreateClient();

        var renewed = await anonymous.PostAsJsonAsync(
            "/api/auth/staff/renew", new { renewalToken });

        Assert.Equal(HttpStatusCode.Unauthorized, renewed.StatusCode);
        Assert.Equal("account-inactive", await CodeAsync(renewed));

        // Ended rather than merely refused once, so the handle is spent even if they are put back.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/staff/renew", new { renewalToken })).StatusCode);
    }

    /// <summary>
    /// A promotion reaches the next sign-in and not the session already open, and a manager cannot
    /// hand out their own rank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The role is a claim, minted when the PIN was tapped, so a live token cannot gain rank -
    /// which is the safe direction, and worth pinning as the deliberate behaviour it is rather
    /// than leaving somebody to discover it as a bug report about a promotion "not working".
    /// </para>
    /// <para>
    /// The refusal below it is a different rule: <c>MayAssign</c> stops a manager creating a peer,
    /// and it is enforced on the patch as well as on the create. Only the create side had a test.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_promotion_reaches_the_next_sign_in_and_a_manager_cannot_grant_their_own_rank()
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

        using var tablet = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, branch));

        var before = await (await PinAsync(tablet, branch.WaiterId, branch.WaiterPin))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)StaffRole.Waiter, before.GetProperty("role").GetInt32());

        var staffRoute = $"/api/venues/{branch.VenueId}/staff";

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        // A manager may not make somebody a manager, on the patch exactly as on the create.
        var refused = await manager.PatchAsJsonAsync(
            $"{staffRoute}/{branch.WaiterId}", new { role = StaffRole.Manager });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var promoted = await platform.PatchAsJsonAsync(
            $"{staffRoute}/{branch.WaiterId}", new { role = StaffRole.Manager });

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal(
            (int)StaffRole.Manager,
            (await promoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetInt32());

        // The token in their hand still says Waiter, and no manager route opens to it. Rank is
        // minted at the PIN, so it cannot be gained without tapping it again.
        using var stale = factory.CreateClientWithToken(before.GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.Forbidden, (await stale.GetAsync(staffRoute)).StatusCode);

        // Tapping it again is what collects the promotion.
        var after = await (await PinAsync(tablet, branch.WaiterId, branch.WaiterPin))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)StaffRole.Manager, after.GetProperty("role").GetInt32());

        using var promotedClient = factory.CreateClientWithToken(
            after.GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.OK, (await promotedClient.GetAsync(staffRoute)).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static Task<HttpResponseMessage> PinAsync(HttpClient tablet, Guid staffMemberId, string pin) =>
        tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId, pin });

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
