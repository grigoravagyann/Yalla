using Yalla.Domain.Enums;

namespace Yalla.Application.Public;

/// <summary>
/// One booking as its manage link shows it, to somebody holding nothing but the link.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hand-picked subset, and the withholding is the design.</b> The caller here has no account,
/// no session and no identity - a URL is the whole credential, and that URL will be pasted into
/// WhatsApp, left in browser history and read by whoever picks up the phone. So this record carries
/// exactly what the confirmation screen renders and refuses to carry anything a person could be
/// harmed by: no diner id, no phone number, no guest name, no other bookings, no floor state, no
/// venue internals. The two ids it does carry - the booking's and its table's - are what changing the
/// booking from the link needs, and neither opens anything to an anonymous caller.
/// </para>
/// <para>
/// It is its own record rather than a trimmed <c>ReservationView</c> for the same reason
/// <see cref="PublicReservationPolicy"/> is not <c>ReservationPolicyView</c>: a field added to the
/// authenticated shape must not be able to leak onto an anonymous route by being added upstream.
/// This one physically cannot carry <c>GuestPhone</c>.
/// </para>
/// <para>
/// <b>A cancelled or finished booking is returned, not refused.</b> Somebody clicking a link from
/// three weeks ago should learn what happened to their table, and a 404 teaches them only that
/// something is broken. See <see cref="ManageBookingFailure"/> for the cases that genuinely cannot
/// be answered, and for why they all answer identically.
/// </para>
/// </remarks>
/// <param name="VenueName">The venue, as the confirmation names it.</param>
/// <param name="BranchName">Which location.</param>
/// <param name="VenueSlug">
/// The venue's half of the branch's public address, <c>/{venueSlug}/{branchSlug}</c>. The link a reminder
/// carries is <c>/booking/{token}</c>, with no branch in it, so the page needs these to link back to
/// the branch's page and open the booking card there. Public already: the address is the page's.
/// </param>
/// <param name="BranchSlug">The branch's half of that address.</param>
/// <param name="BranchAddress">Where to go.</param>
/// <param name="TimeZoneId">
/// The branch's IANA zone. Every time here is wall-clock in it, and a diner reading this on a
/// phone set to another zone must not be shown a converted time.
/// </param>
/// <param name="TableLabel">
/// The table, as printed on the floor. Absent, with <paramref name="TableId"/>, for a booking made by time only
/// that nobody has seated: the venue chooses its table when the party arrives.
/// </param>
/// <param name="LocalDate">The booked date, as the diner reads it off the confirmation.</param>
/// <param name="LocalStartTime">The booked wall-clock start.</param>
/// <param name="PartySize">How many people.</param>
/// <param name="Status">Where the booking stands now.</param>
/// <param name="Code">The short code quoted at the door.</param>
/// <param name="ReservationId">
/// The booking's id - for one thing: the page that changes the booking sends it as
/// <c>excludeReservationId</c> on the branch's public availability and day-slot reads, so the table
/// the visitor already holds reads as free to them, as the app's change screen does. It opens nothing:
/// every route keyed by a booking id wants the booking's diner or the venue's staff, and the reads that
/// take it say no more about it than that its table is free.
/// </param>
/// <param name="TableId">
/// The booked table's id - so the change screen can start from the table the booking holds, and send it
/// back as <c>diningTableId</c> when only the time or the party changes. Table ids are no secret: the
/// branch's public availability and room photos publish them for every pinned table. The booked one is
/// named even if it has since been unpinned or taken out, which those reads do not list; a move to it is
/// then refused as for any table that cannot be booked.
/// </param>
/// <param name="CancellationDeadlineUtc">
/// The instant past which cancelling is recorded as late. Absolute rather than a number of minutes
/// so the page does not have to reimplement the arithmetic, and so a policy edit cannot silently
/// move a deadline the diner has already been shown.
/// </param>
/// <param name="CanCancel">
/// Whether cancelling would do anything - false once the booking is already cancelled, missed or
/// finished. <b>Not</b> the deadline: cancelling past the deadline is allowed and merely recorded.
/// </param>
/// <param name="CancelledAfterDeadline">
/// Whether this booking's cancellation, if it has one, arrived past the deadline.
/// </param>
/// <param name="Note">
/// The note the diner typed for the venue when booking, or absent (K9). Theirs to see again; it
/// says nothing the link holder did not write.
/// </param>
/// <param name="RoomName">
/// The room the table is in - "Hall", "Terrace" - or absent for a table in no room. Said beside the
/// table label, because "table 4" alone does not tell somebody arriving which room to walk into.
/// </param>
/// <param name="ApprovalExpiresAtUtc">
/// For a booking waiting on a short-notice decision or on the guest before it on the table, when the
/// chance to answer runs out - the page counts down to it and reads the link again until the booking is
/// decided. Null otherwise.
/// </param>
/// <param name="AwaitsEarlierGuestAnswer">
/// True while the booking waits on the guest before it on the table to answer in the app - only they can,
/// and the venue cannot decide it meanwhile - and false otherwise, a wait the venue decides after calling
/// them included. Both count down to <paramref name="ApprovalExpiresAtUtc"/>; this says whose answer the
/// page is waiting for.
/// </param>
/// <param name="DeclinedBecause">
/// Why a booking that was waiting was declined - the venue said no, or nobody answered in time - so
/// the page can apologise for the right thing. Null for a booking that was not declined.
/// </param>
/// <param name="PhoneUnverified">
/// True when the booking was made without a proved phone number. The venue may confirm it first, and it is
/// never changed from the link - cancelled and booked again instead.
/// </param>
/// <param name="CancelledBecause">
/// Set when the platform cancelled it - 1, its guest was blocked for three no-shows in three days.
/// </param>
/// <param name="PreOrder">The booking's pre-order, with its state. Absent when it has none.</param>
/// <param name="PreOrderEditableUntilUtc">
/// Until when a pre-order may be added or changed (two hours before the start). Present only where the branch takes
/// pre-orders and the booking is pending or confirmed and still to come.
/// </param>
/// <param name="GuestConfirmedOnWayAtUtc">When the guest answered "Yes, on my way".</param>
/// <param name="OnYourWayAsked">
/// Ask "Are you on your way?" now: from 45 minutes before the start, for a confirmed booking whose pre-order is waiting
/// (not sent, not marked not coming, not cancelled) and whose guest has not answered.
/// </param>
/// <param name="GuestRunningLateMinutes">"I'm running late": 10, 15 or 30 minutes, the guest's latest answer.</param>
/// <param name="StaffAskedOnMyWayAtUtc">When the venue last asked "are you coming?" from its tablet.</param>
/// <param name="OnMyWayOpensAtUtc">From when "on my way" and "running late" are taken: two hours before the start.</param>
/// <param name="CanChange">
/// Whether <c>POST .../reschedule</c> would be considered at all: the booking is still to come, pending or
/// confirmed, and its account is live with a proved number. False otherwise - hide "Change".
/// </param>
public sealed record PublicBookingView(
    string VenueName,
    string BranchName,
    string VenueSlug,
    string BranchSlug,
    string BranchAddress,
    string TimeZoneId,
    string? TableLabel,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    int PartySize,
    ReservationStatus Status,
    string Code,
    Guid ReservationId,
    Guid? TableId,
    DateTime CancellationDeadlineUtc,
    bool CanCancel,
    bool CancelledAfterDeadline,
    bool AwaitsEarlierGuestAnswer,
    string? Note = null,
    string? RoomName = null,
    DateTime? ApprovalExpiresAtUtc = null,
    DeclineReason? DeclinedBecause = null,
    bool PhoneUnverified = false,
    bool CanChange = false,
    Domain.Occupancy.CancellationCause? CancelledBecause = null,
    Reservations.PreOrderView? PreOrder = null,
    DateTime? PreOrderEditableUntilUtc = null,
    DateTime? GuestConfirmedOnWayAtUtc = null,
    bool OnYourWayAsked = false,
    int? GuestRunningLateMinutes = null,
    DateTime? StaffAskedOnMyWayAtUtc = null,
    DateTime? OnMyWayOpensAtUtc = null);

/// <summary>
/// The one answer every unanswerable manage link gets.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token must not be a lookup oracle.</b> An unknown token, an expired one and a valid one
/// whose booking has been purged are three different facts on the server and exactly one answer on
/// the wire - the same status, the same code and the same sentence, every time. Anything less lets
/// somebody with a list of candidate tokens learn which ones are real, and "real" is the only thing
/// standing between them and a stranger's booking.
/// </para>
/// <para>
/// This is why the message is a constant rather than interpolated. A sentence carrying the token,
/// or the reason, or the date it expired, is the oracle rebuilt in prose.
/// </para>
/// </remarks>
public static class ManageBookingFailure
{
    /// <summary>The only sentence a failed manage-link lookup ever produces.</summary>
    public const string Message =
        "This booking link is not valid. It may have expired, or the booking may no longer exist.";

    /// <summary>Builds the one refusal. Deliberately takes no arguments - see the type remarks.</summary>
    public static KeyNotFoundException Raise() => new(Message);
}

/// <summary>
/// Reading, cancelling and changing one booking with nothing but its manage link.
/// </summary>
/// <remarks>
/// A visitor who booked from the public page has no app, so no push reminder and no one-tap cancel
/// reach them. Without this, the product has built a no-show generator: the whole reservation story
/// rests on cancelling being easier than not turning up, and for a web booking this link is the
/// only way that is true.
/// </remarks>
public interface IPublicBookingService
{
    /// <summary>The booking behind a manage token.</summary>
    /// <exception cref="KeyNotFoundException">
    /// The token is unknown, expired, or its booking is gone - answered identically in all three
    /// cases. See <see cref="ManageBookingFailure"/>.
    /// </exception>
    Task<PublicBookingView> GetAsync(string manageToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the booking behind a manage token, honouring the branch's deadline rule.
    /// </summary>
    /// <remarks>
    /// Free before the deadline, allowed after it and recorded as late, never blocked. The rule and
    /// the writing are the reservation service's, not a second implementation.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">As <see cref="GetAsync"/>.</exception>
    Task<PublicBookingView> CancelAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the booking behind a manage token - another date, time, table or party size - and answers
    /// with it as the link now shows it.
    /// </summary>
    /// <remarks>
    /// The diner's move, by the reservation service's code: the same rules, the same status decided
    /// again, the same lock, reminders and idempotency. A retry of the same change answers with the
    /// booking as it stands. See <c>IReservationService.RescheduleByManageTokenAsync</c>.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">As <see cref="GetAsync"/>; or no such table.</exception>
    Task<PublicBookingView> RescheduleAsync(
        string manageToken,
        Reservations.RescheduleByManageTokenCommand command,
        CancellationToken cancellationToken = default);
}
