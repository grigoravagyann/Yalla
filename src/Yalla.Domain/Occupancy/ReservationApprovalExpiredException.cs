using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// Staff tried to approve or decline a booking whose chance to be answered has gone: its answer
/// window ran out, it was already declined for nobody answering, or - for an approval - it has
/// already started.
/// </summary>
/// <remarks>
/// <para>
/// Its own refusal rather than the general "not pending" one, because the tablet has something
/// specific to say - <i>too late, the diner has been told and may book again</i> - and a waiter who
/// reads "conflicting state" taps Approve again.
/// </para>
/// <para>
/// Past the window the decision is the sweep's, so both answers are refused, not only yes: a "no"
/// recorded as the staff's a second after the deadline would tell the diner the venue turned them
/// down when what happened is that nobody looked. Still a <see cref="DomainStateException"/>, so
/// anything that handles a refused state handles this; the API maps it first, with its own code.
/// </para>
/// </remarks>
public sealed class ReservationApprovalExpiredException(
    Guid reservationId,
    string code,
    ReservationStatus status,
    DateTime startUtc,
    DateTime? approvalExpiresAtUtc,
    string message)
    : DomainStateException(message)
{
    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>The short code the diner quotes at the door.</summary>
    public string Code { get; } = code;

    /// <summary>Where it stands now - still pending past its window, or already declined.</summary>
    public ReservationStatus Status { get; } = status;

    /// <summary>When it starts.</summary>
    public DateTime StartUtc { get; } = startUtc;

    /// <summary>When the answer window closed, or null for a booking that never had one.</summary>
    public DateTime? ApprovalExpiresAtUtc { get; } = approvalExpiresAtUtc;
}
