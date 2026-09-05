using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests;

/// <summary>
/// Direct tests for the two policies no endpoint carries yet.
/// </summary>
/// <remarks>
/// <para>
/// Five of the seven policies are exercised end to end by the boundary and staff tests, which is
/// the right way round: what matters about a policy is whether it is actually attached to a
/// route.
/// </para>
/// <para>
/// <c>TabParticipantCanOrder</c> and <c>VenueScoped</c> have no route to be attached to yet -
/// ordering is a later task and there are no venue-level endpoints - so they are tested here
/// instead. A policy that ships defined, unapplied and unexercised is a policy that will be
/// wrong the day somebody applies it.
/// </para>
/// </remarks>
public class PolicyHandlerTests
{
    private static readonly Guid TabId = Guid.CreateVersion7();
    private static readonly Guid ParticipantId = Guid.CreateVersion7();
    private static readonly Guid BranchId = Guid.CreateVersion7();
    private static readonly Guid VenueId = Guid.CreateVersion7();

    [Fact]
    public async Task CanOrder_succeeds_for_a_participant_allowed_to_order()
    {
        var context = await EvaluateTabAsync(mustOrder: true, canOrder: true);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task CanOrder_refuses_a_participant_who_may_not_order()
    {
        var context = await EvaluateTabAsync(mustOrder: true, canOrder: false);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// The plain <c>TabParticipant</c> policy does not care about ordering, which is the whole
    /// difference between the two.
    /// </summary>
    [Fact]
    public async Task The_plain_tab_policy_ignores_the_ordering_flag()
    {
        var context = await EvaluateTabAsync(mustOrder: false, canOrder: false);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task A_removed_participant_is_refused_even_with_a_valid_token()
    {
        var context = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, participantStatus: ParticipantStatus.Removed);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// The token's own expiry is a ceiling. The real lifetime is the tab, plus long enough to
    /// read the receipt.
    /// </summary>
    [Fact]
    public async Task A_participant_may_still_read_a_tab_that_closed_within_the_grace_period()
    {
        var now = new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

        var context = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, nowUtc: now, tabClosedAtUtc: now.AddMinutes(-30));

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task A_participant_is_refused_once_the_receipt_grace_period_has_run_out()
    {
        var now = new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

        // The configured grace is 120 minutes.
        var context = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, nowUtc: now, tabClosedAtUtc: now.AddMinutes(-121));

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task VenueScoped_admits_a_manager_acting_in_their_own_venue()
    {
        var context = await EvaluateVenueAsync(
            role: StaffRole.Manager, tokenVenueId: VenueId, routeVenueId: VenueId);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task VenueScoped_refuses_a_manager_acting_in_someone_elses_venue()
    {
        var context = await EvaluateVenueAsync(
            role: StaffRole.Manager, tokenVenueId: VenueId, routeVenueId: Guid.CreateVersion7());

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// A waiter has a venue and a role, so the only thing stopping them is the role check. This is
    /// the test that says the role check is there.
    /// </summary>
    [Fact]
    public async Task VenueScoped_refuses_a_waiter_in_their_own_venue()
    {
        var context = await EvaluateVenueAsync(
            role: StaffRole.Waiter, tokenVenueId: VenueId, routeVenueId: VenueId);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// The venue can be implied by the branch being addressed rather than named directly, and the
    /// same claim has to be checked either way.
    /// </summary>
    [Fact]
    public async Task VenueScoped_resolves_the_venue_from_the_branch_in_the_route()
    {
        var context = await EvaluateVenueAsync(
            role: StaffRole.Owner, tokenVenueId: VenueId, routeVenueId: null, routeBranchId: BranchId);

        Assert.True(context.HasSucceeded);
    }

    /// <summary>
    /// Test 17. A tab being settled can be read but not changed - somebody who has paid their share
    /// and left must not find it moving behind them.
    /// </summary>
    [Fact]
    public async Task A_closing_tab_can_be_read_but_not_mutated()
    {
        var readable = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, tabStatus: TabStatus.Closing);

        Assert.True(readable.HasSucceeded);

        var mutating = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, mustMutate: true, tabStatus: TabStatus.Closing);

        Assert.False(mutating.HasSucceeded);

        // While it is open, the same request is fine.
        var open = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, mustMutate: true, tabStatus: TabStatus.Open);

        Assert.True(open.HasSucceeded);
    }

    /// <summary>
    /// Test 15, at the policy. A removed participant keeps the token they were issued; the approval
    /// flow only means something if that token stops working.
    /// </summary>
    [Fact]
    public async Task A_removed_participant_is_refused_reads_and_mutations_alike()
    {
        var read = await EvaluateTabAsync(
            mustOrder: false, canOrder: true, participantStatus: ParticipantStatus.Removed);

        Assert.False(read.HasSucceeded);

        var order = await EvaluateTabAsync(
            mustOrder: true, canOrder: true, participantStatus: ParticipantStatus.Removed);

        Assert.False(order.HasSucceeded);
    }

    // ------------------------------------------------------------ the platform tier

    /// <summary>
    /// Test case 2. A platform admin belongs to no venue and no branch, and passes BranchScoped for
    /// a branch they have no relationship to. Decided inside the handler - no call site checks a role.
    /// </summary>
    [Fact]
    public async Task BranchScoped_admits_a_platform_admin_for_a_branch_they_have_no_relationship_to()
    {
        var context = await EvaluateBranchAsync(
            role: StaffRole.PlatformAdmin, tokenVenueId: null, tokenBranchId: null,
            routeBranchId: Guid.CreateVersion7(), branchVenueId: Guid.CreateVersion7());

        Assert.True(context.HasSucceeded);
    }

    /// <summary>...and a manager of some other venue does not. The same handler, the same route.</summary>
    [Fact]
    public async Task BranchScoped_refuses_a_manager_for_a_branch_outside_their_venue()
    {
        var context = await EvaluateBranchAsync(
            role: StaffRole.Manager, tokenVenueId: VenueId, tokenBranchId: null,
            routeBranchId: Guid.CreateVersion7(), branchVenueId: Guid.CreateVersion7());

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task BranchScoped_still_admits_a_manager_for_a_branch_of_their_own_venue()
    {
        var context = await EvaluateBranchAsync(
            role: StaffRole.Manager, tokenVenueId: VenueId, tokenBranchId: null,
            routeBranchId: BranchId, branchVenueId: VenueId);

        Assert.True(context.HasSucceeded);
    }

    /// <summary>
    /// The passthrough reads the principal type as well as the role. A role claim saying
    /// PlatformAdmin on a staff-session token - which no sign-in flow issues - is not enough.
    /// </summary>
    [Fact]
    public async Task BranchScoped_does_not_trust_a_platform_admin_role_on_a_staff_session_token()
    {
        var principal = Principal(
            PrincipalType.StaffSession,
            (YallaClaims.Role, StaffRole.PlatformAdmin.ToString()),
            (YallaClaims.BranchId, BranchId.ToString()));

        var handler = new BranchScopedHandler(
            Accessor(principal, ("branchId", Guid.CreateVersion7().ToString())),
            new StubQueries(null),
            NullLogger<BranchScopedHandler>.Instance);

        var context = new AuthorizationHandlerContext([new BranchScopedRequirement()], principal, resource: null);
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task VenueScoped_admits_a_platform_admin_for_any_venue()
    {
        var principal = Principal(
            PrincipalType.VenueUser,
            (YallaClaims.Role, StaffRole.PlatformAdmin.ToString()));

        var handler = new VenueScopedHandler(
            Accessor(principal, ("venueId", Guid.CreateVersion7().ToString())),
            new StubQueries(null));

        var context = new AuthorizationHandlerContext([new VenueScopedRequirement()], principal, resource: null);
        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    /// <summary>
    /// The venue-wide widening is for the accounts whose job is every branch. A waiter who was
    /// given an admin-panel password holds a VenueUser token too, and must not inherit it - their
    /// PIN on a tablet is confined to one branch, and a password must not be a way around that.
    /// </summary>
    [Theory]
    [InlineData(StaffRole.Waiter)]
    [InlineData(StaffRole.Kitchen)]
    public async Task BranchScoped_does_not_widen_a_venue_user_token_below_manager(StaffRole role)
    {
        var context = await EvaluateBranchAsync(
            role: role, tokenVenueId: VenueId, tokenBranchId: BranchId,
            routeBranchId: Guid.CreateVersion7(), branchVenueId: VenueId);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>...while their own branch still works, so the refusal above is the widening and not the token.</summary>
    [Theory]
    [InlineData(StaffRole.Waiter)]
    [InlineData(StaffRole.Manager)]
    public async Task BranchScoped_admits_any_role_on_the_branch_its_own_claim_names(StaffRole role)
    {
        var context = await EvaluateBranchAsync(
            role: role, tokenVenueId: VenueId, tokenBranchId: BranchId,
            routeBranchId: BranchId, branchVenueId: VenueId);

        Assert.True(context.HasSucceeded);
    }

    private static async Task<AuthorizationHandlerContext> EvaluateBranchAsync(
        StaffRole role,
        Guid? tokenVenueId,
        Guid? tokenBranchId,
        Guid routeBranchId,
        Guid branchVenueId)
    {
        var claims = new List<(string, string)> { (YallaClaims.Role, role.ToString()) };

        if (tokenVenueId is { } venue)
        {
            claims.Add((YallaClaims.VenueId, venue.ToString()));
        }

        if (tokenBranchId is { } branch)
        {
            claims.Add((YallaClaims.BranchId, branch.ToString()));
        }

        var principal = Principal(PrincipalType.VenueUser, [.. claims]);

        var handler = new BranchScopedHandler(
            Accessor(principal, ("branchId", routeBranchId.ToString())),
            new StubQueries(null, branchVenueId: branchVenueId),
            NullLogger<BranchScopedHandler>.Instance);

        var requirement = new BranchScopedRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(context);

        return context;
    }

    private static async Task<AuthorizationHandlerContext> EvaluateTabAsync(
        bool mustOrder,
        bool canOrder,
        ParticipantStatus participantStatus = ParticipantStatus.Approved,
        DateTime? nowUtc = null,
        DateTime? tabClosedAtUtc = null,
        bool mustMutate = false,
        TabStatus? tabStatus = null)
    {
        var now = nowUtc ?? new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

        var access = new TabParticipantAccess(
            TabId,
            BranchId,
            tabStatus ?? (tabClosedAtUtc is null ? TabStatus.Open : TabStatus.Closed),
            tabClosedAtUtc,
            participantStatus,
            ParticipantRole.Guest,
            canOrder,
            CanSeeTableTotal: true,
            CanPay: false);

        var principal = Principal(
            PrincipalType.TabParticipant,
            (YallaClaims.ParticipantId, ParticipantId.ToString()),
            (YallaClaims.TabId, TabId.ToString()),
            (YallaClaims.BranchId, BranchId.ToString()));

        var accessor = Accessor(principal, ("tabId", TabId.ToString()));

        // The real authority check over stub reads, so the lifecycle rules themselves - removed
        // participants, a closing tab, the receipt grace period - are exercised rather than mocked.
        var authority = new Yalla.Infrastructure.Services.TokenAuthorityCheck(
            new StubQueries(access),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new Integration.TestClock(now),
            Options.Create(new JwtOptions()));

        var handler = new TabParticipantHandler(
            accessor,
            authority,
            NullLogger<TabParticipantHandler>.Instance);

        var requirement = new TabParticipantRequirement(mustOrder, mustMutate);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(context);

        return context;
    }

    private static async Task<AuthorizationHandlerContext> EvaluateVenueAsync(
        StaffRole role,
        Guid tokenVenueId,
        Guid? routeVenueId,
        Guid? routeBranchId = null)
    {
        var principal = Principal(
            PrincipalType.VenueUser,
            (YallaClaims.VenueId, tokenVenueId.ToString()),
            (YallaClaims.Role, role.ToString()));

        var routeValues = new List<(string, string)>();

        if (routeVenueId is { } venue)
        {
            routeValues.Add(("venueId", venue.ToString()));
        }

        if (routeBranchId is { } branch)
        {
            routeValues.Add(("branchId", branch.ToString()));
        }

        var handler = new VenueScopedHandler(
            Accessor(principal, [.. routeValues]),
            new StubQueries(null, branchVenueId: tokenVenueId));

        var requirement = new VenueScopedRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(context);

        return context;
    }

    private static ClaimsPrincipal Principal(PrincipalType type, params (string Type, string Value)[] claims)
    {
        var all = claims
            .Select(c => new Claim(c.Type, c.Value))
            .Append(new Claim(
                YallaClaims.PrincipalType, ((int)type).ToString(CultureInfo.InvariantCulture)));

        return new ClaimsPrincipal(new ClaimsIdentity(all, "Test"));
    }

    private static IHttpContextAccessor Accessor(
        ClaimsPrincipal principal,
        params (string Key, string Value)[] routeValues)
    {
        var context = new DefaultHttpContext { User = principal };

        foreach (var (key, value) in routeValues)
        {
            context.Request.RouteValues[key] = value;
        }

        return new HttpContextAccessor { HttpContext = context };
    }

    /// <summary>The two reads the policies make, answered from fixed values.</summary>
    private sealed class StubQueries(TabParticipantAccess? access, Guid? branchVenueId = null)
        : IAuthorizationQueries
    {
        public Task<TabParticipantAccess?> GetTabParticipantAccessAsync(
            Guid tabId, Guid participantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(access);

        public Task<Guid?> GetTabBranchIdAsync(Guid tabId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(access?.BranchId);

        public Task<Guid?> GetTableBranchIdAsync(Guid tableId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(access?.BranchId);

        public Task<bool> BranchBelongsToVenueAsync(
            Guid branchId, Guid venueId, CancellationToken cancellationToken = default) =>
            Task.FromResult(branchVenueId == venueId);

        public Task<bool> IsStaffDeviceActiveAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
