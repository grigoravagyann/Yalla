namespace Yalla.Domain.Enums;

/// <summary>Lifecycle of a booking, from request through to seated, completed or lost.</summary>
public enum ReservationStatus
{
    /// <summary>Awaiting a staff decision because the party is larger than the branch's approval threshold.</summary>
    PendingApproval = 1,

    /// <summary>The table is held for the party for the booked interval.</summary>
    Confirmed = 2,

    /// <summary>Start time has passed with nobody seated; the branch's grace window is running.</summary>
    Late = 3,

    /// <summary>The party arrived and a <c>TableSession</c> was opened.</summary>
    Seated = 4,

    /// <summary>The session closed normally.</summary>
    Completed = 5,

    CancelledByDiner = 6,
    CancelledByVenue = 7,

    /// <summary>Grace ran out and the table was released.</summary>
    NoShow = 8,
}

/// <summary>
/// Optional hint from the diner about how long they expect to stay. Advisory only:
/// <c>Reservation.EndUtc</c> is derived from the branch turn time, not from this.
/// </summary>
public enum StayHint
{
    OneHour = 1,
    TwoHours = 2,
    ThreeHoursPlus = 3,
}

/// <summary>How a party came to occupy a table.</summary>
public enum TableSessionSource
{
    /// <summary>Seated against a booking; <c>TableSession.ReservationId</c> is set.</summary>
    Reservation = 1,

    /// <summary>Seated without a booking - the majority of cafe traffic, and anyone who scans the table QR code.</summary>
    WalkIn = 2,
}
