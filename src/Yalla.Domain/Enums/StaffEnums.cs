namespace Yalla.Domain.Enums;

/// <summary>What a staff member is allowed to do, coarsely. Authorisation itself is a later task.</summary>
public enum StaffRole
{
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
