using Yalla.Domain.Enums;

namespace Yalla.Application.Reservations;

/// <summary>
/// One booking as every client sees it.
/// </summary>
/// <remarks>
/// Carries both the UTC instants and the wall-clock values the diner was shown. The instants are
/// what the staff app sorts and compares; the wall-clock values are what the confirmation says,
/// and they are read from the row rather than recomputed so a change to the branch's zone setting
/// can never move an existing booking on somebody's screen.
/// </remarks>
public sealed record ReservationView
{
    public required Guid Id { get; init; }

    /// <summary>The short code the diner quotes at the door.</summary>
    public required string Code { get; init; }

    public required Guid BranchId { get; init; }

    public required string BranchName { get; init; }

    public required string TimeZoneId { get; init; }

    public required Guid TableId { get; init; }

    public required string TableLabel { get; init; }

    /// <summary>
    /// The room the table is in - its <c>FloorArea</c>, the room a diner picked it from - or absent for
    /// a table in no room.
    /// </summary>
    /// <remarks>
    /// Read live from the table, like <see cref="TableLabel"/>: a booking names a table, not a room, so
    /// a table a manager moves to another room takes its bookings with it - which is where the party
    /// will actually sit.
    /// </remarks>
    public Guid? RoomId { get; init; }

    /// <summary>The room's name - "Hall", "Terrace" - or absent with <see cref="RoomId"/>.</summary>
    public string? RoomName { get; init; }

    public required int PartySize { get; init; }

    public required DateTime StartUtc { get; init; }

    /// <summary>Start plus the branch's turn time, fixed when the booking was made.</summary>
    public required DateTime EndUtc { get; init; }

    public required DateOnly LocalDate { get; init; }

    public required TimeOnly LocalStartTime { get; init; }

    /// <summary>Wall-clock end, derived from the stored interval for display.</summary>
    public required TimeOnly LocalEndTime { get; init; }

    /// <summary>
    /// The time the guest promised to have left the table by, or absent.
    /// </summary>
    /// <remarks>
    /// Set when the diner booked a table whose next booking cut the sitting short - they accepted
    /// "I'll free the table by 21:25" (<c>acceptsFreeByUtc</c>) - or said yes when a later guest asked
    /// for their table. <see cref="EndUtc"/> is that time too when it is sooner. Show it on the booking
    /// ("please free the table by 21:25"); staff see it on the awaiting list and the floor.
    /// </remarks>
    public DateTime? PromisedFreeByUtc { get; init; }

    /// <summary>
    /// Somebody would like this booking's table after it, and this guest is asked whether they will have
    /// left in time - or absent. Only on the diner's own booking, only while the question is open.
    /// </summary>
    /// <remarks>
    /// "Someone would like your table at 19:45. Will you have left by 19:40?" - answer with
    /// <c>POST /api/reservations/{id}/table-requests/{requestId}/answer</c> before
    /// <c>expiresAtUtc</c>. The other guest stays anonymous.
    /// </remarks>
    public IncomingTableRequest? IncomingTableRequest { get; init; }

    public required ReservationStatus Status { get; init; }

    public required string GuestName { get; init; }

    public required string GuestPhone { get; init; }

    /// <summary>
    /// True when the booking was made without a proved phone number - a web visitor who did not verify,
    /// or an app account whose number was never proved. Staff show "number not verified" beside it.
    /// </summary>
    public bool PhoneUnverified { get; init; }

    /// <summary>
    /// The diner's note to the venue - "window table", "a high chair" - or absent (K9).
    /// </summary>
    public string? Note { get; init; }

    public DateTime? ConfirmedAtUtc { get; init; }

    public DateTime? CancelledAtUtc { get; init; }

    public string? CancellationReason { get; init; }

    /// <summary>
    /// Whether the cancellation came in past the branch's free-cancellation deadline. Recorded,
    /// never punished here.
    /// </summary>
    public bool CancelledAfterDeadline { get; init; }

    /// <summary>
    /// The instant past which cancelling is recorded as late: the start less the branch's
    /// cancellation deadline as it stands, which is what a cancellation made now is judged against.
    /// Cancelling after it is still allowed - recorded in <see cref="CancelledAfterDeadline"/>, never
    /// refused.
    /// </summary>
    /// <remarks>
    /// Already past for a booking made inside the window. The app promised free cancellation until
    /// the start, because nothing here said otherwise.
    /// </remarks>
    public required DateTime CancellationDeadlineUtc { get; init; }

    public required Guid ClientCommandId { get; init; }

    /// <summary>
    /// True when this booking already existed and was returned rather than created.
    /// </summary>
    /// <remarks>
    /// A retry over a dropped connection. Callers can treat it exactly like a fresh success - that
    /// is the point - but the flag is on the wire so a client can tell the difference when
    /// diagnosing one.
    /// </remarks>
    public bool WasReplay { get; init; }

    /// <summary>
    /// Why the booking is waiting for a human, when it is. Null for a confirmed booking.
    /// </summary>
    /// <remarks>
    /// Four different situations land in <c>PendingApproval</c> and a diner deserves to know
    /// which: the booking is at short notice and the floor has to check the table, the branch
    /// approves everything by hand, the party is over the branch's threshold, or the diner has run up
    /// no-shows. Only the last is about them. The reason stored when the booking went to approval -
    /// see <see cref="ApprovalTrigger"/> for which one wins when several apply.
    /// </remarks>
    public ApprovalTrigger? AwaitingApprovalBecause { get; init; }

    /// <summary>
    /// When the chance to answer runs out, for a booking waiting on a short-notice decision or on the
    /// guest before it on the table; null for every other booking, and once it is decided.
    /// </summary>
    /// <remarks>
    /// At short notice, fifteen minutes from the request. Waiting on the guest before
    /// (<see cref="AwaitingApprovalBecause"/> 5), two hours, never past that guest's start - whether they
    /// answer in the app (<see cref="AwaitsEarlierGuestAnswer"/>) or the floor calls them. Never past the
    /// booking's own start. Past it nobody can approve or decline the booking - both answer
    /// <c>409 reservation-approval-expired</c> - and the server declines it within moments, with
    /// <see cref="DeclinedBecause"/> <c>NoAnswer</c> (the venue did not answer) or
    /// <c>EarlierGuestNoAnswer</c> (the guest before did not, in the app) and an apology to the diner.
    /// Count down to this, never to a client-side clock.
    /// </remarks>
    public DateTime? ApprovalExpiresAtUtc { get; init; }

    /// <summary>
    /// True while the booking waits on the guest before it on the table to answer in the app - "will you
    /// have left by 19:40?" - and false otherwise, the floor's own questions included.
    /// </summary>
    /// <remarks>
    /// Both kinds of wait for the guest before (<see cref="AwaitingApprovalBecause"/> 5
    /// <c>EarlierGuestConsent</c>) carry a two-hour <see cref="ApprovalExpiresAtUtc"/>, so this is what tells
    /// them apart: while it is true the answer is that guest's alone, and the floor's approve and reject are
    /// <c>409 reservation-awaiting-earlier-guest</c> - show the countdown, not the buttons. False, the floor
    /// decides it as any booking waiting for it, calling the guest before.
    /// </remarks>
    public required bool AwaitsEarlierGuestAnswer { get; init; }

    /// <summary>
    /// Why a booking that was waiting was declined: a member of staff said no (1), nobody at the venue
    /// answered in time (2), the guest with the table before said they would not have left in time (3),
    /// or did not answer within the two hours (4). Null for every booking that was not declined - a venue
    /// letting an accepted booking go is <c>CancelledByVenue</c> without one.
    /// </summary>
    public DeclineReason? DeclinedBecause { get; init; }

    /// <summary>
    /// The plaintext manage token, <b>returned exactly once</b> - in the response to the request
    /// that created this booking - and null on every later read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the hash is stored, so the server cannot return this again even if a later endpoint
    /// wanted to. That is the point: it is a bearer capability, and a capability a read endpoint
    /// will re-issue is a capability anybody who can read the booking can steal.
    /// </para>
    /// <para>
    /// It is what the caller builds the manage link from, and it matters most for a booking made
    /// from the public page: that diner has no app, so this link is the only way they will ever
    /// cancel rather than simply not turn up.
    /// </para>
    /// </remarks>
    public string? ManageToken { get; init; }
}

/// <summary>
/// A later guest's request for a booking's table, as the booking's own guest is shown it.
/// </summary>
/// <param name="RequestId">The request, to answer it by.</param>
/// <param name="RequestedStartUtc">When the later guest would sit down - "someone would like your table at 19:45".</param>
/// <param name="FreeByUtc">The time this guest is asked to have left by - "will you have left by 19:40?".</param>
/// <param name="ExpiresAtUtc">The deadline to answer by. Past it the request lapses and this booking is unchanged.</param>
/// <param name="RequestedLocalTime"><paramref name="RequestedStartUtc"/> on the branch's wall clock.</param>
/// <param name="FreeByLocalTime"><paramref name="FreeByUtc"/> on the branch's wall clock.</param>
public sealed record IncomingTableRequest(
    Guid RequestId,
    DateTime RequestedStartUtc,
    DateTime FreeByUtc,
    DateTime ExpiresAtUtc,
    TimeOnly RequestedLocalTime,
    TimeOnly FreeByLocalTime);

/// <summary>The branch booking list the console's approval panel reads.</summary>
public static class BranchReservationList
{
    /// <summary>
    /// The most rows one call returns. There is no paging: the list is a queue a manager works
    /// through, and a branch with two hundred bookings waiting has a problem no page two solves.
    /// The earliest two hundred come back, so what is cut is what is furthest away.
    /// </summary>
    public const int MaxRows = 200;
}

/// <summary>A diner's own bookings, split the way the app shows them.</summary>
/// <param name="Upcoming">Bookings that have not finished yet, soonest first.</param>
/// <param name="Past">Everything else - completed, cancelled, missed - most recent first.</param>
public sealed record MyReservations(
    IReadOnlyList<ReservationView> Upcoming,
    IReadOnlyList<ReservationView> Past);
