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
}
