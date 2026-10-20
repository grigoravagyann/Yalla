namespace Yalla.Domain.Occupancy;

/// <summary>
/// The booker's proved phone number cannot take this booking: it already holds
/// <see cref="BookingLimits.MaxActiveBookingsPerNumber"/> active bookings. 409 <c>phone-has-active-booking</c>.
/// </summary>
/// <remarks>
/// <para>
/// Replaced "one active booking per number" (Yalla#54) on 2026-10-20, the user's revision: no two overlapping
/// bookings anywhere (that one is <see cref="ReservationOverlapsOwnException"/>, about the booker's own booking)
/// and at most three active per number - this one.
/// </para>
/// <para>
/// The other booking is named only to the account that holds it: <see cref="Existing"/> is the caller's own
/// earliest active booking, so the app can say "the next is Café Aram on 22 October at 19:30" and link to it.
/// </para>
/// <para>
/// Moving a booking the number already holds is never refused by this rule; cancelling one frees a place at once.
/// </para>
/// </remarks>
public sealed class PhoneHasActiveBookingException : DomainStateException
{
    /// <summary>The number already holds the most active bookings it may.</summary>
    /// <param name="existing">The caller's own earliest active booking, or null when none of them is theirs.</param>
    /// <param name="maxActiveBookings">The limit, <see cref="BookingLimits.MaxActiveBookingsPerNumber"/>.</param>
    public PhoneHasActiveBookingException(ActiveBookingFacts? existing, int maxActiveBookings)
        : base(
            $"You already have {maxActiveBookings} active bookings - the most one phone number can hold at once. "
            + (existing is null
                ? "Cancel one, or wait until one is over."
                : $"The next is at {existing.VenueName} on {existing.LocalDate:yyyy-MM-dd} at "
                  + $"{existing.LocalStartTime:HH\\:mm}. Cancel one, or wait until one is over."))
    {
        Existing = existing;
        MaxActiveBookings = maxActiveBookings;
    }

    /// <summary>The caller's own earliest active booking, when the refusal is the limit and one is theirs; otherwise null.</summary>
    public ActiveBookingFacts? Existing { get; }

    /// <summary>The limit that refused it.</summary>
    public int MaxActiveBookings { get; }
}

/// <summary>What a caller is told about their own active booking when the number's limit refuses another.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="LocalDate">The branch's calendar date it is on.</param>
/// <param name="LocalStartTime">Its start on the branch's wall clock.</param>
/// <param name="VenueName">The venue.</param>
/// <param name="BranchName">The branch.</param>
public sealed record ActiveBookingFacts(
    Guid ReservationId,
    string Code,
    DateTime StartUtc,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    string VenueName,
    string BranchName);
