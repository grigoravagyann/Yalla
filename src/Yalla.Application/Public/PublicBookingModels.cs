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
/// harmed by: no diner id, no phone number, no other bookings, no floor state, no table id, no
/// venue internals.
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
/// <param name="BranchAddress">Where to go.</param>
/// <param name="TimeZoneId">
/// The branch's IANA zone. Every time here is wall-clock in it, and a diner reading this on a
/// phone set to another zone must not be shown a converted time.
/// </param>
/// <param name="TableLabel">The table, as printed on the floor.</param>
/// <param name="LocalDate">The booked date, as the diner reads it off the confirmation.</param>
/// <param name="LocalStartTime">The booked wall-clock start.</param>
/// <param name="PartySize">How many people.</param>
/// <param name="Status">Where the booking stands now.</param>
/// <param name="Code">The short code quoted at the door.</param>
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
public sealed record PublicBookingView(
    string VenueName,
    string BranchName,
    string BranchAddress,
    string TimeZoneId,
    string TableLabel,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    int PartySize,
    ReservationStatus Status,
    string Code,
    DateTime CancellationDeadlineUtc,
    bool CanCancel,
    bool CancelledAfterDeadline);

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
/// Reading and cancelling one booking with nothing but its manage link.
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
}
