namespace Yalla.Domain.Takeaway;

/// <summary>
/// The order is not in the state for that: 409 <c>takeaway-status-conflict</c>, <c>context.orderId</c> and
/// <c>status</c> (the order's <see cref="TakeawayStatus"/> now), so the tablet or the app redraws it.
/// </summary>
public sealed class TakeawayStatusConflictException(Guid orderId, TakeawayStatus status, string message)
    : DomainStateException(message)
{
    public const string ErrorCode = "takeaway-status-conflict";

    public Guid OrderId { get; } = orderId;

    public TakeawayStatus Status { get; } = status;
}

/// <summary>
/// "No-show" before thirty minutes past ready: 409 <c>takeaway-no-show-too-early</c>, <c>context.orderId</c> and
/// <c>noShowFromUtc</c>.
/// </summary>
public sealed class TakeawayNoShowTooEarlyException(Guid orderId, DateTime noShowFromUtc)
    : DomainStateException("A takeaway order can be marked a no-show thirty minutes after it was ready.")
{
    public const string ErrorCode = "takeaway-no-show-too-early";

    public Guid OrderId { get; } = orderId;

    public DateTime NoShowFromUtc { get; } = noShowFromUtc;
}

/// <summary>The branch does not take takeaway orders: 409 <c>takeaway-not-accepted</c>, <c>context.branchId</c>.</summary>
public sealed class TakeawayNotAcceptedException(Guid branchId)
    : DomainStateException("This venue does not take takeaway orders.")
{
    public const string ErrorCode = "takeaway-not-accepted";

    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// The branch is closed for takeaway now - shut, or past its last order: 409 <c>takeaway-closed</c>,
/// <c>context.branchId</c>, and <c>opensAtUtc</c> / <c>opensAt</c> (local "HH:mm") when it opens again within a week.
/// </summary>
public sealed class TakeawayClosedException(Guid branchId, DateTime? opensAtUtc, string? opensAtLocal)
    : DomainStateException("This venue is not taking takeaway orders right now.")
{
    public const string ErrorCode = "takeaway-closed";

    public Guid BranchId { get; } = branchId;

    public DateTime? OpensAtUtc { get; } = opensAtUtc;

    public string? OpensAtLocal { get; } = opensAtLocal;
}

/// <summary>
/// "As soon as possible" while the kitchen has as many takeaway orders in progress as the venue allows: 409
/// <c>takeaway-busy</c>, <c>context.branchId</c> and <c>maxInProgress</c>. Picking a later slot may still work.
/// </summary>
public sealed class TakeawayBusyException(Guid branchId, int maxInProgress)
    : DomainStateException("The kitchen is busy with takeaway orders right now. Pick a later time.")
{
    public const string ErrorCode = "takeaway-busy";

    public Guid BranchId { get; } = branchId;

    public int MaxInProgress { get; } = maxInProgress;
}

/// <summary>
/// The pickup time asked for is not one on offer (gone, too soon, past the last order, or full): 409
/// <c>takeaway-slot-unavailable</c>, <c>context.branchId</c>, <c>at</c> and <c>slots</c> - the ones on offer now.
/// </summary>
public sealed class TakeawaySlotUnavailableException(Guid branchId, string at, IReadOnlyList<string> slots)
    : DomainStateException("That pickup time is no longer available. Pick another.")
{
    public const string ErrorCode = "takeaway-slot-unavailable";

    public Guid BranchId { get; } = branchId;

    public string At { get; } = at;

    public IReadOnlyList<string> Slots { get; } = slots;
}

/// <summary>
/// Dishes that cannot be ordered to take away: 422 <c>takeaway-items-unavailable</c>, <c>context.items</c> naming each
/// with its <see cref="TakeawayItemRefusal"/>. Nothing is saved.
/// </summary>
public sealed class TakeawayItemsUnavailableException(IReadOnlyList<(Guid MenuItemId, TakeawayItemRefusal Reason)> items)
    : ArgumentException("Some of these dishes cannot be ordered to take away. Remove them and send again.")
{
    public const string ErrorCode = "takeaway-items-unavailable";

    public IReadOnlyList<(Guid MenuItemId, TakeawayItemRefusal Reason)> Items { get; } = items;
}
