using Yalla.Domain.Enums;

namespace Yalla.Application.Activity;

// ---------------------------------------------------------------- transferring a table

/// <summary>Hands a table to another waiter from now on.</summary>
/// <param name="TabId">The table's tab.</param>
/// <param name="ToStaffMemberId">The waiter taking it: active, at the tab's branch (or venue-wide), and one who serves - a waiter or above, never the kitchen.</param>
public sealed record TransferTableCommand(Guid TabId, Guid ToStaffMemberId);

/// <summary>A table handed over.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="ServerStaffMemberId">Whose it is now.</param>
/// <param name="ServerName">Their name.</param>
/// <param name="PreviousServerStaffMemberId">Whose it was; absent when nobody served it.</param>
/// <param name="PreviousServerName">Their name.</param>
/// <param name="TransferredAtUtc">From when - what came before stays with the earlier waiter.</param>
/// <param name="TableSessionId">The sitting, which carries the waiter.</param>
/// <param name="TableLabel">The table.</param>
/// <param name="WasReplay">Already theirs: answered as done, nothing written.</param>
public sealed record TableTransferResult(
    Guid TabId,
    Guid ServerStaffMemberId,
    string ServerName,
    Guid? PreviousServerStaffMemberId,
    string? PreviousServerName,
    DateTime TransferredAtUtc,
    Guid TableSessionId,
    string TableLabel,
    bool WasReplay);

/// <summary>Transfers. The current waiter, or a manager or above at the branch.</summary>
public interface ITableTransferService
{
    Task<TableTransferResult> TransferAsync(TransferTableCommand command, CancellationToken cancellationToken = default);
}

/// <summary>The waiter named cannot take the table: 422 <c>transfer-target-ineligible</c>, saying why.</summary>
public sealed class TransferTargetIneligibleException(Guid staffMemberId, string why, string message) : Exception(message)
{
    /// <summary>The person named.</summary>
    public Guid StaffMemberId { get; } = staffMemberId;

    /// <summary><c>not-found</c>, <c>inactive</c>, <c>other-branch</c> or <c>does-not-serve</c>.</summary>
    public string Why { get; } = why;

    public const string NotFound = "not-found";
    public const string Inactive = "inactive";
    public const string OtherBranch = "other-branch";
    public const string DoesNotServe = "does-not-serve";
}

/// <summary>The tab's sitting is over - paid and closed, or the table freed: 409 <c>tab-not-transferable</c>.</summary>
public sealed class TabNotTransferableException(Guid tabId, TabStatus status)
    : Exception($"This table's bill is {status} and its party has gone; there is nothing to hand over.")
{
    public Guid TabId { get; } = tabId;

    public TabStatus Status { get; } = status;
}
