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
/// The branch takes no bookings without a verified phone number at all (<c>ReservationPolicy.UnverifiedMaxPartySize</c>
/// is 0). 422 <c>branch-requires-verified-phone</c>, <c>context.reason</c> 15.
/// </summary>
/// <remarks>
/// Its own code rather than <c>reservation-unverified-party-too-large</c> with a cap of 0: the client's answer
/// is not "a smaller party" but "verify your number to book here", and it should not have to infer that from a
/// zero.
/// </remarks>
public sealed class VerifiedPhoneRequiredException()
    : ReservationRejectedException(
        "This venue takes bookings only from a verified phone number. Verify your number to book.")
{
    public const string ErrorCode = "branch-requires-verified-phone";

    public override ReservationRejectionReason Reason => ReservationRejectionReason.VerifiedPhoneRequired;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Context => ContextWith();
}

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

/// <summary>
/// The rules a booking made without a proved number lives by that are the product's, not a branch's:
/// how long the venue has to answer one, and how many may wait at one branch at once.
/// </summary>
public static class UnverifiedBookingRules
{
    /// <summary>
    /// How many bookings without a proved number may wait for one branch's answer at once (pending, not yet
    /// started, window open). Twenty is more than a real evening produces and few enough that a script cycling
    /// numbers cannot hold every table of a branch until somebody declines each by hand.
    /// </summary>
    public const int OutstandingPendingPerBranch = 20;

    /// <summary>The longest the venue has to answer one: a day.</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromHours(24);

    /// <summary>How long before its start the visitor has their answer at the latest.</summary>
    public static readonly TimeSpan AnsweredBeforeStart = TimeSpan.FromHours(2);

    /// <summary>
    /// When a booking without a proved number that waits for the venue is declined for nobody answering:
    /// a day after it was made, or two hours before it starts, whichever is sooner - never past the start.
    /// </summary>
    /// <remarks>
    /// Such a booking never waits at short notice (tonight, or under six hours away: that is
    /// <see cref="ShortNoticeRule"/>'s fifteen minutes), so two hours before the start is at least four hours
    /// from now. Unanswered, <c>ApprovalExpirySweep</c> declines it with <c>NoAnswer</c> and it holds nothing.
    /// </remarks>
    public static DateTime AnswerDeadline(DateTime nowUtc, DateTime startUtc)
    {
        var byWindow = nowUtc.Add(AnswerWindow);
        var beforeStart = startUtc.Subtract(AnsweredBeforeStart);
        var deadline = byWindow < beforeStart ? byWindow : beforeStart;

        return deadline > nowUtc ? deadline : startUtc;
    }
}

/// <summary>
/// The branch already has <see cref="UnverifiedBookingRules.OutstandingPendingPerBranch"/> bookings without a
/// proved number waiting for its answer. 409 <c>branch-unverified-queue-full</c>, no context: verifying the
/// number books as usual.
/// </summary>
public sealed class BranchUnverifiedQueueFullException()
    : DomainStateException(
        "This venue has too many bookings waiting for it to confirm right now. Verify your number to book, or "
        + "call the venue.");
