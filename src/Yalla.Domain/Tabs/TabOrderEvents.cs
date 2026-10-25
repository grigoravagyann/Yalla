using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

// What happens to an order on a tab, as its handlers hear it (docs/domain-events.md). Each carries the order itself:
// a handler reads it as the save sees it - its lines, its owner, where it now is on the rail.

/// <summary>
/// An order went on the tab: from a guest's phone (it waits at <see cref="TabOrderStatus.New"/>), or keyed in by a
/// waiter, in which case it went straight to the kitchen.
/// </summary>
/// <param name="Order">The order, its lines and estimate built, its server recorded.</param>
/// <param name="WentStraightToKitchen">A waiter's order: placed and moved New to InKitchen in the same breath.</param>
/// <param name="OccurredAtUtc">When it was placed.</param>
public sealed record TabOrderPlaced(TabOrder Order, bool WentStraightToKitchen, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>An order moved along the kitchen's rail: to InKitchen, Ready or Served. A void is <see cref="TabOrderVoided"/>.</summary>
/// <param name="Order">The order, at <paramref name="To"/>.</param>
/// <param name="From">Where it was.</param>
/// <param name="To">Where it is now.</param>
/// <param name="ByStaffId">Who moved it, when a person did.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabOrderMoved(
    TabOrder Order, TabOrderStatus From, TabOrderStatus To, Guid? ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A whole order was voided: off the rail, and every line still on the bill came off it with it.</summary>
/// <param name="Order">The order, now voided.</param>
/// <param name="From">Where it was on the rail.</param>
/// <param name="LinesTakenOff">The lines this void took off the bill - not the ones voided earlier on their own.</param>
/// <param name="ByStaffId">Who voided it, when a person did.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabOrderVoided(
    TabOrder Order,
    TabOrderStatus From,
    IReadOnlyList<TabOrderLine> LinesTakenOff,
    Guid? ByStaffId,
    DateTime OccurredAtUtc) : IDomainEvent
{
    /// <summary>What the order still came to on the bill until this void - the lines it took off, at their prices.</summary>
    public long AmountTakenOffAmd => LinesTakenOff.Sum(l => l.UnitPriceAmdSnapshot * l.Quantity);
}

/// <summary>One line was taken off the bill on its own (sent back, a mistake), the rest of its order untouched.</summary>
/// <param name="Order">The order the line is on.</param>
/// <param name="Line">The line, now voided.</param>
/// <param name="Reason">Why, as the caller gave it (the line stores it trimmed).</param>
/// <param name="ByStaffId">Who voided it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabOrderLineVoided(
    TabOrder Order, TabOrderLine Line, string Reason, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;
