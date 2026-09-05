namespace Yalla.Domain.Enums;

/// <summary>What a staff member is allowed to do, coarsely. Authorisation itself is a later task.</summary>
public enum StaffRole
{
    /// <summary>
    /// Nobody. The zero value, and deliberately powerless.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This member exists because <c>PlatformAdmin</c> used to be 0.</b> An unset field, a
    /// deserialisation default, a row inserted by a path that forgot to set a role - every one of
    /// those produced a platform administrator, silently, and every one of them is the kind of
    /// mistake that gets made once. A default that grants everything is a vulnerability waiting for
    /// an ordinary bug to trigger it.
    /// </para>
    /// <para>
    /// It is absent from <c>StaffRoleRules.Seniority</c> on purpose, so <c>RankOf</c> throws rather
    /// than ranking it. A role nobody set is a bug to be surfaced, not a permission level to be
    /// resolved.
    /// </para>
    /// </remarks>
    Unknown = 0,

    Owner = 1,
    Manager = 2,
    Waiter = 3,
    Kitchen = 4,

    /// <summary>
    /// Runs Yalla itself. Belongs to no venue and no branch - a <c>StaffMember</c> row with this
    /// role and a venue id is invalid - and passes every scope check for every venue.
    /// </summary>
    /// <remarks>
    /// Moved from 0 to 5 in Prompt 8b. Prompt 7 decided against renumbering enums in general and
    /// that decision stands; this one value was the exception, because the number it held was the
    /// default and the remap was free while nothing had shipped. It will never be free again.
    /// </remarks>
    PlatformAdmin = 5,
}

/// <summary>Who caused a logged change.</summary>
public enum ActorType
{
    Diner = 1,
    Staff = 2,

    /// <summary>A background process: hold expiry, no-show sweep, auto-release.</summary>
    System = 3,
}
