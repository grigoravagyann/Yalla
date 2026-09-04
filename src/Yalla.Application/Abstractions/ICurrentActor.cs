using Yalla.Domain.Enums;

namespace Yalla.Application.Abstractions;

/// <summary>
/// Who is making the current request.
/// </summary>
/// <remarks>
/// <para>
/// Every service that changes state takes this by injection and writes it into the audit log, so
/// there is no code path that mutates a table without recording who did it.
/// </para>
/// <para>
/// Authentication is the next task. Until it arrives a development stub supplies a seeded
/// waiter; when real auth lands, replacing that single registration is the whole change, because
/// nothing else in the system reads identity from anywhere else.
/// </para>
/// </remarks>
public interface ICurrentActor
{
    /// <summary>Whether a diner, a staff member, or the system itself is acting.</summary>
    ActorType Type { get; }

    /// <summary>The acting staff member, when <see cref="Type"/> is <see cref="ActorType.Staff"/>.</summary>
    Guid? StaffMemberId { get; }

    /// <summary>The acting diner, when <see cref="Type"/> is <see cref="ActorType.Diner"/>.</summary>
    Guid? DinerUserId { get; }

    /// <summary>The acting staff member's role, used for the manager-only operations.</summary>
    StaffRole? Role { get; }
}
