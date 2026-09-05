namespace Yalla.Application.Reservations;

/// <summary>
/// Why a booking was let go, and whether it counts against the diner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two buttons on the tablet, not one.</b> "Release, marked no-show" and "release, they let us
/// know". If there is only one, waiters tap it for both cases - they are busy and the outcomes look
/// the same from the floor - and the no-show threshold ends up punishing the people who phoned ahead
/// to say they could not come.
/// </para>
/// <para>
/// Yerevan is a small market. Being unfair to a regular by accident is a cost the product cannot
/// carry, and the fix is a distinction the person releasing the table already knows and takes one
/// extra tap to record.
/// </para>
/// </remarks>
public enum ReleaseOutcome
{
    /// <summary>Nobody came and nobody called. Counts toward the rolling no-show threshold.</summary>
    NoShow = 1,

    /// <summary>
    /// The venue let it go: they phoned to cancel, or the table went out of service. <b>Does not
    /// count</b> against the diner.
    /// </summary>
    CancelledByVenue = 2,
}

/// <summary>The diner tapping "we are five minutes away" from the late nudge.</summary>
/// <param name="ReservationId">Their booking.</param>
/// <param name="ClientCommandId">
/// Idempotency. Tapping the notification twice must not be two extensions - and since there is only
/// ever one extension, the second tap would otherwise be an error rather than a no-op.
/// </param>
public sealed record ExtendHoldCommand(Guid ReservationId, Guid ClientCommandId);

/// <summary>What extending the hold did.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="HoldExpiresAtUtc">Until when the table is now held.</param>
/// <param name="ExtensionMinutes">How long was added, from the branch's policy.</param>
/// <param name="ExtensionsRemaining">
/// Always zero after a successful extension. Stated so the app can grey the button out rather than
/// letting the diner tap it and be told no.
/// </param>
/// <param name="WasReplay">True when this command had already been applied.</param>
public sealed record ExtendHoldResult(
    Guid ReservationId,
    DateTime HoldExpiresAtUtc,
    int ExtensionMinutes,
    int ExtensionsRemaining,
    bool WasReplay);

/// <summary>A waiter letting a booking go.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="Outcome">Which of the two this is. The diner's next booking depends on it.</param>
/// <param name="ClientCommandId">
/// Idempotency, as everywhere else. Releasing the same booking twice from a tablet that lost its
/// connection must not count two no-shows against a diner.
/// </param>
/// <param name="Reason">Optional free text for the audit row.</param>
public sealed record ReleaseReservationCommand(
    Guid ReservationId,
    ReleaseOutcome Outcome,
    Guid ClientCommandId,
    string? Reason = null);

/// <summary>What releasing did.</summary>
/// <param name="Reservation">The booking in its new state.</param>
/// <param name="Outcome">Which outcome was recorded.</param>
/// <param name="TableFreed">
/// True when the table was held for this booking and has been freed. False when somebody else is
/// sitting there - the release still stands, and the floor is left telling the truth.
/// </param>
/// <param name="TableId">The table this booking was for.</param>
/// <param name="TableLabel">Its label, for the confirmation the waiter sees.</param>
/// <param name="TableStatus">What the table is now.</param>
/// <param name="CountsTowardNoShowThreshold">
/// Whether this one goes on the diner's record. Stated in the response so a client can say so
/// rather than the waiter having to remember which button does what.
/// </param>
/// <param name="WasReplay">True when the booking had already been released and this is that answer.</param>
public sealed record ReservationReleaseResult(
    ReservationView Reservation,
    ReleaseOutcome Outcome,
    bool TableFreed,
    Guid TableId,
    string TableLabel,
    Domain.Enums.TableStatus TableStatus,
    bool CountsTowardNoShowThreshold,
    bool WasReplay);
