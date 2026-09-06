using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Who the development stub pretends to be, from <c>appsettings.Development.json</c>.
/// </summary>
/// <remarks>
/// Exists so the whole permission surface can be exercised without authentication: flip
/// <see cref="Role"/> to <c>Manager</c> and the manager-only operations start working, flip it
/// back and they refuse. When real auth arrives this options class and
/// <see cref="DevCurrentActor"/> are deleted and one registration changes.
/// </remarks>
public sealed class DevActorOptions
{
    public const string SectionName = "DevActor";

    /// <summary>Off unless explicitly enabled, so it can never be switched on by accident.</summary>
    public bool Enabled { get; set; }

    public ActorType Type { get; set; } = ActorType.Staff;

    public StaffRole Role { get; set; } = StaffRole.Waiter;

    /// <summary>
    /// Pins a specific staff member. When null, the stub uses the seeded waiter or manager that
    /// matches <see cref="Role"/>, so the common case needs no ids in configuration at all.
    /// </summary>
    public Guid? StaffMemberId { get; set; }

    public Guid? DinerUserId { get; set; }

    /// <summary>
    /// Pretends to be a tab participant, for poking the ordering endpoints with no token.
    /// </summary>
    /// <remarks>
    /// A tab participant is the one identity type this stub could not stand in for, which meant
    /// the ordering path could not be exercised without minting a real token - and that is exactly
    /// the path that turned out never to have worked. Set this alongside
    /// <c>Type: Diner</c> and leave <see cref="DinerUserId"/> null, which is the shape a real
    /// participant token produces.
    /// </remarks>
    public Guid? ParticipantId { get; set; }
}
