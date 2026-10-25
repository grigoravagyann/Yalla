using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A signal about a table's tab on the branch's change stream, written once for every path that puts one there: the
/// tab-lifecycle handlers (<c>DomainEvents/Tabs/TabFloorSignal</c> - a scan waiting, the table let in, a scan turned
/// away while the party stays) and a tab moved to another table (<see cref="TableTransferService"/>).
/// </summary>
/// <remarks>
/// See <see cref="TabAdmission"/> and <c>docs/tabs.md</c>. Nothing here saves: the row commits with the change it is
/// about.
/// </remarks>
internal static class TabAdmissions
{
    /// <summary>
    /// Puts a signal about the table's tab on the branch change stream - a same-status row, as the
    /// hold-extended row is - in the caller's unit of work, so every tablet following the stream moves.
    /// </summary>
    public static void Signal(
        YallaDbContext db,
        Tab tab,
        TableChangeSignal signal,
        string reason,
        (ActorType Type, Guid? Id) signalActor,
        DateTime nowUtc) =>
        db.TableStateChanges.Add(new TableStateChange(
                tab.BranchId,
                tab.DiningTableId,
                TableStatus.Occupied,
                TableStatus.Occupied,
                reason,
                signalActor.Id is null ? ActorType.System : signalActor.Type,
                nowUtc,
                Guid.CreateVersion7(),
                signalActor.Id,
                tabId: tab.Id,
                tableSessionId: tab.TableSessionId)
            .WithSignal(signal));
}
