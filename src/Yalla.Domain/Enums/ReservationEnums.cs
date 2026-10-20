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

/// <summary>Why a booking needs a human before it is promised.</summary>
/// <remarks>
/// <para>
/// Stored on the booking (<c>Reservation.ApprovalTrigger</c>) as the reason it was given when it went
/// to <see cref="ReservationStatus.PendingApproval"/>, so a policy edited since cannot change what the
/// diner and the venue were told. Rows from before the column was added carry none and have theirs
/// worked out again from the branch's policy.
/// </para>
/// <para>
/// When more than one applies, the first in this order is the one stored: <see cref="ShortNotice"/>,
/// <see cref="EarlierGuestConsent"/>, <see cref="BranchApprovesEveryBooking"/>, <see cref="UnverifiedPhone"/>,
/// <see cref="LargeParty"/>, <see cref="NoShowHistory"/>. Short notice leads because it is the one with a deadline - fifteen
/// minutes to answer - and the one the floor, not the office, decides; the earlier guest's two hours
/// come next. Persisted as its number: a member keeps it for good.
/// </para>
/// </remarks>
public enum ApprovalTrigger
{
    /// <summary>The branch does not auto-confirm anything.</summary>
    BranchApprovesEveryBooking = 1,

    /// <summary>The party is above the branch's <c>ApprovalRequiredAbovePartySize</c>.</summary>
    LargeParty = 2,

    /// <summary>
    /// The diner is over the rolling no-show threshold. See <c>NoShowPolicy</c> - this costs instant
    /// confirmation and nothing else.
    /// </summary>
    NoShowHistory = 3,

    /// <summary>
    /// The booking is for the branch's today, or starts less than six hours from now. Somebody may
    /// have just sat down at that table, so the floor decides - within fifteen minutes, or the booking
    /// is declined for them. See <c>ShortNoticeRule</c>.
    /// </summary>
    ShortNotice = 4,

    /// <summary>
    /// The table has a booking less than three hours before this one - somebody who may still be
    /// sitting there - so whoever has it first is asked whether they will have left in time: the
    /// earlier guest in the app, or the floor when that guest booked on the web or has no phone to push
    /// to. Two hours to answer either way, never past the earlier booking's start; unanswered, it is
    /// declined - <c>EarlierGuestNoAnswer</c> in the app, <c>NoAnswer</c> by the floor. See
    /// <c>TableSequencing</c> and <c>TableShareRequest</c>.
    /// </summary>
    EarlierGuestConsent = 5,

    /// <summary>
    /// Nobody proved the booker's phone number - a web visitor who booked without verifying, or an app
    /// account whose number was never proved - and the branch approves such bookings first
    /// (<c>ReservationPolicy.UnverifiedBookingsNeedApproval</c>, on by default). No deadline: the venue
    /// decides, as for a large party.
    /// </summary>
    UnverifiedPhone = 6,
}

/// <summary>Why a booking that was waiting for the venue was declined.</summary>
/// <remarks>
/// Set by a decline only - a member of staff saying no, or the answer window running out - and never
/// by a venue letting an accepted booking go, which is <see cref="ReservationStatus.CancelledByVenue"/>
/// with no decline reason. Declines from before the column was added carry none either. Persisted as
/// its number.
/// </remarks>
public enum DeclineReason
{
    /// <summary>A member of staff declined it.</summary>
    StaffDeclined = 1,

    /// <summary>
    /// Nobody at the venue answered in time, so it was declined for them: the fifteen minutes at short
    /// notice, or the two hours of a booking the floor was to decide after calling the guest before it.
    /// </summary>
    NoAnswer = 2,

    /// <summary>The guest with the table before this booking said they would not have left in time.</summary>
    EarlierGuestDeclined = 3,

    /// <summary>The guest with the table before this booking did not answer within the two hours.</summary>
    EarlierGuestNoAnswer = 4,
}

/// <summary>Where a request to share a table's evening stands. See <c>TableShareRequest</c>.</summary>
/// <remarks>Persisted as its number: a member keeps it for good.</remarks>
public enum TableShareRequestStatus
{
    /// <summary>Waiting for the earlier guest - or the floor - to answer.</summary>
    Pending = 1,

    /// <summary>Yes: the earlier booking promised to free the table in time and the later one was confirmed.</summary>
    Accepted = 2,

    /// <summary>No: the later booking was declined, or went back to the slot it had.</summary>
    Declined = 3,

    /// <summary>Nobody answered in time; the later booking was declined for it.</summary>
    Expired = 4,

    /// <summary>
    /// The question went away before anybody answered it: the later booking was moved or cancelled, or
    /// the earlier one got out of its way - cancelled, released, declined, or moved off the table.
    /// </summary>
    Withdrawn = 5,
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

/// <summary>
/// Where a booking was made from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-reported by the client, and a metric rather than a boundary.</b> Nothing is authorised
/// on it and nothing refuses a booking because of it, so a client that lies costs a wrong number in
/// a report and nothing else. Recording it needs the client's cooperation because the server cannot
/// tell an app's HTTPS request from a browser's.
/// </para>
/// <para>
/// It exists to answer one question that is about to force a commercial decision: a diner who books
/// from the public branch page has no app, so the reminder, the late nudge and one-tap cancel - the
/// entire no-show story - cannot reach them. <c>docs/reports.md</c> explains what number would
/// justify integrating an SMS or Telegram channel, and this is half of that number. The other half
/// is whether they have a live push device, which the server does know.
/// </para>
/// </remarks>
public enum ReservationChannel
{
    /// <summary>Not stated. Every booking made before the column existed, and any client that omits it.</summary>
    Unknown = 0,

    /// <summary>The diner app, which can be pushed to.</summary>
    App = 1,

    /// <summary>The public branch page. No app, so no push channel.</summary>
    Web = 2,

    /// <summary>Taken by a person - over the phone, or at the door.</summary>
    Staff = 3,
}
