using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Yalla.Api.Authorization;
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
                };
            });

        builder.Services.AddYallaAuthorization();

        return builder;
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
    /// Diner, venue-user and tab participant tokens do not pay it: their access tokens last
    /// fifteen minutes or track a tab, and revoking their refresh chain is enough.
    /// </para>
    /// </remarks>
    private static async Task RejectRevokedDevicesAsync(TokenValidatedContext context)
    {
        var principalType = context.Principal?.PrincipalType();

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

        var queries = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationQueries>();

        if (!await queries.IsStaffDeviceActiveAsync(deviceId.Value, context.HttpContext.RequestAborted))
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

            // The platform tier is "above" in every role set: what an owner may do at their venue,
            // a platform admin may do at any venue. The scope handlers pass them for every branch.
            .AddPolicy(YallaPolicies.WaiterOrAbove, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StaffRoleRequirement(
                    new HashSet<StaffRole> { StaffRole.Waiter, StaffRole.Manager, StaffRole.Owner, StaffRole.PlatformAdmin })))

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
