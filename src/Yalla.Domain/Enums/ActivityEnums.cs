namespace Yalla.Domain.Enums;

/// <summary>
/// Which part of the venue's day an activity entry is about - the console History page's filter. Persisted as an int
/// on <c>ActivityEntries.Category</c> and pinned; on the wire it is the lowercase word (<c>booking</c>, <c>table</c>,
/// ...), which is also what the feed's <c>category</c> filter takes.
/// </summary>
public enum ActivityCategory
{
    /// <summary>A booking: made, approved, declined, moved, cancelled, seated, released, no-show.</summary>
    Booking = 1,

    /// <summary>A table or its tab: a walk-in seated, a table freed, held, transferred, a call claimed.</summary>
    Table = 2,

    /// <summary>An order: sent to the kitchen, ready, a line voided.</summary>
    Order = 3,

    /// <summary>Money: cash taken, a discount or comp, a bill written off.</summary>
    Payment = 4,

    /// <summary>A takeaway order: accepted, declined, ready, collected, not collected.</summary>
    Takeaway = 5,

    /// <summary>A guest message the venue wrote, or the on-my-way question.</summary>
    Message = 6,

    /// <summary>The branch's settings: menu prices and availability, hours, policy, takeaway, messages.</summary>
    Settings = 7,

    /// <summary>The team: people added or changed, PINs, sign-in links.</summary>
    Staff = 8,

    /// <summary>The branch's tablets and phones: enrolled, revoked.</summary>
    Device = 9,
}

/// <summary>Who did what an activity entry records. Persisted as an int on <c>ActivityEntries.ActorKind</c> and pinned.</summary>
public enum ActivityActorKind
{
    /// <summary>A member of the venue's staff, named on the entry.</summary>
    Staff = 1,

    /// <summary>The guest - a booking made or cancelled, an answer to "are you on your way".</summary>
    Diner = 2,

    /// <summary>Nobody: a deadline passed and the system acted (a booking nobody answered, declined).</summary>
    System = 3,

    /// <summary>Somebody at Yalla acting on the venue's behalf.</summary>
    PlatformAdmin = 4,
}
