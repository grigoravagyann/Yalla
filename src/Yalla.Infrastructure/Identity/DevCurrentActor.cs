using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Development stand-in for authentication: reports a configured staff member, defaulting to the
/// seeded waiter.
/// </summary>
/// <remarks>
/// <b>Development only.</b> Registration is gated on <see cref="DevActorOptions.Enabled"/>, and
/// nothing registers it outside Development. Replacing this one registration with a real
/// claims-reading implementation is the entire change when authentication arrives - which is the
/// point of routing every service through <see cref="ICurrentActor"/> now rather than later.
/// </remarks>
internal sealed class DevCurrentActor(IOptions<DevActorOptions> options, DevSeedRegistry seed) : ICurrentActor
{
    private DevActorOptions Options => options.Value;

    public ActorType Type => Options.Type;

    public Guid? StaffMemberId => Options.Type != ActorType.Staff
        ? null
        : Options.StaffMemberId ?? SeededStaffForRole();

    public Guid? DinerUserId => Options.Type == ActorType.Diner ? Options.DinerUserId : null;

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
