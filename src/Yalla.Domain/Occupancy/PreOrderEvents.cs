using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Occupancy;

// What happens to a pre-order, and to a booking's "are you on your way?", as their handlers hear it
// (docs/domain-events.md, pre-orders). Each carries the aggregate as the save sees it, and what a handler cannot read back
// afterwards: the sitting and tab as they were when it was sent, where a held ticket was on the rail.

/// <summary>The guest saved the pre-order - the first time, or an edit - from the app or the booking's link.</summary>
/// <param name="PreOrder">The pre-order, its lines as saved.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderSaved(PreOrder PreOrder, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The guest cleared the pre-order: the row goes, in the same save.</summary>
/// <param name="PreOrder">The pre-order being removed.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderCleared(PreOrder PreOrder, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter sent the pre-order to the kitchen from the rail: a held ticket, or straight onto the party's tab.</summary>
/// <param name="PreOrder">The pre-order, now sent.</param>
/// <param name="ByStaffId">Who sent it.</param>
/// <param name="SittingId">The party's open sitting when it was sent, or null when they were not seated.</param>
/// <param name="TabId">That sitting's tab when it was sent - before the send opened one for it, when it did.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderSent(
    PreOrder PreOrder, Guid ByStaffId, Guid? SittingId, Guid? TabId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter marked the party "not coming / no answer" from the rail. Nothing is sent; sending stays possible.</summary>
/// <param name="PreOrder">The pre-order.</param>
/// <param name="ByStaffId">Who marked it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PartyMarkedNotComing(PreOrder PreOrder, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The booking moved and the pre-order went with it - a waiter's "not coming" for the old time dropped.</summary>
/// <param name="PreOrder">The pre-order.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderFollowedMove(PreOrder PreOrder, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The kitchen moved a held ticket - sent before the party sat - along its rail.</summary>
/// <param name="PreOrder">The pre-order, its ticket at <paramref name="To"/>.</param>
/// <param name="From">Where the ticket was.</param>
/// <param name="To">Where it is now.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record HeldTicketMoved(
    PreOrder PreOrder, TabOrderStatus From, TabOrderStatus To, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The booking ended before its party sat, and the held ticket the kitchen was cooking from was voided.</summary>
/// <param name="PreOrder">The pre-order, its ticket voided.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record HeldTicketVoided(PreOrder PreOrder, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The sent pre-order arrived on the party's tab - their sitting began with the ticket held, or it was sent once they sat:
/// it is now the order with its own id on that tab, at the place on the rail the kitchen had reached.
/// </summary>
/// <remarks>
/// The order itself raises nothing for arriving (<see cref="TabOrder.ArriveFromHeldTicket"/>): its moves were the
/// pre-order's and were told then. This is what the tab's stream hears instead.
/// </remarks>
/// <param name="PreOrder">The pre-order, now attached.</param>
/// <param name="Order">The order it became, its lines and status built.</param>
/// <param name="OnNewTab">The tab was opened for it in the same save, so its stream starts with this order.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderArrivedOnTab(PreOrder PreOrder, TabOrder Order, bool OnNewTab, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The branch's pre-order settings were saved: when the rail's cards appear, and which dishes may be pre-ordered.</summary>
/// <param name="Branch">The branch, its settings as saved.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record PreOrderSettingsSaved(Branch Branch, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The guest said they are on their way - from the app or the link, or over the phone to a member of staff who recorded
/// it for them (2026-10-31).
/// </summary>
/// <param name="Reservation">The booking, its answer recorded.</param>
/// <param name="OccurredAtUtc">When.</param>
/// <param name="RecordedByStaffId">
/// The member of staff who recorded the answer for the guest, or null when the guest answered for themselves - what the
/// History attributes the line to.
/// </param>
public sealed record GuestOnTheirWay(Reservation Reservation, DateTime OccurredAtUtc, Guid? RecordedByStaffId = null) : IDomainEvent;

/// <summary>The guest said they are running late by <paramref name="Minutes"/>.</summary>
/// <param name="Reservation">The booking, its answer recorded.</param>
/// <param name="Minutes">10, 15 or 30.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record GuestRunningLate(Reservation Reservation, int Minutes, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Somebody at the venue asked "are you coming?" from the tablet. The message in the thread and the guest's notice are
/// the thread's own event (<c>VenueAskedComing</c>); this is the booking's stamp.
/// </summary>
/// <param name="Reservation">The booking, the ask recorded.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record OnMyWayAskRecorded(Reservation Reservation, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The booking moved to another start, and what the guest and the venue had said about the old one went.</summary>
/// <param name="Reservation">The booking, at its new start.</param>
/// <param name="OccurredAtUtc">When the move was recorded.</param>
public sealed record OnMyWayAnswersForgotten(Reservation Reservation, DateTime OccurredAtUtc) : IDomainEvent;
