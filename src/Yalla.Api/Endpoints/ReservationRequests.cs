using System.ComponentModel.DataAnnotations;
using Yalla.Api.Filters;
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

/// <summary>Body of <c>POST /api/reservations/{id}/undo-no-show</c>.</summary>
/// <param name="ClientCommandId">The tap's id.</param>
/// <param name="Reason">Why it was a mistake - kept on the booking.</param>
public sealed record UndoNoShowRequest(
    [Required] Guid ClientCommandId,
    [MaxLength(500)] string? Reason = null) : IClientCommandRequest;

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
/// <param name="TableId">
/// The table the diner picked off the room photos. Required at a branch where diners choose a table, refused as
/// <c>validation-failed</c> naming <c>tableId</c> without one; left out at a branch that books by time only
/// (<c>bookingMode</c> 2), where the venue chooses the table when the party arrives and naming one is refused the
/// same way.
/// </param>
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
/// <b>Deprecated and ignored</b> since 2026-10-27: accepted so older clients keep working, and never read. The route
/// decides the channel - <c>POST /api/reservations</c> is the diner app's (<c>App</c>: app bookings allowed by the
/// platform, online bookings on and the policy saved, K9), and the public page books through
/// <c>POST /api/public/branches/{branchId}/reservations</c> (<c>Web</c>). See <c>docs/branch-features.md</c>.
/// </param>
/// <param name="Note">
/// A note to the venue - "window table", "a high chair". Optional; trimmed, blank means none, at
/// most 500 characters, refused as <c>validation-failed</c> naming <c>note</c> with bound <c>max</c>.
/// </param>
/// <param name="AcceptsFreeByUtc">
/// "I'll free the table by 21:25": the table's <c>mustFreeByUtc</c> from availability, sent back once the
/// diner has agreed to it. Required exactly when the table's next booking cuts a full sitting short,
/// and then it must be that instant - otherwise <c>422 reservation-free-by-required</c> naming this
/// field, with <c>context.requiredFreeByUtc</c>. Ignored for a table with no such limit.
/// </param>
public sealed record CreateReservationRequest(
    // NotEmpty as well as Required: a missing Guid binds Guid.Empty rather than null, so Required
    // alone let it reach the lookup, which answered "Branch 00000000-0000-0000-0000-000000000000
    // was not found" - a sentinel quoted at a diner, about a branch nobody asked for. The wire
    // contract is unchanged; only the refusal is.
    [Required][NotEmpty] Guid BranchId,
    [NotEmpty] Guid? TableId,

    // Nullable, unlike the two above, because there is no spare value to test for. A missing
    // DateOnly binds 0001-01-01 and a missing TimeOnly binds midnight - and midnight is a booking
    // time somebody could genuinely mean, so nothing about the bound value can distinguish absent
    // from meant. Omitting either used to produce a confident wrong diagnosis: no date answered
    // "that table can only be booked 30 minutes ahead" because year 1 is in the past, and no time
    // answered "the branch is not open for a 00:00-02:00 sitting". Both sent a client debugging
    // lead times and opening hours over a field it had simply left out.
    [Required] DateOnly? Date,
    [Required] TimeOnly? Time,
    [Range(1, 100)] int PartySize,
    [Required][MaxLength(200)] string GuestName,
    [Required][MaxLength(32)] string GuestPhone,
    [Required] Guid ClientCommandId,
    StayHint? StayHint = null,

    // Deprecated and ignored (2026-10-27): kept so older clients' bodies still bind; the route decides the channel.
    // [Obsolete] is what marks it deprecated in the OpenAPI document.
    [property: Obsolete("Ignored: POST /api/reservations books as the app; the public page has its own route.")]
    ReservationChannel Channel = ReservationChannel.Unknown,

    // No [MaxLength] here on purpose: the validation filter reports every attribute as bound
    // `required`, and the contract says `max`. The service trims it and refuses it naming `note`.
    string? Note = null,

    // Required only when the table's next booking cuts a full sitting short, and then it must be that
    // instant - which only the service, under the table's lock, can know. So no attribute: the refusal
    // is the service's 422 `reservation-free-by-required`, naming this field.
    DateTime? AcceptsFreeByUtc = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/public/branches/{branchId}/reservations</c>: the public page's booking.</summary>
/// <remarks>
/// <see cref="CreateReservationRequest"/> without the branch, which the route names, and without a channel, which the
/// route decides: <c>Web</c>. <see cref="Date"/> and <see cref="Time"/> are local to the branch.
/// </remarks>
/// <param name="TableId">
/// The table the diner picked off the room photos; left out at a branch that books by time only - exactly as on
/// <see cref="CreateReservationRequest"/>.
/// </param>
/// <param name="Date">Local calendar date at the branch, e.g. <c>2026-09-12</c>.</param>
/// <param name="Time">Local wall-clock start at the branch, e.g. <c>19:30</c>.</param>
/// <param name="PartySize">How many are coming.</param>
/// <param name="GuestName">Who to ask for at the door.</param>
/// <param name="GuestPhone">How to reach them when they are late.</param>
/// <param name="ClientCommandId">The caller's own id for this booking. <b>Required</b>: a retry answers the first booking.</param>
/// <param name="StayHint">Advisory only. The interval comes from the branch's turn time.</param>
/// <param name="Note">
/// A note to the venue. Optional; trimmed, blank means none, at most 500 characters, refused as
/// <c>validation-failed</c> naming <c>note</c> with bound <c>max</c>.
/// </param>
/// <param name="AcceptsFreeByUtc">
/// "I'll free the table by 21:25": the table's <c>mustFreeByUtc</c> from availability, once the diner agreed to it -
/// exactly as on <see cref="CreateReservationRequest"/>.
/// </param>
public sealed record CreatePublicReservationRequest(
    [NotEmpty] Guid? TableId,
    [Required] DateOnly? Date,
    [Required] TimeOnly? Time,
    [Range(1, 100)] int PartySize,
    [Required][MaxLength(200)] string GuestName,
    [Required][MaxLength(32)] string GuestPhone,
    [Required] Guid ClientCommandId,
    StayHint? StayHint = null,
    string? Note = null,
    DateTime? AcceptsFreeByUtc = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/reservations/{reservationId}/reschedule</c>.</summary>
/// <remarks>
/// The whole new slot, as a new booking would state it - date, time, party size and table - rather
/// than only what changed. A client that sent a time alone would leave the server guessing whether
/// the old table still fits the old party at the new hour, and the answer the diner was shown is the
/// one to judge.
/// </remarks>
/// <param name="Date">The new local calendar date at the branch, e.g. <c>2026-09-12</c>.</param>
/// <param name="Time">The new local wall-clock start at the branch, e.g. <c>19:30</c>.</param>
/// <param name="PartySize">How many are coming now.</param>
/// <param name="DiningTableId">
/// The table to move to: the one the booking is on, or another at the same branch, picked off the
/// room photos as for a new booking. <b>Required</b>, as <c>tableId</c> is on a new booking - a
/// booking is always for one table the diner chose.
/// </param>
/// <param name="ClientCommandId">
/// The caller's own id for this change. <b>Required.</b> A retry with the same id answers with the
/// booking as it stands instead of moving it again; a fresh id for every change.
/// </param>
/// <param name="AcceptsFreeByUtc">
/// As on a new booking: the new slot's <c>mustFreeByUtc</c>, when its next booking cuts a full sitting
/// short. A booking that already promised exactly that time for the same start need not send it.
/// </param>
public sealed record RescheduleReservationRequest(
    // Nullable for the same reason as on a new booking: a missing DateOnly binds year one and a
    // missing TimeOnly binds midnight, so only null lets [Required] tell absent from meant.
    [Required] DateOnly? Date,
    [Required] TimeOnly? Time,
    [Range(1, 100)] int PartySize,

    // Nullable, so an explicit null is refused naming the field rather than failing to deserialise;
    // NotEmpty for the all-zeros id, as on a new booking.
    [Required][NotEmpty] Guid? DiningTableId,
    [Required] Guid ClientCommandId,

    // As on a new booking: required exactly when the new slot's next booking cuts a full sitting short.
    DateTime? AcceptsFreeByUtc = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/reservations/{reservationId}/table-requests/{requestId}/answer</c>.</summary>
/// <param name="Accept">
/// <c>true</c>: yes, the diner will have left by the time asked - their booking's end is cut to it and
/// the other guest is confirmed. <c>false</c>: no - the other guest is declined. Nullable so an omitted
/// answer is refused naming the field rather than read as no.
/// </param>
/// <param name="ClientCommandId">
/// The caller's own id for this answer. <b>Required.</b> A retry with the same id answers with the
/// booking as it stands; the push action and the card may both send it.
/// </param>
public sealed record AnswerTableRequestRequest(
    [Required] bool? Accept,
    [Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>Query of <c>GET /api/branches/{branchId}/reservations</c>.</summary>
/// <param name="Status">
/// Which bookings to list, as the <c>ReservationStatus</c> number - <c>1</c> for PendingApproval.
/// Required: a list of every booking a branch has ever taken is not a thing anybody asked for.
/// Nullable so that <c>[Required]</c> can fire, and <c>[EnumDataType]</c> refuses a number that is
/// not a status, both as the house 422.
/// </param>
public sealed record ListBranchReservationsQuery(
    [Required][EnumDataType(typeof(ReservationStatus))] ReservationStatus? Status);

/// <summary>Body for a diner cancelling their own booking.</summary>
/// <param name="Reason">Optional free text, recorded on the booking.</param>
public sealed record CancelReservationRequest([MaxLength(500)] string? Reason = null);

/// <summary>Body for staff accepting or declining a booking that is waiting for approval.</summary>
/// <param name="Reason">Optional free text, recorded when declining.</param>
public sealed record DecideReservationRequest([MaxLength(500)] string? Reason = null);
