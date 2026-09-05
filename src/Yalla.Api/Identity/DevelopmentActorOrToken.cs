using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Identity;

/// <summary>
/// Development only: the token's identity when there is one, the configured stub when there is not.
/// </summary>
/// <remarks>
/// <para>
/// The stub used to replace <see cref="ICurrentActor"/> outright, which was right when nothing
/// could sign in. Now that all four flows exist it is a trap: a request carrying a real bearer
/// token passes the authorisation policies as the person it names and then reaches a service that
/// is told they are the seeded waiter. The failure reads as "List venues requires the PlatformAdmin
/// role; the caller is a Waiter" on a request made by a platform admin, which sends you looking in
/// entirely the wrong place.
/// </para>
/// <para>
/// So the token always wins. The stub keeps doing its actual job - letting you poke the API with no
/// token at all and still land a real staff member in the audit log - and can no longer contradict
/// a genuine sign-in.
/// </para>
/// </remarks>
internal sealed class DevelopmentActorOrToken(
    IHttpContextAccessor accessor,
    ClaimsCurrentActor fromToken,
    DevCurrentActor stub) : ICurrentActor
{
    private ICurrentActor Current =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated == true ? fromToken : stub;

    public ActorType Type => Current.Type;

    public Guid? StaffMemberId => Current.StaffMemberId;

    public Guid? DinerUserId => Current.DinerUserId;

    public StaffRole? Role => Current.Role;
}
