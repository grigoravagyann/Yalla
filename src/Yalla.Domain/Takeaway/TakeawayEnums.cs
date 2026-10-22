namespace Yalla.Domain.Takeaway;

/// <summary>
/// Where a takeaway order stands. Stored as an int, so every number is pinned (<c>PersistedEnumValueTests</c>).
/// </summary>
/// <remarks>
/// <c>Sent → Accepted → Ready → Collected</c>, with three ends off the way: <see cref="Declined"/> (the venue said no, or
/// nobody answered within five minutes), <see cref="Cancelled"/> (the guest, only while <see cref="Sent"/>) and
/// <see cref="NoShow"/> (ready, and nobody came for it within thirty minutes). See <c>docs/takeaway.md</c>.
/// </remarks>
public enum TakeawayStatus
{
    /// <summary>The guest sent it; the venue has not answered yet. The guest may still cancel.</summary>
    Sent = 1,

    /// <summary>The venue accepted it with a ready time: it is in the kitchen. "Accepted · being prepared".</summary>
    Accepted = 2,

    /// <summary>The kitchen finished it: waiting at the counter for the guest.</summary>
    Ready = 3,

    /// <summary>Handed over and paid at the venue. Final.</summary>
    Collected = 4,

    /// <summary>The venue said no, or nobody answered in time. Final.</summary>
    Declined = 5,

    /// <summary>The guest cancelled before the venue answered. Final.</summary>
    Cancelled = 6,

    /// <summary>Ready, and nobody came for it. Final.</summary>
    NoShow = 7,
}

/// <summary>How the guest paid at the counter. The venue's own terminal or drawer; nothing is charged in the app.</summary>
public enum TakeawayPaymentMethod
{
    Cash = 1,
    Card = 2,
}

/// <summary>Why a takeaway order was declined.</summary>
public enum TakeawayDeclineCause
{
    /// <summary>Somebody at the venue said no, with or without a reason.</summary>
    StaffDeclined = 1,

    /// <summary>Nobody at the venue answered within the five minutes: the sweep declined it.</summary>
    NoAnswer = 2,
}

/// <summary>Why a dish could not go on a takeaway order: <c>context.items[].reason</c> of 422 <c>takeaway-items-unavailable</c>.</summary>
public enum TakeawayItemRefusal
{
    /// <summary>Not on this branch's menu as a diner sees it: unknown, another branch's, or not complete.</summary>
    NotOnMenu = 1,

    /// <summary>On the menu and sold out.</summary>
    SoldOut = 2,

    /// <summary>The venue serves it in the hall only.</summary>
    NotForTakeaway = 3,
}
