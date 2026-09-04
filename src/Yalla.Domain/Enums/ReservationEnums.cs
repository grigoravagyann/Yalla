namespace Yalla.Domain.Enums;

/// <summary>
/// Lifecycle of a booking. Every member is the result of somebody doing something.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately <b>no <c>Late</c></b> member. Lateness is a pure function of the clock -
/// <c>now &gt; StartUtc + GraceMinutes</c> - computed from data already on the row and the
/// branch's own policy. Storing it would need a timer flipping rows, which can stall, can
/// disagree with the tablet's clock, and can leave a cancelled booking marked late forever. Use
/// <c>Reservation.IsLateAt</c> instead.
/// </para>
/// <para>
/// The <b>late nudge push</b> - the message sent to the diner at start + <c>LateNudgeAfterMinutes</c> -
/// is a different concern and is <i>not</i> solved by deriving lateness. That is a genuine
/// scheduled action with an at-most-once delivery obligation, so it needs an outbox job and its
/// own delivery record. It is out of scope here; the two must not be conflated.
/// </para>
/// <para>
/// Numeric values are not contiguous: <c>3</c> was <c>Late</c> and is permanently retired so it
/// can never be reused and mean two things.
/// </para>
/// </remarks>
public enum ReservationStatus
{
    /// <summary>Awaiting a staff decision because the party is larger than the branch's approval threshold.</summary>
    PendingApproval = 1,

    /// <summary>The table is held for the party for the booked interval.</summary>
    Confirmed = 2,

    // 3 was Late. Permanently retired - lateness is derived. Do not reuse.

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
