using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Yalla.Application.Auth;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Services;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests;

/// <summary>
/// The diner session generation: what bumps it, what a token carries, and how the check reads it.
/// </summary>
/// <remarks>
/// The end-to-end half - a squatter's token refused on every route, a deactivated account after the
/// cache window, a deleted one - is in <c>DinerAccountTests</c> and <c>DinerAccountDeletionTests</c>.
/// These are the pieces those depend on, each provable without a database.
/// </remarks>
public class DinerSessionGenerationTests
{
    private const string SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes";

    // ------------------------------------------------------------ the token

    [Fact]
    public void A_diner_token_carries_the_session_generation_it_was_minted_under()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var issuer = new TokenIssuer(Options.Create(new JwtOptions { SigningKey = SigningKey }), clock);
        var dinerUserId = Guid.CreateVersion7();

        var (token, _) = issuer.IssueDinerToken(dinerUserId, sessionGeneration: 3);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);

        Assert.Equal("3", jwt.GetClaim(YallaClaims.SessionGeneration).Value);
        Assert.Equal(dinerUserId.ToString(), jwt.GetClaim(YallaClaims.DinerUserId).Value);
    }

    [Fact]
    public void A_token_without_the_claim_reads_as_generation_zero_and_a_malformed_one_does_not_read()
    {
        // Minted before the claim existed, when every account was on zero.
        Assert.True(YallaClaims.TryReadSessionGeneration(new ClaimsPrincipal(new ClaimsIdentity()), out var missing));
        Assert.Equal(0, missing);

        Assert.True(YallaClaims.TryReadSessionGeneration(With("7"), out var seven));
        Assert.Equal(7, seven);

        Assert.False(YallaClaims.TryReadSessionGeneration(With("-1"), out _));
        Assert.False(YallaClaims.TryReadSessionGeneration(With("one"), out _));

        static ClaimsPrincipal With(string value) =>
            new(new ClaimsIdentity([new Claim(YallaClaims.SessionGeneration, value)]));
    }

    // ------------------------------------------------------------ the rule

    [Fact]
    public void A_bumped_generation_fails_validation_and_so_does_an_inactive_deleted_or_missing_account()
    {
        var live = new DinerSessionState(IsActive: true, IsDeleted: false, SessionGeneration: 4);

        Assert.Null(TokenAuthorityCheck.EvaluateDinerSession(live, 4));

        foreach (var (state, tokenGeneration) in new (DinerSessionState?, int)[]
                 {
                     (live, 3),
                     (live, 5),
                     (live with { IsActive = false }, 4),
                     (live with { IsDeleted = true }, 4),
                     (null, 0),
                 })
        {
            var refused = TokenAuthorityCheck.EvaluateDinerSession(state, tokenGeneration);

            Assert.NotNull(refused);
            Assert.Equal(TokenRevoked.SessionRevoked, refused.Code);
        }
    }

    // ------------------------------------------------------------ the cache

    /// <summary>
    /// Five seconds on the application clock, evicted by the write that changes the answer, and
    /// never in the way of a token newer than what it holds.
    /// </summary>
    [Fact]
    public async Task The_check_reuses_a_read_for_five_seconds_and_never_refuses_a_token_newer_than_it()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var queries = new ScriptedQueries { State = new DinerSessionState(true, false, 0) };
        var check = new TokenAuthorityCheck(
            queries, new MemoryCache(new MemoryCacheOptions()), clock, Options.Create(new JwtOptions()));
        var diner = Guid.CreateVersion7();

        Assert.Null(await check.CheckDinerSessionAsync(diner, 0));
        Assert.Equal(1, queries.Reads);

        // The password changed on another instance. The new token is newer than the cached read,
        // so it is checked against the row rather than refused from the cache.
        queries.State = new DinerSessionState(true, false, 1);
        Assert.Null(await check.CheckDinerSessionAsync(diner, 1));
        Assert.Equal(2, queries.Reads);

        // And the old token is refused from that same read.
        Assert.Equal(TokenRevoked.SessionRevoked, (await check.CheckDinerSessionAsync(diner, 0))?.Code);
        Assert.Equal(2, queries.Reads);

        // Deactivated by a write that announced nothing: the window is what delays noticing.
        queries.State = new DinerSessionState(false, false, 2);
        Assert.Null(await check.CheckDinerSessionAsync(diner, 1));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(TokenRevoked.SessionRevoked, (await check.CheckDinerSessionAsync(diner, 1))?.Code);
        Assert.Equal(3, queries.Reads);

        // Switched back on, and said so: the next request reads the row.
        queries.State = new DinerSessionState(true, false, 2);
        check.InvalidateDiner(diner);
        Assert.Null(await check.CheckDinerSessionAsync(diner, 2));
        Assert.Equal(4, queries.Reads);
    }

    // ------------------------------------------------------------ what bumps it

    [Fact]
    public void Displacing_a_registrant_bumps_the_generation_and_the_registrants_own_proof_does_not()
    {
        var nowUtc = DateTime.UtcNow;

        var honest = Registered("honest");
        Assert.Equal(0, honest.SessionGeneration);
        Assert.False(honest.ProveNumberByCode(nowUtc, verifierIsAccountHolder: true));
        Assert.Equal(0, honest.SessionGeneration);

        var squatted = Registered("squatted");
        Assert.True(squatted.ProveNumberByCode(nowUtc));
        Assert.Equal(1, squatted.SessionGeneration);
        Assert.Null(squatted.PasswordHash);

        // Proving an already-proved number again ends nothing.
        Assert.False(squatted.ProveNumberByCode(nowUtc));
        Assert.Equal(1, squatted.SessionGeneration);
    }

    [Fact]
    public void Setting_a_password_and_deactivating_bump_it_while_a_rehash_and_reactivating_do_not()
    {
        var diner = new DinerUser("+37491000101", "en");

        diner.SetPassword("first-hash");
        Assert.Equal(1, diner.SessionGeneration);

        diner.SetPassword("second-hash");
        Assert.Equal(2, diner.SessionGeneration);

        diner.RehashPassword("second-hash-stronger");
        Assert.Equal(2, diner.SessionGeneration);

        diner.SetActive(false);
        diner.SetActive(false);
        Assert.Equal(3, diner.SessionGeneration);

        diner.SetActive(true);
        Assert.Equal(3, diner.SessionGeneration);
    }

    [Fact]
    public void Deleting_clears_everything_that_identifies_the_person_and_ends_every_session_once()
    {
        var nowUtc = DateTime.UtcNow;
        var diner = DinerUser.Register(
            "+37491000102", "en", "Ani Kh", "ani_deleted", "ani.deleted@example.test", "a-hash");
        diner.MarkPhoneVerified(nowUtc);
        diner.SetPhoto(Guid.CreateVersion7());

        diner.MarkDeleted(nowUtc);

        Assert.True(diner.IsDeleted);
        Assert.False(diner.IsActive);
        Assert.Null(diner.PhoneE164);
        Assert.Null(diner.Username);
        Assert.Null(diner.Email);
        Assert.Null(diner.DisplayName);
        Assert.Null(diner.PasswordHash);
        Assert.Null(diner.PhoneVerifiedAtUtc);
        Assert.Null(diner.PhotoId);
        Assert.Equal(1, diner.SessionGeneration);

        diner.MarkDeleted(nowUtc.AddMinutes(1));
        Assert.Equal(1, diner.SessionGeneration);
        Assert.Equal(nowUtc, diner.DeletedAtUtc);
    }

    private static DinerUser Registered(string name) =>
        DinerUser.Register(
            "+37491000100", "en", "Ani", $"ani_{name}", $"ani.{name}@example.test", "registrant-hash");

    /// <summary>The account read, scripted and counted. Nothing else here is asked.</summary>
    private sealed class ScriptedQueries : IAuthorizationQueries
    {
        public DinerSessionState? State { get; set; }

        public int Reads { get; private set; }

        public Task<DinerSessionState?> GetDinerSessionStateAsync(Guid dinerUserId, CancellationToken cancellationToken = default)
        {
            Reads++;

            return Task.FromResult(State);
        }

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

        public Task<bool> IsStaffDeviceActiveAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
