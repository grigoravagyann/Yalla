using Yalla.Domain.Common;

namespace Yalla.Domain.Takeaway;

// What happens to a takeaway order, as its handlers hear it (docs/domain-events.md). Each carries the order itself: a
// handler reads it as the save sees it - its lines, its code, where it now stands and the times it was stamped with.

/// <summary>
/// A guest sent the order from the app. At a branch that accepts automatically it was accepted in the same breath -
/// ready when the guest asked for it, nobody at the venue answering - and the event says so: the guest's own tap already
/// answers that it is accepted, and the floor has nothing to accept.
/// </summary>
/// <param name="Order">The order, its lines built; <see cref="TakeawayStatus.Sent"/>, or Accepted when accepted on placing.</param>
/// <param name="AcceptedOnPlacing">The branch accepts automatically: placed and accepted as one step.</param>
/// <param name="OccurredAtUtc">When it was placed.</param>
public sealed record TakeawayPlaced(TakeawayOrder Order, bool AcceptedOnPlacing, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The venue accepted the order with a ready time: it goes to the kitchen. A null <paramref name="ByStaffId"/> is the
/// sweep accepting one sent before its branch turned automatic acceptance on - nobody at the venue, and the guest still
/// waiting to hear.
/// </summary>
/// <param name="Order">The order, Accepted.</param>
/// <param name="ByStaffId">Who accepted it; null when nobody at the venue did.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayAccepted(TakeawayOrder Order, Guid? ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The venue said no, with or without a reason - or nobody answered within the five minutes and the sweep declined it.</summary>
/// <param name="Order">The order, Declined; the reason as stored (trimmed) is on it.</param>
/// <param name="Cause">Somebody's no, or nobody's answer.</param>
/// <param name="ByStaffId">Who declined it; null for nobody's answer.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayDeclined(
    TakeawayOrder Order, TakeawayDeclineCause Cause, Guid? ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The guest cancelled the order in the app, before the venue answered.</summary>
/// <param name="Order">The order, Cancelled.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayCancelled(TakeawayOrder Order, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The kitchen finished the order: it waits at the counter.</summary>
/// <param name="Order">The order, Ready.</param>
/// <param name="ByStaffId">Who marked it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayMarkedReady(TakeawayOrder Order, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Handed over and paid at the counter - the whole total, by the method on the order.</summary>
/// <param name="Order">The order, Collected, its payment recorded.</param>
/// <param name="ByStaffId">Who handed it over.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayCollected(TakeawayOrder Order, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Ready, and nobody came for it: marked not collected, thirty minutes or more after it was ready.</summary>
/// <param name="Order">The order, NoShow.</param>
/// <param name="ByStaffId">Who marked it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TakeawayMarkedNoShow(TakeawayOrder Order, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;
