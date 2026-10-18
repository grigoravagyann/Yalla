using Yalla.Domain.Enums;

namespace Yalla.Application.Reservations;

/// <summary>
/// One booking waiting for the floor to say yes or no, with what the floor needs to know about its
/// table to decide.
/// </summary>
/// <remarks>
/// <para>
/// What the staff tablet's <i>awaiting approval</i> panel reads. A short-notice booking is a promise
/// about a table somebody may have just sat down at, so the request comes with the table's evening
/// around it: who had it before (and their number, to call and ask how long they will be), who has
/// it next, and whether anybody is sitting there now.
/// </para>
/// <para>
/// <see cref="ApprovalExpiresAtUtc"/> and <see cref="Trigger"/> repeat what <see cref="Reservation"/>
/// carries, so a panel sorting and counting down reads them without unwrapping the booking.
/// </para>
/// </remarks>
public sealed record AwaitingApproval
{
    /// <summary>The booking, as every client sees it - code, start, party, table, room, guest.</summary>
    /// <remarks>
    /// <c>guestPhone</c> is the guest's number, for calling them - staff of the branch may see it. A
    /// platform admin reading the list gets it <b>empty</b>: the list is the venue's working view, not
    /// the platform's.
    /// </remarks>
    public required ReservationView Reservation { get; init; }

    /// <summary>
    /// When the chance to answer runs out - fifteen minutes at short notice, two hours for a booking
    /// waiting on the guest before it (<see cref="Trigger"/> 5) - or null for a booking with no deadline,
    /// which waits for the venue as long as it takes (a large party next week, a branch that approves
    /// every booking).
    /// </summary>
    public DateTime? ApprovalExpiresAtUtc { get; init; }

    /// <summary>Why it is waiting.</summary>
    public required ApprovalTrigger Trigger { get; init; }

    /// <summary>
    /// The booking before this one on the same table that day - still to come, sitting there, gone
    /// home or never turned up - or null when there is none. Cancelled bookings are not counted.
    /// </summary>
    public AwaitingPreviousBooking? PreviousBooking { get; init; }

    /// <summary>The next booking after this one on the same table that day, or null when there is none.</summary>
    public AwaitingNextBooking? NextBooking { get; init; }

    /// <summary>The party sitting at the table right now, or null when nobody is.</summary>
    public AwaitingCurrentSitting? CurrentSitting { get; init; }

    /// <summary>
    /// The time the guest of <see cref="PreviousBooking"/> is asked to have left by for this booking -
    /// its start less the clearing time - when saying yes means holding them to it; null when nobody
    /// before has to be asked.
    /// </summary>
    /// <remarks>
    /// "Call Anna: will they have left by 19:40?" Approving this booking records that promise on the
    /// previous booking - its end is cut to it - so it is the floor saying yes for them. Set for
    /// <see cref="Yalla.Domain.Enums.ApprovalTrigger.EarlierGuestConsent"/> bookings the floor decides
    /// (the earlier guest booked on the web or has no phone to push to) and for short-notice ones whose
    /// table is still within three hours of its previous booking.
    /// </remarks>
    public DateTime? EarlierGuestFreeByUtc { get; init; }
}

/// <summary>The booking before one awaiting approval, on its table, that day.</summary>
/// <param name="ReservationId">The earlier booking.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="LocalStartTime">When it starts, on the branch's wall clock.</param>
/// <param name="PartySize">How many it is for.</param>
/// <param name="GuestName">Who it is for.</param>
/// <param name="GuestPhone">
/// Their number, E.164, so the floor can call and ask how long they will be. Null for a platform
/// admin reading the list.
/// </param>
/// <param name="Status">
/// Where it stands: <c>Confirmed</c> (still to come, or late), <c>Seated</c> (at the table),
/// <c>Completed</c> (gone), <c>NoShow</c>, or <c>PendingApproval</c>.
/// </param>
/// <param name="PromisedFreeByUtc">
/// The time its guest already promised to have left by, or null - see <c>ReservationView.PromisedFreeByUtc</c>.
/// </param>
public sealed record AwaitingPreviousBooking(
    Guid ReservationId,
    DateTime StartUtc,
    TimeOnly LocalStartTime,
    int PartySize,
    string GuestName,
    string? GuestPhone,
    ReservationStatus Status,
    DateTime? PromisedFreeByUtc = null);

/// <summary>The booking after one awaiting approval, on its table, that day.</summary>
/// <param name="ReservationId">The later booking.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="LocalStartTime">When it starts, on the branch's wall clock.</param>
/// <param name="PartySize">How many it is for.</param>
/// <param name="PromisedFreeByUtc">The time its guest promised to have left by, or null.</param>
public sealed record AwaitingNextBooking(
    Guid ReservationId,
    DateTime StartUtc,
    TimeOnly LocalStartTime,
    int PartySize,
    DateTime? PromisedFreeByUtc = null);

/// <summary>The party at the table of a booking awaiting approval, right now.</summary>
/// <param name="SeatedAtUtc">Since when they have been sitting there.</param>
/// <param name="PartySize">
/// How many, when a waiter seated them or they came on a booking; null when a diner's scan opened the
/// table, which only guesses.
/// </param>
public sealed record AwaitingCurrentSitting(DateTime SeatedAtUtc, int? PartySize);
