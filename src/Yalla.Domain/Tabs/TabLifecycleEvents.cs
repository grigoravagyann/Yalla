using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

// What happens to a tab apart from its orders, money and calls, as its handlers hear it (docs/domain-events.md): it
// opens, it is let in to order or turned away, people ask to join and are let on, turned away, taken off or leave, the
// host moves, the split changes, the bill is presented. Each carries the tab, and the participant it is about: a handler
// reads them as the save sees them.

/// <summary>
/// A phone's scan (or a booker with their code) opened the tab and hosts it. It may order at once, or it waits for a
/// waiter (<see cref="Tab.IsAdmitted"/> false).
/// </summary>
/// <param name="Tab">The tab, just opened.</param>
/// <param name="Host">The phone that opened it, its host.</param>
/// <param name="OccurredAtUtc">When it opened.</param>
public sealed record TabOpenedByScan(Tab Tab, TabParticipant Host, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter opened the tab on a seated table, with nobody on it; it may order at once.</summary>
/// <param name="Tab">The tab, just opened.</param>
/// <param name="ByStaffId">The waiter.</param>
/// <param name="OccurredAtUtc">When it opened.</param>
public sealed record TabOpenedByStaff(Tab Tab, Guid? ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A tab that was waiting was let in to order: a waiter's tap, or the booker arriving.</summary>
/// <param name="Tab">The tab, admitted.</param>
/// <param name="Because">Why: <see cref="TabAdmissionReason.Waiter"/> or <see cref="TabAdmissionReason.Booker"/>.</param>
/// <param name="ByStaffId">The waiter, when it was one.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabAdmitted(Tab Tab, TabAdmissionReason Because, Guid? ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter turned away a tab that was waiting: nobody they could see was there. The tab closed.</summary>
/// <param name="Tab">The tab, closed.</param>
/// <param name="Reason">Why, as the waiter gave it; null when they gave none.</param>
/// <param name="TableFreed">The scan had seated the table, and the table goes back with the tab (the caller frees it).</param>
/// <param name="ByStaffId">The waiter.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabTurnedAway(Tab Tab, string? Reason, bool TableFreed, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Somebody landed on the tab pending, waiting for the host to let them on.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The newcomer, pending.</param>
/// <param name="GivenName">The name they gave, or null: "Guest 3" is only the roster's placeholder.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabJoinRequested(Tab Tab, TabParticipant Participant, string? GivenName, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Somebody was let on: by the host's tap, or the booker on their own booking's tab, let on by the booking.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The participant, approved.</param>
/// <param name="AsBooker">The booking let them on, not the host: nobody was asked, so nobody is told on their phone.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabParticipantApproved(Tab Tab, TabParticipant Participant, bool AsBooker, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The host turned away somebody waiting to be let on.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The participant, removed without ever having been let on.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabParticipantRejected(Tab Tab, TabParticipant Participant, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Somebody came off the tab: the host took them off, or they left.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The participant, removed.</param>
/// <param name="Left">They took themselves off.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabParticipantRemoved(Tab Tab, TabParticipant Participant, bool Left, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The host changed what somebody may do: order, see the table's total, pay.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The participant, with the new permissions.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabParticipantPermissionsChanged(Tab Tab, TabParticipant Participant, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Somebody put their name on the tab.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Participant">The participant, renamed.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabParticipantRenamed(Tab Tab, TabParticipant Participant, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The host role moved: a waiter moved it, or the host left and handed it on.</summary>
/// <param name="Tab">The tab, answering to its new host.</param>
/// <param name="From">The host it was, now a guest; null when the tab had none on record.</param>
/// <param name="To">The new host.</param>
/// <param name="HostLeft">The host left the tab and it passed to the longest-standing approved guest.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabHostMoved(Tab Tab, TabParticipant? From, TabParticipant To, bool HostLeft, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The host chose another way of splitting the bill.</summary>
/// <param name="Tab">The tab, with its new settlement mode.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabSettlementModeChanged(Tab Tab, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter presented the bill: no new orders, nobody new on the tab.</summary>
/// <param name="Tab">The tab, closing.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TabBillPresented(Tab Tab, DateTime OccurredAtUtc) : IDomainEvent;
