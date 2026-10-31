using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

// What happens to a booking, as its handlers hear it (docs/domain-events.md, bookings). Each carries the booking itself -
// a handler reads it as the save sees it - and, for a change, where it stood before: the handlers that put a booking on
// its table's change stream, buzz the floor for an approval or void a held pre-order judge "what changed" from that, as
// the change tracker used to.

/// <summary>A booking's slot and standing at one moment - what a change event says it was before.</summary>
/// <param name="Status">Its status.</param>
/// <param name="DiningTableId">Its table.</param>
/// <param name="StartUtc">Its start.</param>
/// <param name="EndUtc">Its end.</param>
/// <param name="PartySize">How many.</param>
/// <param name="LocalDate">The local date the guest saw.</param>
/// <param name="LocalStartTime">The local start time the guest saw.</param>
/// <param name="ApprovalTrigger">Why it waited, or waits.</param>
/// <param name="ApprovalExpiresAtUtc">Until when the venue could answer.</param>
public sealed record ReservationStanding(
    ReservationStatus Status,
    Guid? DiningTableId,
    DateTime StartUtc,
    DateTime EndUtc,
    int PartySize,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    ApprovalTrigger? ApprovalTrigger,
    DateTime? ApprovalExpiresAtUtc);

/// <summary>Something that happened to a booking.</summary>
public interface IReservationEvent : IDomainEvent
{
    /// <summary>The booking, as it now stands.</summary>
    Reservation Reservation { get; }

    /// <summary>Where it stood before; null for a booking just made.</summary>
    ReservationStanding? Before { get; }
}

/// <summary>Who said yes to a booking that was waiting.</summary>
public enum ReservationApprover
{
    /// <summary>A member of staff - the floor, or the console.</summary>
    Staff = 1,

    /// <summary>The guest before it on the table, in the app: they will have left in time.</summary>
    EarlierGuest = 2,

    /// <summary>Nobody: the question it waited on went away (the guest before moved or left), and nothing else needs a human.</summary>
    NoLongerAsked = 3,
}

/// <summary>Who ended a booking that was not declined.</summary>
public enum ReservationCanceller
{
    /// <summary>The guest - in the app, or from the booking's link.</summary>
    Guest = 1,

    /// <summary>The venue let it go.</summary>
    Venue = 2,

    /// <summary>The platform: its guest was blocked for no-shows.</summary>
    NoShowBlock = 3,
}

/// <summary>A diner made the booking - from the app, the public page or the staff channel.</summary>
/// <param name="Reservation">The booking, Confirmed or PendingApproval, its reason stored.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationMade(Reservation Reservation, DateTime OccurredAtUtc) : IReservationEvent
{
    public ReservationStanding? Before => null;
}

/// <summary>A waiting booking - or the waiting change to one - was said yes to.</summary>
/// <param name="Reservation">The booking, Confirmed; a change's old slot forgotten.</param>
/// <param name="Before">Waiting.</param>
/// <param name="By">Who said yes.</param>
/// <param name="AnsweredRequest">The request the guest before said yes to, for <see cref="ReservationApprover.EarlierGuest"/>.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationApproved(
    Reservation Reservation,
    ReservationStanding Before,
    ReservationApprover By,
    TableShareRequest? AnsweredRequest,
    DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>
/// A waiting booking - or the waiting change to one - was declined: by a member of staff, by the guest before it on the
/// table, by nobody answering in time, or crowded out by the guest before it moving.
/// </summary>
/// <param name="Reservation">
/// The booking: CancelledByVenue - or, for a change whose old slot was still free, Confirmed at that slot again.
/// </param>
/// <param name="Before">Waiting.</param>
/// <param name="Because">Whose no, or whose silence.</param>
/// <param name="Reason">Why, as somebody typed it; null when nobody did.</param>
/// <param name="WasChange">It was a change of a confirmed booking, told to the guest as one.</param>
/// <param name="SlotKept">The change was declined and the booking went back to the slot it had.</param>
/// <param name="RequestedDate">The local date it asked for.</param>
/// <param name="RequestedTime">The local start time it asked for.</param>
/// <param name="AnsweredRequest">The request the guest before said no to, when it was their answer.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationDeclined(
    Reservation Reservation,
    ReservationStanding Before,
    DeclineReason Because,
    string? Reason,
    bool WasChange,
    bool SlotKept,
    DateOnly RequestedDate,
    TimeOnly RequestedTime,
    TableShareRequest? AnsweredRequest,
    DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>A waiting booking is now waiting for another reason - and with that reason's deadline, or none.</summary>
/// <param name="Reservation">The booking, still PendingApproval.</param>
/// <param name="Before">Waiting for the reason it had.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationWaitingInstead(
    Reservation Reservation, ReservationStanding Before, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>The guest moved the booking - in the app, or from its link - and it stays the same booking.</summary>
/// <param name="Reservation">The booking at its new slot, its status decided again.</param>
/// <param name="Before">Where it was.</param>
/// <param name="ViaManageLink">Whoever holds the booking's link moved it, not the app.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationRescheduled(
    Reservation Reservation, ReservationStanding Before, bool ViaManageLink, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>
/// The guest before a later booking on the table said yes to its request - or the floor said yes for them: they will have
/// left by the time asked.
/// </summary>
/// <param name="Reservation">The earlier booking, its end cut to the promise when that is sooner.</param>
/// <param name="Before">Its end before.</param>
/// <param name="Request">The request said yes to.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationPromisedToFreeBy(
    Reservation Reservation, ReservationStanding Before, TableShareRequest Request, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>A booking that was not declined ended before its party sat: cancelled by the guest, the venue or a no-show block.</summary>
/// <param name="Reservation">The booking, CancelledByDiner or CancelledByVenue.</param>
/// <param name="Before">Pending or Confirmed.</param>
/// <param name="By">Who ended it.</param>
/// <param name="Reason">Why, as somebody typed it; null when nobody did.</param>
/// <param name="ViaManageLink">The guest cancelled from the booking's link, not the app.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationCancelled(
    Reservation Reservation,
    ReservationStanding Before,
    ReservationCanceller By,
    string? Reason,
    bool ViaManageLink,
    DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>Nobody came: the floor marked the booking a no-show.</summary>
/// <param name="Reservation">The booking, NoShow, whether it counts toward a block stored.</param>
/// <param name="Before">Confirmed.</param>
/// <param name="Reason">Why, as the floor typed it; null when nobody did.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationMarkedNoShow(
    Reservation Reservation, ReservationStanding Before, string? Reason, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>Staff withdrew a no-show they marked by mistake: the booking is confirmed again.</summary>
/// <param name="Reservation">The booking, Confirmed, the withdrawal recorded.</param>
/// <param name="Before">NoShow.</param>
/// <param name="Reason">Why, as they typed it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationNoShowWithdrawn(
    Reservation Reservation, ReservationStanding Before, string? Reason, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>"We are five minutes away": the guest's one extension of the hold on their table.</summary>
/// <param name="Reservation">The booking, its hold extended.</param>
/// <param name="Before">Its standing before (the hold is not on it).</param>
/// <param name="Minutes">By how much.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ReservationHoldExtended(
    Reservation Reservation, ReservationStanding Before, int Minutes, DateTime OccurredAtUtc) : IReservationEvent;

/// <summary>
/// A later booking asks the booking before it on the same table to free the table in time - put to the earlier guest in
/// the app, or to the floor to put to them.
/// </summary>
/// <param name="Request">The request, Pending.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableShareAsked(TableShareRequest Request, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A guest was blocked from booking for three no-shows in three days, and their bookings not yet started were cancelled.</summary>
/// <param name="Block">The block.</param>
/// <param name="CancelledBookings">How many bookings it cancelled.</param>
/// <param name="BlockedUntilLocalDate">The day they can book again, on the wall clock of the venue whose no-show made it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record BookingBlockImposed(
    BookingBlock Block, int CancelledBookings, DateOnly BlockedUntilLocalDate, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A guest's block was lifted early - a no-show withdrawn, or the platform. What it cancelled stays cancelled.</summary>
/// <param name="Block">The block, lifted, with why.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record BookingBlockLifted(BookingBlock Block, DateTime OccurredAtUtc) : IDomainEvent;
