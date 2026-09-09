using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yalla.Api.ApplicationExtensions;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Who a rate-limit budget belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The partition key decided nothing for three of the five identity types. It read
/// <c>Identity.Name</c>, which is configured as the <c>StaffMemberId</c> claim, and a tab
/// participant, a diner and an enrolled tablet carry no such claim - so all three produced the
/// single key <c>user:</c> and shared one budget with each other, across the whole platform.
/// </para>
/// <para>
/// The first test walks the enum rather than checking the three that were broken, because the
/// failure was not three mistakes. It was one key that happened to work for the two types somebody
/// tried, and nothing anywhere required the other three to be different. A sixth identity type
/// added tomorrow would have joined them silently; now it fails here.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class RateLimitPartitionTests(SqlServerFixture fixture)
{
    private const string SigningKey = "partition-test-signing-key-comfortably-longer-than-thirty-two-bytes";

    /// <summary>
    /// Every identity type gets its own budget, every holder of one gets their own, and the same
    /// holder gets the same one twice running.
    /// </summary>
    /// <remarks>
    /// Driven by tokens the real issuer minted and the API's own validation parameters read back,
    /// for the reason <c>docs/contract-tests.md</c> gives: a hand-built principal lets a claim name
    /// drift on one side with nothing noticing, and a claim name is exactly what went wrong here.
    /// </remarks>
    [Fact]
    public async Task Every_identity_type_partitions_on_something_and_never_on_the_same_thing()
    {
        var keys = new Dictionary<PrincipalType, string>();

        foreach (var type in Enum.GetValues<PrincipalType>())
        {
            var key = RateLimitingExtensions.PartitionKey(await ContextFor(type, Subject(type)));

            // The address fallback is for a token that names nobody. Reaching it here would mean
            // this identity type has no budget of its own, which is the bug.
            Assert.False(
                key.StartsWith("ip:", StringComparison.Ordinal),
                $"{type} fell back to the remote address, so every one of them shares a budget.");

            Assert.DoesNotContain(type, keys);
            keys[type] = key;
        }

        // Five types, five keys. A collision means two identity types are spending each other's
        // budget, which is what this file exists to stop.
        Assert.Equal(keys.Count, keys.Values.Distinct(StringComparer.Ordinal).Count());

        foreach (var type in Enum.GetValues<PrincipalType>())
        {
            // The same holder, asked twice: one budget, not a fresh one per request.
            Assert.Equal(keys[type], RateLimitingExtensions.PartitionKey(await ContextFor(type, Subject(type))));

            // A different holder of the same type: their own.
            Assert.NotEqual(
                keys[type],
                RateLimitingExtensions.PartitionKey(await ContextFor(type, Guid.CreateVersion7())));
        }
    }

    /// <summary>
    /// One tablet spending the PIN limit does not stop the tablet in the next venue.
    /// </summary>
    /// <remarks>
    /// The PIN endpoint is reached with a <b>device</b> token, and a device token names no staff
    /// member - so this limit was ten a minute for every tablet in the product at once. Anybody
    /// holding any valid token could spend it, and every waiter on the platform would be unable to
    /// sign in until the window turned.
    /// </remarks>
    [SkippableFact]
    public async Task One_tablet_spending_the_pin_limit_does_not_lock_out_the_next_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // Two a minute, so the budget is spent in two requests rather than ten. Everything else is
        // raised out of the way: this test is about which bucket, not about any other limit.
        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:PinPermitLimit", "2")
            .With("RateLimiting:PinWindowSeconds", "60")
            .With("RateLimiting:GlobalPermitLimit", "1000")
            .With("RateLimiting:AuthPermitLimit", "1000");

        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var busy = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, mine));

        using var elsewhere = factory.CreateClientWithToken(
            await StaffAuthTests.EnrolDeviceAsync(factory, theirs));

        // The busy tablet spends its two, both of them real sign-ins.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PinAsync(busy, mine)).StatusCode);
        }

        var spent = await PinAsync(busy, mine);

        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        Assert.Equal("rate-limited", await CodeAsync(spent));

        // And the tablet in the other venue, which has spent nothing, signs in.
        Assert.Equal(HttpStatusCode.OK, (await PinAsync(elsewhere, theirs)).StatusCode);
    }

    /// <summary>
    /// One diner ordering all evening does not use up the budget of the table next to them.
    /// </summary>
    /// <remarks>
    /// The global limiter keys on the same partition for every request in the product. With three
    /// of five identity types collapsing onto one key, its three hundred a minute was three hundred
    /// shared by every diner and every open tab everywhere - and tab participants ordering from
    /// their phones are the highest-volume identity there is. That ceiling is a busy Friday, not an
    /// attack, and it is the half of this bug that had nothing to do with anybody being hostile.
    /// </remarks>
    [SkippableFact]
    public async Task One_diner_exhausting_the_global_limit_leaves_the_next_diner_untouched()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int Budget = 8;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:GlobalPermitLimit", Budget.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .With("RateLimiting:GlobalWindowSeconds", "60")
            .With("RateLimiting:AuthPermitLimit", "1000")
            .With("RateLimiting:CodeRequestPermitLimit", "1000");

        // Signing in is anonymous, so it spends an address budget rather than either diner's - four
        // requests against the same Budget, which is why Budget is comfortably above four.
        using var busy = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var quiet = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        for (var call = 0; call < Budget; call++)
        {
            Assert.Equal(HttpStatusCode.OK, (await busy.GetAsync("/api/reservations/mine")).StatusCode);
        }

        var spent = await busy.GetAsync("/api/reservations/mine");

        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);

        // The diner at the next table has not made a request all evening.
        Assert.Equal(HttpStatusCode.OK, (await quiet.GetAsync("/api/reservations/mine")).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>A fixed subject per identity type, so two calls for one type agree.</summary>
    private static Guid Subject(PrincipalType type) =>
        new($"00000000-0000-0000-0000-00000000000{(int)type}");

    /// <summary>
    /// A request carrying a real token for this identity type, validated back the way the JwtBearer
    /// handler does.
    /// </summary>
    /// <remarks>
    /// The remote address is set so that a fall through to the address fallback is visible as such
    /// rather than as an "unknown" that could be anything.
    /// </remarks>
    private static async Task<HttpContext> ContextFor(PrincipalType type, Guid subject)
    {
        var context = new DefaultHttpContext { User = await ValidateAsync(Mint(type, subject)) };

        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        return context;
    }

    /// <summary>The real issuer, writing the real claim set for this identity type.</summary>
    /// <remarks>
    /// The throw is the point of the default arm: a sixth identity type cannot be added without
    /// somebody coming here and deciding what it partitions on.
    /// </remarks>
    private static string Mint(PrincipalType type, Guid subject)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new JwtOptions { SigningKey = SigningKey });
        var clock = new TestClock(DateTime.UtcNow);
        var issuer = new TokenIssuer(options, clock);

        return type switch
        {
            PrincipalType.TabParticipant => issuer.IssueTabParticipantToken(
                subject, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.UtcNow.AddHours(4)).Token,

            PrincipalType.Diner => issuer.IssueDinerToken(subject).Token,

            PrincipalType.StaffDevice => issuer.IssueDeviceToken(
                subject, Guid.CreateVersion7(), Guid.CreateVersion7()).Token,

            PrincipalType.StaffSession => issuer.IssueStaffSessionToken(
                subject, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
                Guid.CreateVersion7(), StaffRole.Waiter).Token,

            PrincipalType.VenueUser => issuer.IssueVenueUserToken(
                subject, Guid.CreateVersion7(), branchId: null, StaffRole.Manager).Token,

            _ => throw new NotSupportedException(
                $"{type} has no token here, so nothing knows what budget it should get. Decide that "
                + "in RateLimitingExtensions.PartitionKey, then mint one for it."),
        };
    }

    private static async Task<ClaimsPrincipal> ValidateAsync(string token)
    {
        var jwt = new JwtOptions { SigningKey = SigningKey };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),

                // The two the API configures, and the second is the one this whole file is about.
                RoleClaimType = YallaClaims.Role,
                NameClaimType = YallaClaims.StaffMemberId,
            });

        Assert.True(result.IsValid, result.Exception?.Message);

        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static Task<HttpResponseMessage> PinAsync(HttpClient tablet, AuthBranch branch) =>
        tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    /// <summary>A diner account, through the real sign-in flow.</summary>
    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone });

        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }
}
