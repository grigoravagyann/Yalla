using Yalla.Domain.Enums;

namespace Yalla.Domain.Staff;

/// <summary>
/// Who may give whom which role. One function, so the staff-management service and its tests
/// read the same table.
/// </summary>
/// <remarks>
/// <para>
/// Lower numbers outrank higher ones: <c>PlatformAdmin = 0</c> above <c>Owner = 1</c> above
/// <c>Manager = 2</c>. The rules the product asks for are:
/// </para>
/// <list type="bullet">
/// <item>A <b>manager</b> may create and edit waiters and kitchen staff, and nobody above them.</item>
/// <item>An <b>owner</b> may create and edit managers, and may add another owner - a co-owner is a
/// normal thing for a family business.</item>
/// <item>A <b>platform admin</b> may assign any venue role, but never mint another platform admin
/// through a venue's staff list: those come from configuration, on purpose.</item>
/// <item>Nobody changes their <i>own</i> role. That is checked by the service, because it needs
/// to know who is acting, not just what rank they hold.</item>
/// </list>
/// </remarks>
public static class StaffRoleRules
{
    /// <summary>Whether <paramref name="actor"/> may create someone as, or change someone to, <paramref name="target"/>.</summary>
    public static bool MayAssign(StaffRole actor, StaffRole target) => actor switch
    {
        StaffRole.PlatformAdmin => target != StaffRole.PlatformAdmin,
        StaffRole.Owner => target is StaffRole.Owner or StaffRole.Manager or StaffRole.Waiter or StaffRole.Kitchen,
        StaffRole.Manager => target is StaffRole.Waiter or StaffRole.Kitchen,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="actor"/> may edit a person who currently holds <paramref name="subject"/>.
    /// Same table as <see cref="MayAssign"/>: you may touch the people you could have created.
    /// </summary>
    public static bool MayManage(StaffRole actor, StaffRole subject) => MayAssign(actor, subject);

    /// <summary>
    /// Seniority, lowest number most senior. <b>Deliberately not the enum's own values.</b>
    /// </summary>
    /// <remarks>
    /// The enum's numbers are storage: they are pinned to what is already on disk and carry gaps
    /// where members were retired, so their order says nothing about authority and must never be
    /// read as if it did. <c>PlatformAdmin</c> happens to be 0 today; the day somebody retires a
    /// role and leaves a gap, an ordinal comparison would quietly re-rank the hierarchy. This map
    /// is the hierarchy, and it changes only when somebody means to change it.
    /// </remarks>
    private static readonly IReadOnlyDictionary<StaffRole, int> Seniority = new Dictionary<StaffRole, int>
    {
        [StaffRole.PlatformAdmin] = 0,
        [StaffRole.Owner] = 1,
        [StaffRole.Manager] = 2,
        [StaffRole.Waiter] = 3,
        [StaffRole.Kitchen] = 4,
    };

    /// <summary>Strictly higher in the hierarchy.</summary>
    public static bool Outranks(StaffRole actor, StaffRole other) => RankOf(actor) < RankOf(other);

    /// <summary>
    /// Where a role sits, most senior first. Throws for a role with no place in the hierarchy,
    /// because silently ranking it last is how an unranked role ends up able to do nothing or
    /// everything depending on which way the comparison happens to run.
    /// </summary>
    public static int RankOf(StaffRole role) =>
        Seniority.TryGetValue(role, out var rank)
            ? rank
            : throw new ArgumentOutOfRangeException(
                nameof(role), role, "This role has no place in the seniority map. Add it deliberately.");
}
