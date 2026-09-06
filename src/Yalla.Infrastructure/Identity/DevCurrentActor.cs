using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Development stand-in for authentication: reports a configured staff member, defaulting to the
/// seeded waiter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Development only.</b> Registration is gated on <see cref="DevActorOptions.Enabled"/>, and
/// nothing registers it outside Development.
/// </para>
/// <para>
/// It no longer replaces the real actor. Authentication exists now, so a request carrying a token
/// is the person that token names and this stub only answers for requests that carry none - see
/// the composite in the API layer, which is why this type is public.
/// </para>
/// </remarks>
public sealed class DevCurrentActor(IOptions<DevActorOptions> options, DevSeedRegistry seed) : ICurrentActor
{
    private DevActorOptions Options => options.Value;

    public ActorType Type => Options.Type;

    public Guid? StaffMemberId => Options.Type != ActorType.Staff
        ? null
        : Options.StaffMemberId ?? SeededStaffForRole();

    public Guid? DinerUserId => Options.Type == ActorType.Diner ? Options.DinerUserId : null;

    public Guid? ParticipantId => Options.Type == ActorType.Diner ? Options.ParticipantId : null;

    public StaffRole? Role => Options.Type == ActorType.Staff ? Options.Role : null;

    /// <summary>
    /// Picks the seeded staff member matching the configured role, so switching
    /// <c>DevActor:Role</c> to Manager also switches to an actual manager row - the audit log
    /// records a real staff member either way, and the foreign key on
    /// <c>TableSession.SeatedByStaffId</c> resolves.
    /// </summary>
    private Guid? SeededStaffForRole() => Options.Role switch
    {
        StaffRole.Manager or StaffRole.Owner => seed.ManagerId ?? seed.WaiterId,
        _ => seed.WaiterId,
    };
}
