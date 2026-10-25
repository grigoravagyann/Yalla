using Yalla.Domain.Common;

namespace Yalla.Domain.Tabs;

// What happens to a table's call (a service request: water, napkins, the bill), as its handlers hear it
// (docs/domain-events.md). Each carries the request itself: a handler reads it as the save sees it.

/// <summary>A table called: a waiter, water, napkins, the bill. The floor's list has a new line.</summary>
/// <param name="Request">The call, as raised - its bill columns priced when it chose a scope.</param>
/// <param name="OccurredAtUtc">When it was raised.</param>
public sealed record ServiceRequestRaised(ServiceRequest Request, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// A waiter has the call: "I'm on it", or an acknowledgement of a call nobody had claimed, which claims it in the same
/// moment (raised just before its <see cref="ServiceRequestAcknowledged"/>). The first claim only: a call is claimed once.
/// </summary>
/// <param name="Request">The call, its claim recorded.</param>
/// <param name="ByStaffId">Who claimed it.</param>
/// <param name="OccurredAtUtc">When - the end of the waiter's response time.</param>
public sealed record ServiceRequestClaimed(ServiceRequest Request, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A waiter has answered the call: it leaves the floor's list, and the table hears so.</summary>
/// <param name="Request">The call, now acknowledged.</param>
/// <param name="ByStaffId">Who answered it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record ServiceRequestAcknowledged(ServiceRequest Request, Guid ByStaffId, DateTime OccurredAtUtc) : IDomainEvent;
