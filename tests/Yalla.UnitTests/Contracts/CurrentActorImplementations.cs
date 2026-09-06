using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yalla.Api.Identity;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests.Contracts;

/// <summary>
/// <b>The production actor</b>, driven by real tokens minted by the real issuer and validated with
/// the API's own validation parameters.
/// </summary>
/// <remarks>
/// <para>
/// This is the definition the other subclasses are measured against, so it goes through the whole
/// path rather than hand-assembling a <c>ClaimsPrincipal</c>: <c>TokenIssuer</c> writes the claim
/// set for the identity type, the token is signed, and it is then validated back into a principal
/// exactly as <c>AddJwtBearer</c> does. A hand-built principal would let a claim name drift on one
/// side without any test noticing, which is the class of failure this whole file exists for.
/// </para>
/// <para>
/// The <c>StaffDevice</c> principal type is absent from the contract on purpose: a device is not
/// an identity that can act. It offers a PIN and is given a staff session in return, and it is the
/// session that reaches a service.
/// </para>
/// </remarks>
public sealed class ClaimsCurrentActorContractTests : CurrentActorContract
{
    private const string SigningKey = "contract-test-signing-key-comfortably-longer-than-thirty-two-bytes";

    protected override ICurrentActor ActorFor(ActorIdentity identity)
    {
        var principal = ValidateAsync(Mint(identity)).GetAwaiter().GetResult();

        return new ClaimsCurrentActor(new StubHttpContextAccessor(principal));
    }

    /// <summary>The real issuer, writing the real claim set for this identity type.</summary>
    private static string Mint(ActorIdentity identity)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new JwtOptions { SigningKey = SigningKey });

        // The wall clock, not a fixed instant. Validation below checks lifetime against the real
        // clock - as the JwtBearer handler does - so a token minted at a fixed 2026 noon is "not
        // yet valid" for most of the day. Nothing in this suite is about time.
        var clock = new TestClock(DateTime.UtcNow);
        var issuer = new TokenIssuer(options, clock);

        return identity.Principal switch
        {
            PrincipalType.TabParticipant => issuer.IssueTabParticipantToken(
                identity.ParticipantId!.Value,
                identity.TabId!.Value,
                identity.BranchId!.Value,
                clock.UtcNow.AddHours(4)).Token,

            PrincipalType.Diner => issuer.IssueDinerToken(identity.DinerUserId!.Value).Token,

            PrincipalType.StaffSession => issuer.IssueStaffSessionToken(
                identity.StaffMemberId!.Value,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                identity.BranchId!.Value,
                identity.VenueId!.Value,
                identity.Role!.Value).Token,

            PrincipalType.VenueUser => issuer.IssueVenueUserToken(
                identity.StaffMemberId!.Value,
                identity.VenueId,
                branchId: null,
                identity.Role!.Value).Token,

            _ => throw new NotSupportedException($"{identity.Principal} is not an identity that acts."),
        };
    }

    /// <summary>
    /// Validates the token back into a principal, the way the JwtBearer handler does.
    /// </summary>
    /// <remarks>
    /// Signature, issuer, audience and lifetime all checked. A test that merely <i>read</i> the
    /// token would still pass if the issuer stopped signing it, which is not a property worth
    /// leaving untested in the one file that claims to exercise the real path.
    /// </remarks>
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
                RoleClaimType = YallaClaims.Role,
                NameClaimType = YallaClaims.StaffMemberId,
            });

        Assert.True(result.IsValid, result.Exception?.Message);

        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    /// <summary>The one thing <c>ClaimsCurrentActor</c> needs: a request with a principal on it.</summary>
    private sealed class StubHttpContextAccessor(ClaimsPrincipal principal) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext { User = principal };
    }
}

/// <summary>
/// <b>The double the integration tests use.</b> This is the one that was lying.
/// </summary>
/// <remarks>
/// It put the participant id into <c>DinerUserId</c>, which production never does, and that single
/// mismatch is why the ordering flow could be green in the suite and broken in production. The
/// factory methods below are the ones every test calls, so they are what is measured - not a
/// freshly constructed <c>TestActor</c> with the fields set by hand, which would test nothing.
/// </remarks>
public sealed class TestActorContractTests : CurrentActorContract
{
    protected override ICurrentActor ActorFor(ActorIdentity identity) => identity.Principal switch
    {
        PrincipalType.TabParticipant => TestActor.Participant(identity.ParticipantId!.Value),
        PrincipalType.Diner => TestActor.Diner(identity.DinerUserId),
        PrincipalType.StaffSession => TestActor.Waiter(identity.StaffMemberId!.Value),
        PrincipalType.VenueUser => TestActor.Owner(identity.StaffMemberId!.Value),
        _ => throw new NotSupportedException($"{identity.Principal} is not an identity that acts."),
    };
}

/// <summary>
/// <b>The development stub</b>, which answers for a request carrying no token at all.
/// </summary>
/// <remarks>
/// It is a double in production code rather than in the test project, which is exactly why it
/// belongs here: it is the actor a developer poking the API with curl is served by, and if it
/// reports a shape the real one cannot, everything they conclude from that session is wrong.
/// <para>
/// Before Prompt 11 it could not stand for a tab participant at all - there was nowhere to put a
/// participant id - so the ordering path could not be exercised without minting a token, which is
/// the path that turned out never to have worked.
/// </para>
/// </remarks>
public sealed class DevCurrentActorContractTests : CurrentActorContract
{
    protected override ICurrentActor ActorFor(ActorIdentity identity) => Build(identity);

    /// <summary>Shared with the composite's suite below, which wraps this one.</summary>
    internal static DevCurrentActor Build(ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var options = new DevActorOptions
        {
            Enabled = true,
            Type = identity.Principal is PrincipalType.TabParticipant or PrincipalType.Diner
                ? ActorType.Diner
                : ActorType.Staff,
            Role = identity.Role ?? StaffRole.Waiter,
            StaffMemberId = identity.StaffMemberId,
            DinerUserId = identity.DinerUserId,
            ParticipantId = identity.ParticipantId,
        };

        return new DevCurrentActor(
            Microsoft.Extensions.Options.Options.Create(options),
            new DevSeedRegistry());
    }
}

/// <summary>
/// <b>The development composite</b>: the token when there is one, the stub when there is not.
/// </summary>
/// <remarks>
/// Exercised through its stub branch, because the token branch is <c>ClaimsCurrentActor</c> and is
/// already covered above. What is worth pinning here is that the composite forwards every member -
/// a delegating type that forgets one is exactly how <c>ParticipantId</c> could reach production as
/// a permanent null.
/// </remarks>
public sealed class DevelopmentActorOrTokenContractTests : CurrentActorContract
{
    protected override ICurrentActor ActorFor(ActorIdentity identity) =>
        new DevelopmentActorOrToken(
            new UnauthenticatedAccessor(),

            // Never reached: the accessor below reports an unauthenticated request, so the
            // composite takes its stub branch every time. Null rather than a second real actor,
            // so a regression that starts consulting the token branch here fails loudly.
            fromToken: null!,
            stub: DevCurrentActorContractTests.Build(identity));

    /// <summary>An anonymous request, which is the case the stub exists for.</summary>
    private sealed class UnauthenticatedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
    }
}
