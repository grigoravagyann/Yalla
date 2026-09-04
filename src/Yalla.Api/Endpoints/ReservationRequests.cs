using System.ComponentModel.DataAnnotations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

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
public sealed record CreateReservationRequest(
    [Required] Guid BranchId,
    [Required] Guid TableId,
    [Required] DateOnly Date,
    [Required] TimeOnly Time,
    [Range(1, 100)] int PartySize,
    [Required][MaxLength(200)] string GuestName,
    [Required][MaxLength(32)] string GuestPhone,
    [Required] Guid ClientCommandId,
    StayHint? StayHint = null) : IClientCommandRequest;

/// <summary>Body for a diner cancelling their own booking.</summary>
/// <param name="Reason">Optional free text, recorded on the booking.</param>
public sealed record CancelReservationRequest([MaxLength(500)] string? Reason = null);

/// <summary>Body for staff accepting or declining a booking that is waiting for approval.</summary>
/// <param name="Reason">Optional free text, recorded when declining.</param>
public sealed record DecideReservationRequest([MaxLength(500)] string? Reason = null);
