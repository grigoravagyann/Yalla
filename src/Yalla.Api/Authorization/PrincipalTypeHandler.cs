using Microsoft.AspNetCore.Authorization;
using Yalla.Domain.Enums;

namespace Yalla.Api.Authorization;

/// <summary>Requires the token to be one of the named principal types.</summary>
/// <param name="Allowed">The principal types that satisfy this requirement.</param>
public sealed record PrincipalTypeRequirement(IReadOnlySet<PrincipalType> Allowed) : IAuthorizationRequirement;

/// <summary>
/// Enforces that the caller is the kind of identity an endpoint is for.
/// </summary>
/// <remarks>
/// <para>
/// The booking endpoints exist for a diner with an <b>account</b>, and the distinction is not
/// cosmetic. A <see cref="PrincipalType.TabParticipant"/> is also a diner in the audit sense -
/// <c>ClaimsCurrentActor</c> reports both as <see cref="ActorType.Diner"/> - but a participant has
/// no <c>DinerUser</c> row by design: they scanned a QR code at a table. There is nothing to list
/// under "my bookings", nothing to count no-shows against, and nobody to telephone when the party
/// is late.
/// </para>
/// <para>
/// So the check is on the principal type, not on the actor type. Reading it off the explicit
/// <c>ytyp</c> claim rather than inferring it from which claims happen to be present is the same
/// discipline the rest of these handlers follow.
/// </para>
/// </remarks>
internal sealed class PrincipalTypeHandler : AuthorizationHandler<PrincipalTypeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PrincipalTypeRequirement requirement)
    {
        if (context.User.PrincipalType() is { } principalType && requirement.Allowed.Contains(principalType))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
