using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// JWT bearer authentication and the seven authorisation policies.
/// </summary>
public static class AuthenticationExtensions
{
    public static WebApplicationBuilder AddYallaAuthentication(this WebApplicationBuilder builder)
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                  ?? new JwtOptions();

        // The signing key was already validated in AddAuthenticationServices, which runs first
        // and fails startup rather than letting the process reach here with nothing to sign with.
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,

                    // The framework default is five minutes, which on a fifteen-minute access
                    // token is a third of its life spent already expired but still accepted.
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),

                    RoleClaimType = YallaClaims.Role,
                    NameClaimType = YallaClaims.StaffMemberId,
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = RejectRevokedDevicesAsync,
                    OnChallenge = AnswerRevokedSessionAsync,
                };
            });

        builder.Services.AddYallaAuthorization();

        return builder;
    }

    /// <summary>
    /// Where <see cref="RejectRevokedDevicesAsync"/> leaves the reason a diner session was refused,
    /// for the challenge to put in the body.
    /// </summary>
    private const string RevokedSessionItem = "yalla:session-revoked";

    /// <summary>
    /// Answers a refused diner session with a body the app can branch on, rather than the bare 401
    /// the bearer handler writes.
    /// </summary>
    /// <remarks>
    /// The failure is recorded during token validation, but the response is written here: the
    /// authorisation middleware challenges only when the route needs a signed-in caller, and an
    /// anonymous route carrying a stale token should simply run as anonymous.
    /// </remarks>
    private static async Task AnswerRevokedSessionAsync(JwtBearerChallengeContext context)
    {
        if (context.HttpContext.Items[RevokedSessionItem] is not TokenRevoked revoked)
        {
            return;
        }

        context.HandleResponse();

        var response = context.Response;
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";

        await response.WriteAsJsonAsync(
            new UnifiedErrorEnvelope
            {
                Type = ErrorCodes.TypeFor(ErrorCodes.SessionRevoked),
                Title = ErrorCodes.TitleFor(ErrorCodes.SessionRevoked),
                Status = StatusCodes.Status401Unauthorized,
                Detail = revoked.Message,
                Instance = context.Request.Path.Value,
                Code = ErrorCodes.SessionRevoked,
                TraceId = context.HttpContext.TraceIdentifier,
            },
            options: null,
            contentType: "application/problem+json",
            context.HttpContext.RequestAborted);
    }

    /// <summary>
    /// Rejects a signed, unexpired token from a tablet that has since been revoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the price of statelessness, paid deliberately and only where it is owed. A device
    /// token lives for a year and a session token for half an hour; neither can be withdrawn by
    /// waiting. A tablet left in a taxi has to stop working from the admin panel <i>now</i>, so
    /// requests carrying a device or session token cost one lookup on a primary key.
    /// </para>
    /// <para>
    /// Diner tokens pay it too, through the same five-second cache: a squatter evicted by the
    /// number's owner, a deactivated account and a deleted one must stop working now, and fifteen
    /// minutes of a token that can set a password is not "short". Venue-user tokens still do not.
    /// </para>
    /// </remarks>
    private static async Task RejectRevokedDevicesAsync(TokenValidatedContext context)
    {
        var principalType = context.Principal?.PrincipalType();
        var authority = context.HttpContext.RequestServices.GetRequiredService<ITokenAuthorityCheck>();

        // A diner session that has ended: deactivated, deleted, or its generation moved on. Failed
        // here so no handler has to remember it, and answered by AnswerRevokedSessionAsync with
        // `session-revoked` so the app refreshes or signs out instead of reporting a bug.
        if (principalType == PrincipalType.Diner)
        {
            var dinerUserId = context.Principal?.Guid(YallaClaims.DinerUserId);

            TokenRevoked? refused;

            if (dinerUserId is null)
            {
                refused = new TokenRevoked(TokenRevoked.SessionRevoked, "Diner token carries no account.");
            }
            else if (!YallaClaims.TryReadSessionGeneration(context.Principal, out var generation))
            {
                refused = new TokenRevoked(TokenRevoked.SessionRevoked, "Diner token carries an unreadable session.");
            }
            else
            {
                refused = await authority.CheckDinerSessionAsync(
                    dinerUserId.Value, generation, context.HttpContext.RequestAborted);
            }

            if (refused is not null)
            {
                context.HttpContext.Items[RevokedSessionItem] = refused;
                context.Fail(refused.Message);
            }

            return;
        }

        // A tab that has closed. The token was minted before anybody knew when that would be, so
        // its own expiry cannot express it. Failing here rather than in a policy is deliberate: it
        // produces a 401 carrying a reason the app can render as "this tab is closed", instead of a
        // bare 403 that looks like a permissions bug.
        if (principalType == PrincipalType.TabParticipant)
        {
            var tabId = context.Principal?.Guid(YallaClaims.TabId);
            var participantId = context.Principal?.Guid(YallaClaims.ParticipantId);

            if (tabId is null || participantId is null)
            {
                context.Fail("Tab participant token is incomplete.");
                return;
            }

            var standing = await authority.GetParticipantAuthorityAsync(
                tabId.Value, participantId.Value, context.HttpContext.RequestAborted);

            if (standing?.Revoked is { } revoked)
            {
                context.Fail(revoked.Message);
            }

            return;
        }

        if (principalType is not (PrincipalType.StaffDevice or PrincipalType.StaffSession))
        {
            return;
        }

        var deviceId = context.Principal?.Guid(YallaClaims.DeviceId);

        if (deviceId is null)
        {
            context.Fail("Staff token carries no device.");
            return;
        }

        if (!await authority.IsDeviceActiveAsync(deviceId.Value, context.HttpContext.RequestAborted))
        {
            context.Fail("This tablet is no longer enrolled.");
        }
    }

    private static IServiceCollection AddYallaAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddSingleton<IAuthorizationHandler, StaffRoleHandler>();
        services.AddSingleton<IAuthorizationHandler, PrincipalTypeHandler>();
        services.AddScoped<IAuthorizationHandler, TabParticipantHandler>();
        services.AddScoped<IAuthorizationHandler, BranchScopedHandler>();
        services.AddScoped<IAuthorizationHandler, VenueScopedHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(YallaPolicies.TabParticipant, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new TabParticipantRequirement(MustBeAbleToOrder: false)))

            .AddPolicy(YallaPolicies.TabParticipantCanOrder, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new TabParticipantRequirement(MustBeAbleToOrder: true)))

            .AddPolicy(YallaPolicies.TabParticipantMutating, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new TabParticipantRequirement(
                    MustBeAbleToOrder: false, MustBeAbleToMutate: true)))

            // The platform tier is "above" in every role set: what an owner may do at their venue,
            // a platform admin may do at any venue. The scope handlers pass them for every branch.
            .AddPolicy(YallaPolicies.WaiterOrAbove, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StaffRoleRequirement(
                    new HashSet<StaffRole> { StaffRole.Waiter, StaffRole.Manager, StaffRole.Owner, StaffRole.PlatformAdmin })))

            .AddPolicy(YallaPolicies.KitchenOrAbove, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StaffRoleRequirement(
                    new HashSet<StaffRole> { StaffRole.Kitchen, StaffRole.Waiter, StaffRole.Manager, StaffRole.Owner, StaffRole.PlatformAdmin })))

            .AddPolicy(YallaPolicies.ManagerOrAbove, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StaffRoleRequirement(
                    new HashSet<StaffRole> { StaffRole.Manager, StaffRole.Owner, StaffRole.PlatformAdmin })))

            .AddPolicy(YallaPolicies.PlatformAdminOnly, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StaffRoleRequirement(
                    new HashSet<StaffRole> { StaffRole.PlatformAdmin })))

            .AddPolicy(YallaPolicies.BranchScoped, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new BranchScopedRequirement()))

            .AddPolicy(YallaPolicies.VenueScoped, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new VenueScopedRequirement()))

            .AddPolicy(YallaPolicies.VerifiedDiner, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PrincipalTypeRequirement(
                    new HashSet<PrincipalType> { PrincipalType.Diner })));

        return services;
    }
}
