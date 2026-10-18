using Yalla.Domain.Enums;

namespace Yalla.Application.Reservations;

/// <summary>
/// A diner booking one table for one slot.
/// </summary>
/// <remarks>
/// The date and time are <b>local to the branch</b>, exactly as the diner picked them off a
/// calendar. They are not instants and must not be sent as one: a client that converts to UTC
/// itself has to know the branch's zone and its history of clock changes, and the moment it gets
/// that wrong the booking lands an hour out with nothing in the request to reveal it.
/// </remarks>
/// <param name="BranchId">The branch being booked.</param>
/// <param name="TableId">The table the diner picked off the floor plan.</param>
/// <param name="LocalDate">The local calendar date at the branch.</param>
/// <param name="LocalTime">The local wall-clock start time at the branch.</param>
/// <param name="PartySize">How many are coming.</param>
/// <param name="GuestName">Who to ask for at the door.</param>
/// <param name="GuestPhone">How to reach them when they are late.</param>
/// <param name="ClientCommandId">
/// The caller's own id for this booking. <b>Required.</b> A mobile app on a flaky connection
/// retries, and this is what makes the retry return the original booking instead of taking a
/// second table for the same party.
/// </param>
/// <param name="StayHint">Advisory only. The interval comes from the branch's turn time.</param>
/// <param name="Channel">
/// Where the booking was made from. Self-reported by the client, and a reporting field only - no
/// rule reads it and no booking is refused because of it, so a client that lies costs one wrong
/// number in a report.
/// <para>
/// Somebody who books from the public branch page has no app and therefore no push channel, so the
/// reminder, the late nudge and one-tap cancel do not reach them. How often that happens is the
/// number that decides whether an SMS or Telegram channel is worth paying for - and there is no way
/// for the server to work it out on its own, because an app's HTTPS request and a browser's look
/// identical. See <c>docs/reports.md</c>.
/// </para>
/// </param>
/// <param name="Note">
/// A note to the venue - "window table", "a high chair". Trimmed, blank means none, at most 500
/// characters (K9).
/// </param>
/// <param name="AcceptsFreeByUtc">
/// The time the diner agreed to free the table by, when the table's next booking cuts a full sitting
/// short - availability's <c>mustFreeByUtc</c> for the table, sent back. Required exactly then, and it
/// must be that instant: anything else is refused naming <c>acceptsFreeByUtc</c>, with the time
/// required. Ignored when the table has no such limit.
/// </param>
public sealed record CreateReservationCommand(
    Guid BranchId,
    Guid TableId,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    int PartySize,
    string GuestName,
    string GuestPhone,
    Guid ClientCommandId,
    StayHint? StayHint = null,
    ReservationChannel Channel = ReservationChannel.Unknown,
    string? Note = null,
    DateTime? AcceptsFreeByUtc = null);

/// <summary>
/// A diner moving their own booking: another time, another table, another party size - the same
/// booking, with the same code.
/// </summary>
/// <remarks>
/// Judged exactly as a new booking for the same slot would be, by the same rules and under the same
/// table lock, except that the booking being moved never stands in its own way.
/// </remarks>
/// <param name="ReservationId">The booking to move. Must be the caller's own.</param>
/// <param name="TableId">
/// The table to move to - the one it is on, or another at the same branch. Required, as on
/// <see cref="CreateReservationCommand"/>: a booking is always for one table the diner picked.
/// </param>
/// <param name="LocalDate">The new local calendar date at the branch.</param>
/// <param name="LocalTime">The new local wall-clock start time at the branch.</param>
/// <param name="PartySize">How many are coming now.</param>
/// <param name="ClientCommandId">
/// The caller's own id for this change. <b>Required.</b> A retry with the same id answers with the
/// booking as it stands instead of moving it again.
/// </param>
/// <param name="AcceptsFreeByUtc">
/// As on <see cref="CreateReservationCommand"/>: the time to free the table by at the new slot, when
/// its next booking cuts a full sitting short. A booking that already promised exactly that time
/// need not send it again.
/// </param>
public sealed record RescheduleReservationCommand(
    Guid ReservationId,
    Guid TableId,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    int PartySize,
    Guid ClientCommandId,
    DateTime? AcceptsFreeByUtc = null);

/// <summary>
/// The earlier guest answering a later guest's request for their table: "will you have left by 19:40?".
/// </summary>
/// <param name="ReservationId">The earlier guest's own booking - the one whose table is asked for.</param>
/// <param name="RequestId">The request, from the booking's <c>incomingTableRequest</c>.</param>
/// <param name="Accept">Yes, they will have left by then; or no.</param>
/// <param name="ClientCommandId">
/// The caller's own id for this answer. A retry with the same id answers with the booking as it stands.
/// </param>
public sealed record AnswerTableRequestCommand(
    Guid ReservationId,
    Guid RequestId,
    bool Accept,
    Guid ClientCommandId);

/// <summary>A diner giving up a booking.</summary>
/// <param name="ReservationId">The booking to cancel.</param>
/// <param name="Reason">Optional free text, recorded on the booking.</param>
public sealed record CancelReservationCommand(Guid ReservationId, string? Reason = null);

/// <summary>Staff accepting or declining a booking that was waiting for approval.</summary>
/// <param name="ReservationId">The booking being decided.</param>
/// <param name="Reason">Optional free text, recorded when declining.</param>
public sealed record DecideReservationCommand(Guid ReservationId, string? Reason = null);

/// <summary>
/// What the availability screen is asking.
/// </summary>
/// <remarks>
/// <see cref="LocalDate"/> and <see cref="LocalTime"/> are optional so browsing works with no
/// input at all: omitted, they mean "now, at the branch", which is what somebody standing outside
/// the door wants to know.
/// </remarks>
/// <param name="BranchId">The branch being browsed.</param>
/// <param name="PartySize">How many are coming. Decides capacity and seat overhang.</param>
/// <param name="LocalDate">Local date at the branch. Defaults to today there.</param>
/// <param name="LocalTime">Local time at the branch. Defaults to now there.</param>
/// <param name="ExcludeReservationId">
/// A booking to leave out of the answer, as though it had never been made - the diner's own, when
/// they are changing it, so the table they already hold reads as free to them. Null leaves every
/// booking in.
/// </param>
public sealed record AvailabilityRequest(
    Guid BranchId,
    int PartySize,
    DateOnly? LocalDate = null,
    TimeOnly? LocalTime = null,
    Guid? ExcludeReservationId = null);

/// <summary>
/// What the time picker is asking: which start times on one day have a table for this party.
/// </summary>
/// <param name="BranchId">The branch being browsed.</param>
/// <param name="LocalDate">The local date at the branch.</param>
/// <param name="PartySize">How many are coming.</param>
/// <param name="ExcludeReservationId">
/// A booking to leave out, as on <see cref="AvailabilityRequest"/> - the diner's own while they
/// change it.
/// </param>
public sealed record DaySlotsRequest(
    Guid BranchId,
    DateOnly LocalDate,
    int PartySize,
    Guid? ExcludeReservationId = null);

/// <summary>
/// How long a booking will wait for the table's lock before giving up.
/// </summary>
/// <remarks>
/// An infrastructure setting, not a venue one, which is why it is here and not on
/// <c>ReservationPolicy</c>: an owner has no opinion about lock contention. Five seconds is far
/// longer than the locked span takes - a range check and one insert - so hitting it means
/// something is genuinely wrong rather than merely busy.
/// </remarks>
public sealed class BookingLockOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "BookingLock";

    /// <summary>Milliseconds to wait for the table lock before answering with a retryable error.</summary>
    public int LockTimeoutMilliseconds { get; set; } = 5_000;
}
