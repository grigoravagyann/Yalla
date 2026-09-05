namespace Yalla.Domain.Enums;

/// <summary>What a staff member is allowed to do, coarsely. Authorisation itself is a later task.</summary>
public enum StaffRole
{
    /// <summary>
    /// Runs Yalla itself. Belongs to no venue and no branch - a <c>StaffMember</c> row with this
    /// role and a venue id is invalid - and passes every scope check for every venue.
    /// </summary>
    PlatformAdmin = 0,

    Owner = 1,
    Manager = 2,
    Waiter = 3,
    Kitchen = 4,
}

/// <summary>Who caused a logged change.</summary>
public enum ActorType
{
    Diner = 1,
    Staff = 2,

    /// <summary>A background process: hold expiry, no-show sweep, auto-release.</summary>
    System = 3,
}
