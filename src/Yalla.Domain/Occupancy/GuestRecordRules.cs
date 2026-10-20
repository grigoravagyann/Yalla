namespace Yalla.Domain.Occupancy;

/// <summary>
/// The product's rules about one guest's bookings across every venue: the guest record's window, what a good
/// record is, and how many bookings one proved phone number may hold. Platform-wide limits on one person, not a
/// venue's settings - so constants, documented in <c>docs/reservations.md</c>.
/// </summary>
public static class GuestRecordRules
{
    /// <summary>The guest record looks back this many days: twelve months.</summary>
    public const int WindowDays = 365;

    /// <summary>A good record has at least this many visits in the window.</summary>
    public const int GoodRecordMinimumCame = 3;

    /// <summary>
    /// Whether a record is good enough for a venue that auto-confirms guests with a good record
    /// (<c>ReservationPolicy.AutoConfirmGuestsWithGoodRecord</c>): came at least
    /// <see cref="GoodRecordMinimumCame"/> times, never missed a booking, never cancelled late.
    /// </summary>
    /// <remarks>
    /// Strict on purpose. The setting skips a venue's own look at a booking; the cost of being wrong is a table
    /// held for somebody who does not come, while the cost of being too strict is only that the venue looks at
    /// the booking as it would have anyway.
    /// </remarks>
    public static bool IsGood(int came, int noShows, int lateCancellations) =>
        came >= GoodRecordMinimumCame && noShows == 0 && lateCancellations == 0;
}

/// <summary>Limits on what one proved phone number may hold, at every venue.</summary>
public static class BookingLimits
{
    /// <summary>
    /// The most bookings one proved number holds at once - pending or confirmed, and not over. Three: a birthday,
    /// a business lunch and a weekend table; a fourth is far more likely somebody holding tables they will not
    /// use. Replaced "one active booking per number" (2026-10-20).
    /// </summary>
    public const int MaxActiveBookingsPerNumber = 3;

    /// <summary>
    /// How far apart two sittings of one guest at <b>different</b> venues must be: half an hour to get from one to
    /// the other. With the shipped 90-minute restaurant turn, starts less than two hours apart are refused - the
    /// "about two hours" the user agreed. At the same branch the sittings themselves decide (back to back is fine).
    /// </summary>
    public const int OtherVenueGapMinutes = 30;
}

/// <summary>
/// Three no-shows in three days blocks a guest from booking for a week, and cancels every booking of theirs not
/// yet started (the user's rule of 2026-10-20). Product constants, documented in <c>docs/reservations.md</c>.
/// </summary>
/// <remarks>
/// The window is over the <b>bookings' start times</b>, ending when the third no-show is marked: the start is when
/// the guest failed to come; when a waiter got round to marking it is the venue's business. A venue marking last
/// week's no-shows together at closing time blocks nobody; one marking late does not let the third slip.
/// </remarks>
public static class NoShowBlockRules
{
    /// <summary>How many no-shows start a block.</summary>
    public const int Threshold = 3;

    /// <summary>Within how many hours of start times, ending now.</summary>
    public const int WindowHours = 72;

    /// <summary>How long the block lasts.</summary>
    public const int BlockDays = 7;

    /// <summary>
    /// A no-show may be withdrawn until this hour, branch time, the morning after the booking's date - the end of its
    /// service day, late openings included.
    /// </summary>
    public const int UndoUntilLocalHourNextDay = 5;

    /// <summary>What a booking cancelled by a block says, for the venue reading it.</summary>
    public const string CancellationReason = "Cancelled automatically: the guest missed three bookings in three days.";
}
