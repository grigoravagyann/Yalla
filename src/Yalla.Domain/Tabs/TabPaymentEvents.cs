using Yalla.Domain.Common;

namespace Yalla.Domain.Tabs;

// What happens to the money on a tab, as its handlers hear it (docs/domain-events.md): a payment taken, the bill settled,
// a discount or comp given or reversed, a balance written off. Each carries the tab or the adjustment itself: a handler
// reads the totals as the save sees them - the ledger has applied them by then.

/// <summary>A payment that went through was taken onto the tab: the split is locked, the balance is down by it.</summary>
/// <param name="Tab">The tab, its totals applied with this payment counted.</param>
/// <param name="Payment">The payment, succeeded, with who took it and whose table it was.</param>
/// <param name="SettledTheBill">Nothing is owed now: the tab closed with this payment (<see cref="TabSettled"/> follows).</param>
/// <param name="OccurredAtUtc">When it was taken.</param>
public sealed record TabPaymentRecorded(Tab Tab, Payment Payment, bool SettledTheBill, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The bill was paid in full and the tab closed - and its sitting with it, when it was still open. Not the table.</summary>
/// <param name="Tab">The tab, closed.</param>
/// <param name="SittingClosed">Whether this closed the tab's sitting; false when it had already ended (the table was freed first).</param>
/// <param name="OccurredAtUtc">When it closed.</param>
public sealed record TabSettled(Tab Tab, bool SittingClosed, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A manager wrote off what was still owed: the diners left without paying and the venue accepts the loss.</summary>
/// <param name="Tab">The tab, abandoned.</param>
/// <param name="WrittenOffAmd">What was still owed when it was written off.</param>
/// <param name="Reason">Why, as the manager gave it.</param>
/// <param name="ByStaffId">The manager.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabWrittenOff(Tab Tab, long WrittenOffAmd, string? Reason, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A manager gave a discount or comped something: on the whole bill, or on one line.</summary>
/// <param name="Adjustment">The adjustment, standing.</param>
/// <param name="OccurredAtUtc">When it was given.</param>
public sealed record TabAdjustmentAdded(TabAdjustment Adjustment, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A manager reversed a discount or comp: it stops counting, and stays on the record.</summary>
/// <param name="Adjustment">The adjustment, reversed - who did it is on it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabAdjustmentReversed(TabAdjustment Adjustment, DateTime OccurredAtUtc) : IDomainEvent;
