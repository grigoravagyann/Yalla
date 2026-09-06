using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The two boundaries that are the real security of this system.
/// </summary>
/// <remarks>
/// <para>
/// Everything else here is a sign-in flow; these two are what the sign-in flows exist to make
/// enforceable. A chain has several branches and staff belong to one, and two adjacent tables
/// must not be able to order on each other's bill.
/// </para>
/// <para>
/// Both are tested through the real pipeline, because the failure they guard against is not "the
/// policy got the answer wrong" - it is "the policy was never applied to the endpoint", which no
/// unit test of a handler can see.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class AuthorizationBoundaryTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A waiter's tablet is enrolled to one branch, and the branch claim on their session is
    /// copied from that tablet. There is nothing they can send that changes it.
    /// </summary>
    [SkippableFact]
    public async Task A_staff_token_for_branch_A_is_refused_on_branch_B()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branchA;
        AuthBranch branchB;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branchA = await AuthTestData.CreateBranchAsync(db);
            branchB = await AuthTestData.CreateBranchAsync(db);
        }

        var tokenForA = await StaffAuthTests.SignInWaiterAsync(factory, branchA);
        using var staff = factory.CreateClientWithToken(tokenForA);

        // Their own branch works, so the refusal below is about the boundary and not about the
        // token being broken.
        var ownFloor = await staff.GetAsync($"/api/branches/{branchA.BranchId}/tables/floor");
        Assert.Equal(HttpStatusCode.OK, ownFloor.StatusCode);

        var otherFloor = await staff.GetAsync($"/api/branches/{branchB.BranchId}/tables/floor");
        Assert.Equal(HttpStatusCode.Forbidden, otherFloor.StatusCode);

        // Not just the read. The transitions from the previous task carry the same policy.
        var seat = await staff.PostAsJsonAsync(
            $"/api/branches/{branchB.BranchId}/tables/{branchB.FirstTableId}/seat-walk-in",
            new { partySize = 2, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, seat.StatusCode);

        // And the admin surface, where a manager of one branch must not enrol a tablet at another.
        var managerForA = await StaffAuthTests.SignInManagerAsync(factory, branchA);
        using var manager = factory.CreateClientWithToken(managerForA);

        var enrol = await manager.PostAsJsonAsync(
            $"/api/branches/{branchB.BranchId}/devices/enrolment-codes", new { });

        Assert.Equal(HttpStatusCode.Forbidden, enrol.StatusCode);
    }

    /// <summary>
    /// Two adjacent tables must not be able to order on each other's bill.
    /// </summary>
    [SkippableFact]
    public async Task A_tab_participant_token_is_refused_on_another_tab()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        AuthTab tableSeven;
        AuthTab tableEight;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            tableSeven = await AuthTestData.CreateOpenTabAsync(
                db, branch, branch.TableIds[0], factory.Clock.UtcNow);

            tableEight = await AuthTestData.CreateOpenTabAsync(
                db, branch, branch.TableIds[1], factory.Clock.UtcNow);
        }

        var tokenForSeven = await JoinAsync(factory, tableSeven.JoinToken);
        using var diner = factory.CreateClientWithToken(tokenForSeven);

        var ownTab = await diner.GetAsync($"/api/tabs/{tableSeven.TabId}");
        Assert.Equal(HttpStatusCode.OK, ownTab.StatusCode);

        // The claim names table seven's tab. There is no request shape that makes it name another.
        var neighboursTab = await diner.GetAsync($"/api/tabs/{tableEight.TabId}");
        Assert.Equal(HttpStatusCode.Forbidden, neighboursTab.StatusCode);

        var rename = await diner.PostAsJsonAsync(
            $"/api/tabs/{tableEight.TabId}/display-name", new { displayName = "Not mine" });

        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    /// <summary>
    /// A participant token is not a staff token, however the request is addressed. It carries a
    /// branch claim - the tab's branch - so this also proves the branch policy is not the only
    /// thing standing between a diner and the floor plan.
    /// </summary>
    [SkippableFact]
    public async Task A_tab_participant_token_cannot_reach_the_staff_surface()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        AuthTab tab;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[0], factory.Clock.UtcNow);
        }

        var token = await JoinAsync(factory, tab.JoinToken);
        using var diner = factory.CreateClientWithToken(token);

        var floor = await diner.GetAsync($"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(HttpStatusCode.Forbidden, floor.StatusCode);
    }

    /// <summary>An unauthenticated caller gets 401 rather than reaching anything.</summary>
    [SkippableFact]
    public async Task The_staff_surface_refuses_an_anonymous_caller()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/branches/{Guid.CreateVersion7()}/tables/floor");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The PIN exchange is refused by the pipeline, not by a null check in its handler.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the failure mode is silent. The other auth routes are mapped in
    /// a group marked <c>AllowAnonymous</c>, and an <c>IAllowAnonymous</c> anywhere in an
    /// endpoint's metadata makes the authorization middleware treat the endpoint as succeeded -
    /// so a <c>RequireAuthorization</c> added on top of it would look protective and do nothing.
    /// The <c>WWW-Authenticate</c> header is how the two are told apart: it is written by the
    /// bearer scheme's challenge, which only runs when authorization actually refused the request.
    /// </remarks>
    [SkippableFact]
    public async Task The_pin_exchange_is_refused_by_authorization_and_not_by_the_handler()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = Guid.CreateVersion7(), pin = "1234" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    /// <summary>
    /// A waiter at one branch cannot acknowledge another branch's service requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route is <c>/api/service-requests/{id}/acknowledge</c>. It names no branch and no tab,
    /// so <c>BranchScoped</c> cannot be applied to it - the handler resolves a branch from
    /// <c>branchId</c>, <c>tableId</c> or <c>tabId</c>, this route has none of them, and the policy
    /// would fail closed on every call. The service-level guard is therefore the only thing
    /// standing here, which is why this test is at the HTTP level: the failure mode it guards
    /// against is not "the guard got the answer wrong", it is "there is no guard".
    /// </para>
    /// <para>
    /// The refusal has to leave the request <i>unacknowledged</i>, not merely answer 403. An
    /// acknowledged request drops out of <c>GET /api/branches/{id}/service-requests</c>, so the
    /// damage is that a table's call for the bill vanishes off the floor screen while the diner's
    /// app says somebody is coming - silent in both directions.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_waiter_at_another_branch_cannot_acknowledge_a_service_request()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branchA;
        AuthBranch branchB;
        AuthTab tabAtB;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branchA = await AuthTestData.CreateBranchAsync(db);
            branchB = await AuthTestData.CreateBranchAsync(db);

            tabAtB = await AuthTestData.CreateOpenTabAsync(
                db, branchB, branchB.FirstTableId, factory.Clock.UtcNow);
        }

        // The diner at branch B asks for the bill. The id comes straight back in the 201, which is
        // also how anybody holding a participant token learns it - scanning the QR code at that
        // table is a public flow, so this is not a guessing game.
        using var diner = factory.CreateClientWithToken(await JoinAsync(factory, tabAtB.JoinToken));

        var raised = await diner.PostAsJsonAsync(
            $"/api/tabs/{tabAtB.TabId}/service-requests",
            new { preset = (int)ServiceRequestPreset.TheBill });

        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);

        var serviceRequestId = (await raised.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("serviceRequestId").GetGuid();

        // A waiter at branch A holds a perfectly good staff token. It is simply not for this
        // branch, and this endpoint never used to look.
        using var waiterAtA = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branchA));

        var poached = await waiterAtA.PostAsJsonAsync(
            $"/api/service-requests/{serviceRequestId}/acknowledge", new { });

        Assert.Equal(HttpStatusCode.Forbidden, poached.StatusCode);

        using var waiterAtB = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branchB));

        // Still on branch B's floor screen. This is the assertion that matters.
        var open = await waiterAtB.GetAsync($"/api/branches/{branchB.BranchId}/service-requests");

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        var rows = await open.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            rows.EnumerateArray(),
            row => row.GetProperty("serviceRequestId").GetGuid() == serviceRequestId);

        // And the waiter who does work there is not locked out by the guard.
        var acknowledged = await waiterAtB.PostAsJsonAsync(
            $"/api/service-requests/{serviceRequestId}/acknowledge", new { });

        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
    }

    /// <summary>
    /// A staff token for one branch cannot settle or write off another branch's bill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The money path, and the one where getting this wrong costs actual cash: marking a tab paid
    /// closes it with nothing in the drawer, and writing one off erases the balance outright. Both
    /// routes carry <c>tabId</c>, so <c>BranchScoped</c> can cover them and now does - the service
    /// guard is checked underneath it as well, because the policy resolves the branch through a
    /// database read and a route that stopped carrying <c>tabId</c> would silently lose the cover.
    /// </para>
    /// <para>
    /// The controls assert <b>not</b> 403 rather than a specific success: the fixture tab has no
    /// orders on it, so a cash payment against a zero balance is legitimately refused on its own
    /// terms. What is being tested is who gets past authorisation, not what the arithmetic says.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_staff_token_for_branch_A_cannot_settle_branch_Bs_bill()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branchA;
        AuthBranch branchB;
        AuthTab tabAtB;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branchA = await AuthTestData.CreateBranchAsync(db);
            branchB = await AuthTestData.CreateBranchAsync(db);

            tabAtB = await AuthTestData.CreateOpenTabAsync(
                db, branchB, branchB.FirstTableId, factory.Clock.UtcNow);
        }

        using var waiterAtA = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branchA));

        var cash = await waiterAtA.PostAsJsonAsync(
            $"/api/tabs/{tabAtB.TabId}/payments/cash",
            new { amountAmd = 5_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, cash.StatusCode);

        using var managerAtA = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branchA));

        var writeOff = await managerAtA.PostAsJsonAsync(
            $"/api/tabs/{tabAtB.TabId}/abandon", new { reason = "not my venue" });

        Assert.Equal(HttpStatusCode.Forbidden, writeOff.StatusCode);

        // Nothing was taken and nothing was written off.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var tab = await db.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabAtB.TabId);

            Assert.Equal(TabStatus.Open, tab.Status);

            Assert.Empty(
                await db.Payments.AsNoTracking().Where(p => p.TabId == tabAtB.TabId).ToListAsync());
        }

        // The staff who do work there get past the boundary.
        using var waiterAtB = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branchB));

        var ownCash = await waiterAtB.PostAsJsonAsync(
            $"/api/tabs/{tabAtB.TabId}/payments/cash",
            new { amountAmd = 5_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });

        Assert.NotEqual(HttpStatusCode.Forbidden, ownCash.StatusCode);

        using var managerAtB = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branchB));

        var ownWriteOff = await managerAtB.PostAsJsonAsync(
            $"/api/tabs/{tabAtB.TabId}/abandon", new { reason = "walked out" });

        Assert.NotEqual(HttpStatusCode.Forbidden, ownWriteOff.StatusCode);
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static async Task<string> JoinAsync(YallaApiFactory factory, string joinToken)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/tabs/join",
            new { joinToken, deviceId = $"device-{Guid.NewGuid():N}", displayName = "Ani" });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetProperty("accessToken").GetString()!;
    }
}
