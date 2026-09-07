using System.ComponentModel.DataAnnotations;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>POST /api/reservations/{id}/extend-hold</c>.</summary>
/// <param name="ClientCommandId">
/// Idempotency. The late nudge is a notification the diner can tap twice, and the second tap must be
/// a no-op rather than an error about an extension already used.
/// </param>
public sealed record ExtendHoldRequest([Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/reservations/{id}/no-show</c>.</summary>
/// <param name="ClientCommandId">Idempotency - see <see cref="ExtendHoldRequest"/>.</param>
/// <param name="Reason">Optional free text for the audit row.</param>
public sealed record MarkNoShowRequest(
    [Required] Guid ClientCommandId,
    string? Reason = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/reservations/{id}/release</c>.</summary>
/// <param name="Outcome">
/// <b>1 NoShow</b> - nobody came and nobody called; counts toward the diner's rolling no-show
/// threshold. <b>2 CancelledByVenue</b> - they phoned, or the table went out of service; does
/// <b>not</b> count.
///
/// Two buttons on the tablet, never one. A single "release" gets tapped for both cases by a busy
/// waiter, and the threshold then punishes the diners who bothered to ring ahead.
/// </param>
/// <param name="ClientCommandId">
/// Idempotency. Releasing twice from a tablet that lost its connection must not put two no-shows on
/// a diner's record.
/// </param>
/// <param name="Reason">Optional free text for the audit row.</param>
public sealed record ReleaseReservationRequest(
    // Nullable so that [Required] can actually fire. A non-nullable enum binds its default when the
    // field is absent, and ReleaseOutcome has no zero member - so an omitted outcome used to arrive
    // as 0, miss the NoShow branch and be written as CancelledByVenue, silently, with a 200. That is
    // the outcome that does NOT count against the diner, so a real no-show quietly stopped counting.
    [Required] ReleaseOutcome? Outcome,
    [Required] Guid ClientCommandId,
    string? Reason = null) : IClientCommandRequest;

/// <summary>
/// Body for booking a table.
/// </summary>
/// <remarks>
/// <see cref="Date"/> and <see cref="Time"/> are <b>local to the branch</b>, exactly as the diner
/// picked them. They are deliberately not an instant: a client that converts to UTC itself has to
/// know the branch's zone and its history of clock changes, and the first time it gets that wrong
/// the booking lands an hour out with nothing in the request to show it.
/// </remarks>
/// <param name="BranchId">The branch being booked.</param>
/// <param name="TableId">The table the diner picked off the floor plan.</param>
/// <param name="Date">Local calendar date at the branch, e.g. <c>2026-09-12</c>.</param>
/// <param name="Time">Local wall-clock start at the branch, e.g. <c>19:30</c>.</param>
/// <param name="PartySize">How many are coming.</param>
/// <param name="GuestName">Who to ask for at the door.</param>
/// <param name="GuestPhone">How to reach them when they are late.</param>
/// <param name="ClientCommandId">
/// The caller's own id for this booking. <b>Required.</b> A phone on a patchy connection retries,
/// and this is what makes the retry return the original booking instead of taking a second table
/// for the same party.
/// </param>
/// <param name="StayHint">Advisory only. The interval comes from the branch's turn time.</param>
/// <param name="Channel">
/// Where the booking was made from, so the reports can count how many arrive from the public page.
/// Self-reported and never authorised on. Send <c>Web</c> from the browser and <c>App</c> from the
/// diner app; a booking a waiter takes over the phone is <c>Staff</c>. See <c>docs/reports.md</c>.
/// </param>
public sealed record CreateReservationRequest(
    [Required] Guid BranchId,
    [Required] Guid TableId,
    [Required] DateOnly Date,
    [Required] TimeOnly Time,
    [Range(1, 100)] int PartySize,
    [Required][MaxLength(200)] string GuestName,
    [Required][MaxLength(32)] string GuestPhone,
    [Required] Guid ClientCommandId,
    StayHint? StayHint = null,
    ReservationChannel Channel = ReservationChannel.Unknown) : IClientCommandRequest;

/// <summary>Body for a diner cancelling their own booking.</summary>
/// <param name="Reason">Optional free text, recorded on the booking.</param>
public sealed record CancelReservationRequest([MaxLength(500)] string? Reason = null);

/// <summary>Body for staff accepting or declining a booking that is waiting for approval.</summary>
/// <param name="Reason">Optional free text, recorded when declining.</param>
public sealed record DecideReservationRequest([MaxLength(500)] string? Reason = null);
