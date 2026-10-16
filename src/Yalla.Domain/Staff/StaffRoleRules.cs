using Yalla.Domain.Enums;

namespace Yalla.Domain.Staff;

/// <summary>
/// Who may give whom which role. One function, so the staff-management service and its tests
/// read the same table.
/// </summary>
/// <remarks>
/// <para>
/// Rank comes from <see cref="Seniority"/>, never from the enum's numbers:
/// <c>PlatformAdmin</c> above <c>Owner</c> above <c>GeneralManager</c> above <c>Manager</c> (a
/// branch manager) above <c>Waiter</c> and <c>Kitchen</c>. The rules the product asks for are:
/// </para>
/// <list type="bullet">
/// <item>A <b>branch manager</b> may create and edit waiters and kitchen staff, and nobody above
/// them - and only at their own branch, which the staff service checks from the stored rows.</item>
/// <item>A <b>general manager</b> may create and edit branch managers, waiters and kitchen staff at
/// any branch of their venue. Never an owner or another general manager.</item>
/// <item>An <b>owner</b> may create and edit general managers and everyone below, and may add another
/// owner - a co-owner is a normal thing for a family business.</item>
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
        StaffRole.Owner => target is StaffRole.Owner or StaffRole.GeneralManager or StaffRole.Manager
            or StaffRole.Waiter or StaffRole.Kitchen,
        StaffRole.GeneralManager => target is StaffRole.Manager or StaffRole.Waiter or StaffRole.Kitchen,
        StaffRole.Manager => target is StaffRole.Waiter or StaffRole.Kitchen,
        _ => false,
    };

    /// <summary>
    /// The venue roles whose job is every branch: an owner and a general manager. They pass the
    /// branch checks for any branch of their own venue; everybody else is held to their own branch.
    /// </summary>
    public static bool CoversWholeVenue(StaffRole role) => role is StaffRole.Owner or StaffRole.GeneralManager;

    /// <summary>
    /// Manager or above inside a venue: owner, general manager, branch manager. Not the platform
    /// tier, which belongs to no venue and is answered separately wherever it applies.
    /// </summary>
    public static bool ManagesAtVenue(StaffRole role) =>
        role is StaffRole.Owner or StaffRole.GeneralManager or StaffRole.Manager;

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
        [StaffRole.GeneralManager] = 2,
        [StaffRole.Manager] = 3,
        [StaffRole.Waiter] = 4,
        [StaffRole.Kitchen] = 5,
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
