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
    ReservationChannel Channel = ReservationChannel.Unknown);

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
public sealed record AvailabilityRequest(
    Guid BranchId,
    int PartySize,
    DateOnly? LocalDate = null,
    TimeOnly? LocalTime = null);

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
