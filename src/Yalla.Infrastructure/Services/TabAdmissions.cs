using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A signal about a table's tab on the branch's change stream, written once for every path that puts one there: the
/// tab-lifecycle handlers (<c>DomainEvents/Tabs/TabFloorSignal</c> - a scan waiting, the table let in, a scan turned
/// away while the party stays), a table handed to another waiter (<c>DomainEvents/Tables/TableChangeStream</c>, on
/// <c>TableTransferred</c>) and a tab written off (<c>DomainEvents/Payments/WrittenOffTabFloorSignal</c>, on
/// <c>TabWrittenOff</c>).
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
    /// <param name="db">The caller's context.</param>
    /// <param name="tab">The tab the signal is about.</param>
    /// <param name="signal">What it signals.</param>
    /// <param name="reason">In words, for the row.</param>
    /// <param name="signalActor">Who the row names.</param>
    /// <param name="nowUtc">When.</param>
    /// <param name="tableStatus">
    /// The table's status, both sides of the row. Occupied for the admission signals and a hand-over, which happen at a
    /// seated table; a written-off tab's (signal 9) passes the table's own, since a tab may be written off after its
    /// table went free.
    /// </param>
    public static void Signal(
        YallaDbContext db,
        Tab tab,
        TableChangeSignal signal,
        string reason,
        (ActorType Type, Guid? Id) signalActor,
        DateTime nowUtc,
        TableStatus tableStatus = TableStatus.Occupied) =>
        db.TableStateChanges.Add(new TableStateChange(
                tab.BranchId,
                tab.DiningTableId,
                tableStatus,
                tableStatus,
                reason,
                signalActor.Id is null ? ActorType.System : signalActor.Type,
                nowUtc,
                Guid.CreateVersion7(),
                signalActor.Id,
                tabId: tab.Id,
                tableSessionId: tab.TableSessionId)
            .WithSignal(signal));
}
