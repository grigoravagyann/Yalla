using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Admitting a tab, written once for every way it happens after it was opened: a waiter's tap, the booker
/// arriving. The tab, its stream's <c>TabAdmitted</c> and the branch stream's signal, staged together.
/// </summary>
/// <remarks>
/// See <see cref="TabAdmission"/> and <c>docs/tabs.md</c>. Nothing here saves: the caller saves through
/// <see cref="TabLedger.SaveAppendedAsync"/>, which numbers the event.
/// </remarks>
internal static class TabAdmissions
{
    /// <summary>
    /// Admits a tab that was waiting, in the caller's unit of work. False, staging nothing, when it
    /// already was.
    /// </summary>
    /// <param name="db">The caller's unit of work.</param>
    /// <param name="ledger">The caller's ledger, for the event.</param>
    /// <param name="tab">The tab, tracked.</param>
    /// <param name="because">Why.</param>
    /// <param name="staffId">The waiter, when it was one.</param>
    /// <param name="signalActor">Who the branch stream's row names - the waiter, or the booker's account, or the system.</param>
    /// <param name="nowUtc">Now.</param>
    /// <exception cref="TabAdmissionDecidedException">A waiter already turned it away.</exception>
    public static bool Admit(
        YallaDbContext db,
        TabLedger ledger,
        Tab tab,
        TabAdmissionReason because,
        Guid? staffId,
        (ActorType Type, Guid? Id) signalActor,
        DateTime nowUtc)
    {
        if (!tab.Admit(nowUtc, because, staffId))
        {
            return false;
        }

        ledger.Append(tab.Id, TabEventType.TabAdmitted, new
        {
            admittedBecause = (int)because,
            byStaffId = staffId,
            atUtc = nowUtc,
        });

        Signal(db, tab, TableChangeSignal.TabAdmitted, "table let in to order", signalActor, nowUtc);

        return true;
    }

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
