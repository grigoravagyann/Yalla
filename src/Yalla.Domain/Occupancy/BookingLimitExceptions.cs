namespace Yalla.Domain.Occupancy;

/// <summary>
/// The booker's phone number already holds an active booking - pending or confirmed, and not over -
/// anywhere on the platform. One number, one active booking (decided 2026-10-20), for everybody.
/// </summary>
/// <remarks>
/// <para>
/// 409 <c>phone-has-active-booking</c>. The other booking is named only to the account that holds it:
/// <see cref="Existing"/> is set when the caller is that account, so the app can say "You already have a
/// booking at Café Aram on 22 October at 19:30" and link to it. Anybody else - a web visitor, or an account
/// that typed a number some other account books with - gets the plain sentence and no context: the
/// number has a booking, and nothing about where or when.
/// </para>
/// <para>
/// Changing the booking the number already holds is never refused by this rule; cancelling it frees the
/// number at once.
/// </para>
/// </remarks>
public sealed class PhoneHasActiveBookingException(ActiveBookingFacts? existing)
    : DomainStateException(
        existing is null
            ? "This phone number already has a booking that is still active. Cancel or finish that one first, "
              + "or use its link to change it."
            : $"You already have a booking at {existing.VenueName} on {existing.LocalDate:yyyy-MM-dd} at "
              + $"{existing.LocalStartTime:HH\\:mm}. One phone number can hold one booking at a time - change "
              + "or cancel that one first.")
{
    /// <summary>The booking already held, when the caller is its own account; otherwise null.</summary>
    public ActiveBookingFacts? Existing { get; } = existing;
}

/// <summary>What a caller is told about their own active booking when they try to make a second.</summary>
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

/// <summary>
/// The browser booking without a proved number already holds an active booking made the same way.
/// 409 <c>device-has-active-booking</c>, with nothing about that booking.
/// </summary>
public sealed class DeviceHasActiveBookingException()
    : DomainStateException(
        "A booking made from this browser without a verified number is still active. Cancel it from its link "
        + "first, or verify your number to book again.");

/// <summary>
/// A booking with an unproved phone number for more people than the branch takes on trust
/// (<c>ReservationPolicy.UnverifiedMaxPartySize</c>). 422 <c>reservation-unverified-party-too-large</c>.
/// </summary>
/// <remarks>The client offers to verify the number, which lifts the cap.</remarks>
public sealed class UnverifiedPartyTooLargeException(int partySize, int maxPartySize)
    : ReservationRejectedException(
        $"Without a verified phone number this venue takes bookings for up to {maxPartySize}. Verify your "
        + "number to book for more.")
{
    public const string ErrorCode = "reservation-unverified-party-too-large";

    public int PartySize { get; } = partySize;

    public int MaxPartySize { get; } = maxPartySize;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.UnverifiedPartyTooLarge;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Context =>
        ContextWith(("partySize", PartySize), ("maxPartySize", MaxPartySize));
}

/// <summary>
/// The manage link of a booking made without a proved number cannot change it - cancel and book again.
/// 403 <c>phone-not-verified</c>, as the link's other refusal to change a booking.
/// </summary>
public sealed class UnverifiedBookingNotChangeableException()
    : Exception(
        "A booking made without a verified phone number can't be changed online. Cancel it and book again, "
        + "or call the venue.");
