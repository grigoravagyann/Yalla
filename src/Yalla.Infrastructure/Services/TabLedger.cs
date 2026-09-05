using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Ordering;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The tab's books: recomputing the totals from the lines, and recording what happened.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation to a tab goes through here, so the totals cache and the event stream are written
/// in the same <c>SaveChanges</c> as the change itself. Neither can therefore disagree with the
/// tab - there is no state in which the wine was ordered and the total does not include it, or in
/// which it was ordered and nothing on the stream says so.
/// </para>
/// <para>
/// <b>The stored totals are a cache.</b> <c>Tab.SubtotalAmd</c> and its siblings are denormalised
/// exactly like <c>DiningTable.Status</c>: the authoritative total is, and remains, the sum of the
/// lines. They exist because the floor screen lists thirty tabs and cannot recompute each one, not
/// because they are the truth.
/// </para>
/// </remarks>
internal sealed class TabLedger(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ILogger<TabLedger> logger)
{
    /// <summary>
    /// How many times a totals recomputation may lose the row-version race before giving up.
    /// </summary>
    /// <remarks>
    /// See <see cref="SaveWithTotalsAsync"/> for why retrying is correct here and forbidden on the
    /// table state machine.
    /// </remarks>
    public const int TotalsRetryAttempts = 3;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>How many attempts the last <see cref="SaveWithTotalsAsync"/> actually needed.</summary>
    /// <remarks>Read by the concurrency test, which has to prove the retry does real work.</remarks>
    public int LastAttemptCount { get; private set; }

    /// <summary>
    /// The tab with everything the arithmetic needs, tracked so a caller can mutate it.
    /// </summary>
    public async Task<Tab> LoadForWriteAsync(Guid tabId, CancellationToken cancellationToken)
    {
        var tab = await db.Tabs
            .Include(t => t.Participants)
            .Include(t => t.Orders)
            .ThenInclude(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Include(t => t.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == tabId, cancellationToken)
            ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");

        // No navigation from Tab to its adjustments - they hang off lines as often as off the tab -
        // so they are loaded into the tracker separately and picked up from Local below.
        await db.TabAdjustments
            .Where(a => a.TabId == tabId)
            .LoadAsync(cancellationToken);

        return tab;
    }

    /// <summary>
    /// Computes the bill from the tracked graph, including rows added in this unit of work.
    /// </summary>
    /// <remarks>
    /// Deliberately not a database query. The caller has just added an order that has not been
    /// saved yet, and a query would compute a total that is already out of date by the time it is
    /// stored. Reading the change tracker is what lets the mutation and its total go in together.
    /// </remarks>
    public TabBill Compute(Tab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var lines = tab.Orders
            .SelectMany(order => order.Lines.Select(line => new BillingLine(
                line.Id,
                line.IsSplitAcrossParticipants ? null : order.OwningParticipantId,
                line.UnitPriceAmdSnapshot,
                line.Quantity,
                line.IsVoided,
                line.IsSplitAcrossParticipants,
                [.. line.Shares.Select(s => s.TabParticipantId)])))
            .ToList();

        var adjustments = db.TabAdjustments.Local
            .Where(a => a.TabId == tab.Id && a.IsActive)
            .Select(a => new BillingAdjustment(a.TabOrderLineId, a.Percent, a.AmountAmd))
            .ToList();

        // Tips are excluded, entirely and on purpose: a 10,000 AMD bill settled with 12,000 AMD is
        // not overpaid by 2,000, and folding the tip in makes every number after it unexplainable.
        var paid = tab.Payments
            .Where(p => p.Status is PaymentStatus.Reserved or PaymentStatus.Succeeded)
            .Sum(p => p.AmountAmd);

        var paidByParticipant = tab.Payments
            .Where(p => p.Status is PaymentStatus.Reserved or PaymentStatus.Succeeded)
            .Where(p => p.TabParticipantId is not null)
            .GroupBy(p => p.TabParticipantId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountAmd));

        var participants = tab.Participants
            .OrderBy(p => p.JoinedAtUtc)
            .Select(p => new BillingParticipant(
                p.Id,
                p.Id == tab.HostParticipantId,
                p.Status,
                paidByParticipant.GetValueOrDefault(p.Id)))
            .ToList();

        return TabBilling.Compute(lines, adjustments, participants, tab.ServiceChargePercentSnapshot, paid);
    }

    /// <summary>
    /// Appends an event to the tab's stream. Saved by whoever saves the change it describes.
    /// </summary>
    public TabEvent Append(Guid tabId, TabEventType type, object payload)
    {
        var (actorType, actorId) = ResolveActor();

        var record = new TabEvent(
            tabId,
            type,
            JsonSerializer.Serialize(payload, PayloadJson),
            actorType,
            actorId,
            clock.UtcNow);

        db.TabEvents.Add(record);

        return record;
    }

    /// <summary>
    /// Recomputes the totals onto the tab and saves, retrying if another writer got there first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one place in Yalla where retrying is correct, and Prompt 2 explicitly forbade
    /// it everywhere else.</b> That rule is right for the table state machine: a retried "seat this
    /// table" seats a party at a table that was taken while the request was in flight, and the
    /// second attempt is a different, wrong action.
    /// </para>
    /// <para>
    /// Order placement is the opposite case. Two participants tapping "add" at the same moment both
    /// legitimately succeed - they are inserting different rows, and the operations commute. The
    /// only thing that collides is the totals cache on the shared <c>Tab</c> row, and a retry there
    /// re-reads and re-adds rather than repeating an action. Refusing one of the two would be the
    /// bug: a diner is told their order failed when nothing was wrong with it.
    /// </para>
    /// <para>
    /// So: catch the row-version clash on the tab, reload it, recompute from what is now there, and
    /// try again - up to <see cref="TotalsRetryAttempts"/> times with a short backoff. Payments do
    /// <b>not</b> come through here, because reserving twice is precisely the failure the reserve
    /// exists to prevent.
    /// </para>
    /// </remarks>
    public async Task<TabBill> SaveWithTotalsAsync(Tab tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        for (var attempt = 1; ; attempt++)
        {
            LastAttemptCount = attempt;

            var bill = Compute(tab);
            tab.ApplyComputedTotals(bill.SubtotalAmd, bill.ServiceChargeAmd, bill.PaidAmd);

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return bill;
            }
            catch (DbUpdateConcurrencyException ex)
                when (attempt < TotalsRetryAttempts && IsTabTotalsClash(ex, tab.Id))
            {
                logger.LogInformation(
                    "Another writer changed tab {TabId} while its totals were being recomputed; "
                    + "reloading and retrying (attempt {Attempt} of {Max}).",
                    tab.Id, attempt, TotalsRetryAttempts);

                await ReloadForRetryAsync(ex, tab, cancellationToken);

                // Short and growing a little, so two writers that collided do not collide again on
                // the same tick. Milliseconds: a waiter is standing at the table.
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>True when the clash is the tab's own row version rather than something else's.</summary>
    private static bool IsTabTotalsClash(DbUpdateConcurrencyException exception, Guid tabId) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry => entry.Entity is Tab t && t.Id == tabId);

    /// <summary>
    /// Re-reads what the winner wrote, so the next attempt recomputes against reality.
    /// </summary>
    /// <remarks>
    /// The tab's row version has to be refreshed or the retry clashes again immediately, and its
    /// orders and payments have to be re-read or the recomputed total silently omits whatever the
    /// other writer just added - which would be worse than the failure, because it would commit.
    /// </remarks>
    private async Task ReloadForRetryAsync(
        DbUpdateConcurrencyException exception,
        Tab tab,
        CancellationToken cancellationToken)
    {
        foreach (var entry in exception.Entries)
        {
            await entry.ReloadAsync(cancellationToken);
        }

        await db.Entry(tab).Collection(t => t.Orders).LoadAsync(cancellationToken);

        foreach (var order in tab.Orders)
        {
            await db.Entry(order).Collection(o => o.Lines).LoadAsync(cancellationToken);

            foreach (var line in order.Lines)
            {
                await db.Entry(line).Collection(l => l.Shares).LoadAsync(cancellationToken);
            }
        }

        await db.Entry(tab).Collection(t => t.Payments).LoadAsync(cancellationToken);
        await db.Entry(tab).Collection(t => t.Participants).LoadAsync(cancellationToken);
        await db.TabAdjustments.Where(a => a.TabId == tab.Id).LoadAsync(cancellationToken);
    }

    /// <summary>The tab's newest event sequence, for a client that wants to know where it stands.</summary>
    public async Task<long> MaxSequenceAsync(Guid tabId, CancellationToken cancellationToken) =>
        await db.TabEvents
            .Where(e => e.TabId == tabId)
            .MaxAsync(e => (long?)e.Sequence, cancellationToken) ?? 0L;

    /// <summary>Turns a computed bill into the shape the wire uses.</summary>
    public static TabTotalsSnapshot ToSnapshot(TabBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        return new TabTotalsSnapshot(
            bill.SubtotalAmd, bill.ServiceChargeAmd, bill.TotalAmd, bill.PaidAmd, bill.RemainingAmd);
    }

    private (ActorType Type, Guid? Id) ResolveActor() => actor.Type switch
    {
        ActorType.Staff when actor.StaffMemberId is { } staffId => (ActorType.Staff, staffId),
        ActorType.Diner when actor.DinerUserId is { } dinerId => (ActorType.Diner, dinerId),
        _ => (actor.Type, null),
    };
}
