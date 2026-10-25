using Yalla.Domain.Common;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// The table was handed to another waiter: what is served from now on is theirs (<c>docs/activity.md</c>, transfer).
/// What came before stays with whoever took it.
/// </summary>
/// <param name="Sitting">The sitting, its waiter moved.</param>
/// <param name="TabId">The tab the hand-over was asked through - the History's subject.</param>
/// <param name="FromStaffId">The waiter it was; null when it had none.</param>
/// <param name="ToStaffId">The waiter it is now.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableTransferred(
    TableSession Sitting, Guid TabId, Guid? FromStaffId, Guid ToStaffId, DateTime OccurredAtUtc) : IDomainEvent;
