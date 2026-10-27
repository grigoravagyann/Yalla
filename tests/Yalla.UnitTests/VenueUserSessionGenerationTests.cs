using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Services;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests;

/// <summary>
/// The admin-panel session generation: what the token carries, how the check reads it, and what
/// moves it. The end-to-end half - a changed password ending the old token on the next request - is
/// in <c>PlatformAdminAccountTests</c> and <c>PlatformConsoleEndpointTests</c>.
/// </summary>
public class VenueUserSessionGenerationTests
{
    private const string SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes";

    [Fact]
    public void An_admin_panel_token_carries_the_session_generation_it_was_minted_under()
    {
        var clock = new TestClock(TestNow.UtcNow);
        var issuer = new TokenIssuer(Options.Create(new JwtOptions { SigningKey = SigningKey }), clock);
        var staffId = Guid.CreateVersion7();

        var (token, _) = issuer.IssueVenueUserToken(staffId, null, null, StaffRole.PlatformAdmin, sessionGeneration: 2);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);

        Assert.Equal("2", jwt.GetClaim(YallaClaims.SessionGeneration).Value);
        Assert.Equal(staffId.ToString(), jwt.GetClaim(YallaClaims.StaffMemberId).Value);
    }

    [Fact]
    public void Only_the_rows_own_generation_passes_and_a_missing_row_does_not()
    {
        var row = new VenueUserSessionState(SessionGeneration: 3);

        Assert.Null(TokenAuthorityCheck.EvaluateVenueUserSession(row, 3));

        foreach (var (state, token) in new (VenueUserSessionState?, int)[] { (row, 2), (row, 4), (null, 0) })
        {
            Assert.Equal(TokenRevoked.SessionRevoked, TokenAuthorityCheck.EvaluateVenueUserSession(state, token)?.Code);
        }

        // A token from before the claim reads as 0, which is where every row starts.
        Assert.Null(TokenAuthorityCheck.EvaluateVenueUserSession(new VenueUserSessionState(0), 0));
    }

    [Fact]
    public async Task The_check_reuses_a_read_for_five_seconds_and_never_refuses_a_token_newer_than_it()
    {
        var clock = new TestClock(TestNow.UtcNow);
        var queries = new ScriptedQueries { State = new VenueUserSessionState(0) };
        var check = new TokenAuthorityCheck(
            queries, new MemoryCache(new MemoryCacheOptions()), clock, Options.Create(new JwtOptions()));
        var staff = Guid.CreateVersion7();

        Assert.Null(await check.CheckVenueUserSessionAsync(staff, 0));
        Assert.Equal(1, queries.Reads);

        // The password changed on another instance: the new token is newer than the cached read, so it
        // is checked against the row, and the old one is refused from that same read.
        queries.State = new VenueUserSessionState(1);
        Assert.Null(await check.CheckVenueUserSessionAsync(staff, 1));
        Assert.Equal(2, queries.Reads);
        Assert.Equal(TokenRevoked.SessionRevoked, (await check.CheckVenueUserSessionAsync(staff, 0))?.Code);
        Assert.Equal(2, queries.Reads);

        // A bump nobody announced is noticed when the window lapses; an announced one at once.
        queries.State = new VenueUserSessionState(2);
        Assert.Null(await check.CheckVenueUserSessionAsync(staff, 1));
        check.InvalidateVenueUser(staff);
        Assert.Equal(TokenRevoked.SessionRevoked, (await check.CheckVenueUserSessionAsync(staff, 1))?.Code);
        Assert.Equal(3, queries.Reads);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await check.CheckVenueUserSessionAsync(staff, 2));
        Assert.Equal(4, queries.Reads);
    }

    [Fact]
    public void Ending_sessions_moves_the_generation_and_a_rehash_does_not()
    {
        var admin = StaffMember.PlatformAdmin("Grigor", "+37400000000", "admin@yalla.test", "first-hash");
        Assert.Equal(0, admin.SessionGeneration);

        admin.SetPasswordHash("rehashed");
        Assert.Equal(0, admin.SessionGeneration);

        admin.EndSessions();
        admin.EndSessions();
        Assert.Equal(2, admin.SessionGeneration);
    }

    /// <summary>The staff-row read, scripted and counted. Nothing else here is asked.</summary>
    private sealed class ScriptedQueries : IAuthorizationQueries
    {
        public VenueUserSessionState? State { get; set; }

        public int Reads { get; private set; }

        public Task<VenueUserSessionState?> GetVenueUserSessionStateAsync(Guid staffMemberId, CancellationToken cancellationToken = default)
        {
            Reads++;

            return Task.FromResult(State);
        }

        public Task<DinerSessionState?> GetDinerSessionStateAsync(Guid dinerUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TabParticipantAccess?> GetTabParticipantAccessAsync(
            Guid tabId, Guid participantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> GetTabBranchIdAsync(Guid tabId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> GetTableBranchIdAsync(Guid tableId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> BranchBelongsToVenueAsync(
            Guid branchId, Guid venueId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StaffDeviceStanding?> GetLiveStaffDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
