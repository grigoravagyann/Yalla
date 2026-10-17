using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// The diner asked to change a booking that can no longer be changed: it is not pending or confirmed,
/// or it has already started.
/// </summary>
/// <remarks>
/// <para>
/// A conflict about the booking's state, not a refusal of the new slot, which is why it is checked
/// before any branch rule: a seated or cancelled booking answers this whatever time was asked for,
/// rather than "that table is booked" about a change that was never going to be made.
/// </para>
/// <para>
/// <b>Started</b> is the line because from the start the booking is the venue's to run - the table is
/// held, the late nudge fires, a waiter may release it - and moving it then would pull the table out
/// from under a party that may be walking in. Still a <see cref="DomainStateException"/>, so anything
/// that handles a refused state handles this; the API maps it first, with its own code.
/// </para>
/// </remarks>
public sealed class ReservationNotChangeableException(
    Guid reservationId,
    string code,
    ReservationStatus status,
    DateTime startUtc)
    : DomainStateException(
        status is ReservationStatus.Confirmed or ReservationStatus.PendingApproval
            ? $"Booking {code} has already started, so it can no longer be changed. Speak to the venue."
            : $"Booking {code} is {status} and can no longer be changed. Make a new booking instead.")
{
    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>Where it stands, so a client can tell "already started" from "cancelled".</summary>
    public ReservationStatus Status { get; } = status;

    /// <summary>When it starts - or started.</summary>
    public DateTime StartUtc { get; } = startUtc;
}
