using System.ComponentModel.DataAnnotations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Implemented by every state-change body so one endpoint filter can enforce the idempotency key
/// instead of eight handlers each remembering to.
/// </summary>
public interface IClientCommandRequest
{
    /// <summary>The caller's own id for this command.</summary>
    Guid ClientCommandId { get; }
}

/// <summary>
/// Body of a state-change request that needs nothing beyond the table.
/// </summary>
/// <param name="ClientCommandId">
/// The caller's own id for this command. <b>Required.</b> The staff tablet queues changes while
/// the wifi is down and replays them on reconnect, so the same request arrives twice; this is
/// what makes the second arrival return the first one's result instead of acting again.
/// </param>
/// <param name="Reason">Optional free text for the audit log.</param>
/// <param name="Queued">
/// True when this came off the tablet's offline queue rather than from a live tap. A queued command
/// <b>must</b> carry <paramref name="ExpectedFromStatus"/>, because idempotency stops it being
/// applied twice and says nothing about it being out of date.
/// </param>
/// <param name="ExpectedFromStatus">The status the table had when the waiter tapped.</param>
/// <param name="ExpectedRowVersion">
/// The table's <c>rowVersion</c> from the floor read model at the moment of the tap, sent back
/// untouched. <b>Status is not a version:</b> a table that went Free to Occupied and back to Free
/// while the command sat in the queue passes a status check, and the command lands on a sitting that
/// has already ended. When both are supplied both must match, and the refusal says which failed.
/// </param>
public sealed record TableStateRequest(
    [Required] Guid ClientCommandId,
    string? Reason = null,
    bool Queued = false,
    TableStatus? ExpectedFromStatus = null,
    string? ExpectedRowVersion = null) : IClientCommandRequest;

/// <summary>Body for seating a party with no booking.</summary>
/// <param name="PartySize">How many people sat down.</param>
/// <param name="ClientCommandId">See <see cref="TableStateRequest.ClientCommandId"/>.</param>
/// <param name="Reason">Optional free text for the audit log.</param>
/// <param name="Queued">
/// True when this came off the tablet's offline queue rather than from a live tap. A queued command
/// <b>must</b> carry <paramref name="ExpectedFromStatus"/>, because idempotency stops it being
/// applied twice and says nothing about it being out of date.
/// </param>
/// <param name="ExpectedFromStatus">The status the table had when the waiter tapped.</param>
/// <param name="ExpectedRowVersion">
/// The table's <c>rowVersion</c> from the floor read model at the moment of the tap, sent back
/// untouched. <b>Status is not a version:</b> a table that went Free to Occupied and back to Free
/// while the command sat in the queue passes a status check, and the command lands on a sitting that
/// has already ended. When both are supplied both must match, and the refusal says which failed.
/// </param>
public sealed record SeatWalkInRequest(
    [Range(1, 100)] int PartySize,
    [Required] Guid ClientCommandId,
    string? Reason = null,
    bool Queued = false,
    TableStatus? ExpectedFromStatus = null,
    string? ExpectedRowVersion = null) : IClientCommandRequest;

/// <summary>Body for seating a booked party.</summary>
/// <param name="ReservationId">The booking being honoured.</param>
/// <param name="ClientCommandId">See <see cref="TableStateRequest.ClientCommandId"/>.</param>
/// <param name="PartySize">Overrides the booked size when the number who turned up differs.</param>
/// <param name="Reason">Optional free text for the audit log.</param>
/// <param name="Queued">
/// True when this came off the tablet's offline queue rather than from a live tap. A queued command
/// <b>must</b> carry <paramref name="ExpectedFromStatus"/>, because idempotency stops it being
/// applied twice and says nothing about it being out of date.
/// </param>
/// <param name="ExpectedFromStatus">The status the table had when the waiter tapped.</param>
/// <param name="ExpectedRowVersion">
/// The table's <c>rowVersion</c> from the floor read model at the moment of the tap, sent back
/// untouched. <b>Status is not a version:</b> a table that went Free to Occupied and back to Free
/// while the command sat in the queue passes a status check, and the command lands on a sitting that
/// has already ended. When both are supplied both must match, and the refusal says which failed.
/// </param>
public sealed record SeatReservationRequest(
    [Required] Guid ReservationId,
    [Required] Guid ClientCommandId,
    [Range(1, 100)] int? PartySize = null,
    string? Reason = null,
    bool Queued = false,
    TableStatus? ExpectedFromStatus = null,
    string? ExpectedRowVersion = null) : IClientCommandRequest;

/// <summary>Body for seating the party a hold was placed for.</summary>
/// <param name="PartySize">How many people sat down.</param>
/// <param name="ClientCommandId">See <see cref="TableStateRequest.ClientCommandId"/>.</param>
/// <param name="ReservationId">Set when the hold was for a late booking, which is the usual case.</param>
/// <param name="Reason">Optional free text for the audit log.</param>
/// <param name="Queued">
/// True when this came off the tablet's offline queue rather than from a live tap. A queued command
/// <b>must</b> carry <paramref name="ExpectedFromStatus"/>, because idempotency stops it being
/// applied twice and says nothing about it being out of date.
/// </param>
/// <param name="ExpectedFromStatus">The status the table had when the waiter tapped.</param>
/// <param name="ExpectedRowVersion">
/// The table's <c>rowVersion</c> from the floor read model at the moment of the tap, sent back
/// untouched. <b>Status is not a version:</b> a table that went Free to Occupied and back to Free
/// while the command sat in the queue passes a status check, and the command lands on a sitting that
/// has already ended. When both are supplied both must match, and the refusal says which failed.
/// </param>
public sealed record SeatHeldPartyRequest(
    [Range(1, 100)] int PartySize,
    [Required] Guid ClientCommandId,
    Guid? ReservationId = null,
    string? Reason = null,
    bool Queued = false,
    TableStatus? ExpectedFromStatus = null,
    string? ExpectedRowVersion = null) : IClientCommandRequest;
