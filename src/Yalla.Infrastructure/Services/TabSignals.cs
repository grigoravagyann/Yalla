using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What a tab command tells the rest of the floor, in the caller's unit of work: a tab let in, a signal on
/// the branch change stream, and the activity log's line.
/// </summary>
internal sealed class TabSignals(
    YallaDbContext db,
    ICurrentActor actor,
    TabLedger ledger,
    IActivityLog activity)
{
    /// <summary>Something staff did to a tab, for the activity log, in the caller's unit of work.</summary>
    internal Task LogTabAsync(Tab tab, string action, object? summary, CancellationToken cancellationToken) =>
        activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, action, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                VenueId = tab.Branch.VenueId,
                TableSessionId = tab.TableSessionId,
                TableLabel = tab.DiningTable.Label,
                Summary = summary,
            },
            cancellationToken);

    /// <summary>
    /// Admits a tab that was waiting, in the caller's unit of work: the tab, its stream's
    /// <c>TabAdmitted</c>, and the branch stream's signal. False, staging nothing, when it already was.
    /// </summary>
    /// <exception cref="TabAdmissionDecidedException">A waiter already turned it away.</exception>
    internal bool AdmitInContext(Tab tab, TabAdmissionReason because, Guid? staffId, DateTime nowUtc) =>
        TabAdmissions.Admit(db, ledger, tab, because, staffId, SignalActor(staffId), nowUtc);

    /// <summary>
    /// Puts a signal about the table's tab on the branch change stream - a same-status row, as the
    /// hold-extended row is - in the caller's unit of work, so every tablet following the stream moves.
    /// </summary>
    internal void SignalTable(Tab tab, TableChangeSignal signal, string reason, Guid? staffId, DateTime nowUtc) =>
        TabAdmissions.Signal(db, tab, signal, reason, SignalActor(staffId), nowUtc);

    /// <summary>Who a branch-stream row names: the waiter, else the diner account on the request, else the system.</summary>
    private (ActorType Type, Guid? Id) SignalActor(Guid? staffId) =>
        staffId is { } staff
            ? (ActorType.Staff, staff)
            : actor.Type == ActorType.Diner && actor.DinerUserId is { } diner
                ? (ActorType.Diner, diner)
                : (ActorType.System, null);
}
