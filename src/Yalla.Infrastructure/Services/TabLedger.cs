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
/// Every mutation to a tab goes through here, so the event stream is written in the same
/// <c>SaveChanges</c> as the change itself. There is no state in which the wine was ordered and
/// nothing on the stream says so.
/// </para>
/// <para>
/// <b>The stored totals are a cache.</b> <c>Tab.SubtotalAmd</c> and its siblings are denormalised
/// exactly like <c>DiningTable.Status</c>: the authoritative total is, and remains, the sum of the
/// lines. They exist because the floor screen lists thirty tabs and cannot recompute each one, not
/// because they are the truth.
/// </para>
/// <para>
/// <b>That is why the cache is no longer written by the order-insert transaction.</b> It used to
/// be, and the shared <c>Tab</c> row was then a contention point that six genuinely simultaneous
/// orders could exhaust - surfacing a concurrency error to a diner adding a coffee, which is the
/// worst-looking failure in the product because the fault is invisible and the app simply appears
/// broken. Raising the retry ceiling would have moved that point rather than removed it. Inserting
/// the order without touching the tab row removes it: two orders no longer write anything in
/// common, so they cannot collide. See <see cref="RecomputeTotalsAsync"/> and
/// <c>docs/tab-totals.md</c>.
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
    /// Generous, and safe to be generous with, because a recomputation is idempotent: it derives
    /// the whole answer from the lines, so losing and repeating it cannot double anything. It is
    /// also no longer on the path that answers a diner - see <see cref="RecomputeTotalsAsync"/> -
    /// so exhausting it costs a stale cache that the next read repairs, not a failed order.
    /// </remarks>
    public const int TotalsRetryAttempts = 10;

    /// <summary>How many times an event may be renumbered after losing its place to another writer.</summary>
    /// <remarks>
    /// Ten, not five. Renumbering is a pure retry - nothing in the unit of work is re-applied - and
    /// the number has to cover the worst case rather than the common one: ten phones at one table
    /// ordering at once all reach for the same position, and the tenth to win has lost nine times.
    /// </remarks>
    public const int SequenceRetryAttempts = 10;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>Events appended in this unit of work, in the order they were appended.</summary>
    private readonly List<TabEvent> pending = [];

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
    /// Computes the bill from the change tracker, including rows added in this unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a database query. The caller has just added an order that has not been saved
    /// yet, and a query would compute a total that is already stale by the time it is stored.
    /// Reading the tracker is what lets the mutation and its total go in together.
    /// </para>
    /// <para>
    /// <b>Read from <c>Local</c>, not from the tab's navigation collections.</b> Those two are
    /// usually the same thing and diverge exactly where it matters most: after a lost row-version
    /// race, the retry reloads the tab's orders from the database, and whether the writer's own
    /// unsaved order survives that reload into <c>tab.Orders</c> is a question about EF's fixup
    /// rules rather than about the bill. <c>Local</c> is every tracked entity, however it got there,
    /// which is precisely the set that is about to be saved. Getting this wrong cost a whole order:
    /// two diners ordered at once, both succeeded, and the total showed only one of them.
    /// </para>
    /// </remarks>
    public TabBill Compute(Tab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var orders = db.TabOrders.Local.Where(o => o.TabId == tab.Id).ToList();

        var lines = orders
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

        var settled = db.Payments.Local
            .Where(p => p.TabId == tab.Id)
            .Where(p => p.Status is PaymentStatus.Reserved or PaymentStatus.Succeeded)
            .ToList();

        // Tips are excluded, entirely and on purpose: a 10,000 AMD bill settled with 12,000 AMD is
        // not overpaid by 2,000, and folding the tip in makes every number after it unexplainable.
        var paid = settled.Sum(p => p.AmountAmd);

        var paidByParticipant = settled
            .Where(p => p.TabParticipantId is not null)
            .GroupBy(p => p.TabParticipantId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountAmd));

        var participants = db.TabParticipants.Local
            .Where(p => p.TabId == tab.Id)
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
        pending.Add(record);

        return record;
    }

    /// <summary>
    /// Saves, numbering any appended events first so the stream reads in the order things happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every save on a path that appends an event must come through here.</b> The numbering is
    /// what the whole event stream rests on, and a <c>db.SaveChangesAsync</c> beside it would write
    /// events with no position at all.
    /// </para>
    /// <para>
    /// The positions come from the tab's current maximum, so two writers racing on one tab can both
    /// reach for the same number. The unique index on <c>(TabId, Sequence)</c> is what makes that
    /// safe: the loser sees a violation, re-reads the maximum, renumbers, and tries again. That is a
    /// renumbering rather than a re-application - nothing else in the unit of work is touched - so
    /// it is safe even on the payment path, which must never retry the payment itself.
    /// </para>
    /// </remarks>
    public async Task SaveAppendedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await NumberPendingAsync(cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                pending.Clear();

                return;
            }
            catch (DbUpdateException ex)
                when (attempt < SequenceRetryAttempts
                      && UniqueViolation.IsOn(ex, DatabaseIndexNames.TabEventSequence))
            {
                logger.LogInformation(
                    "Another writer took the same place in tab {TabId}'s event stream; renumbering "
                    + "(attempt {Attempt} of {Max}).",
                    pending[0].TabId, attempt, SequenceRetryAttempts);

                // Jittered, not fixed. Ten writers that collided once and then all re-read the
                // maximum on the same tick would simply collide again in the same order; a random
                // few milliseconds spreads them out, which is what keeps the worst case inside the
                // attempt budget rather than merely making it less likely.
                await Task.Delay(Random.Shared.Next(2, 12 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Gives each appended event the next free position on its tab.</summary>
    private async Task NumberPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var group in pending.GroupBy(e => e.TabId))
        {
            var next = await MaxSequenceAsync(group.Key, cancellationToken) + 1L;

            foreach (var record in group)
            {
                record.PlaceAt(next++);
            }
        }
    }

    /// <summary>
    /// Writes a change and its recomputed totals in one transaction, retrying a lost row-version
    /// race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the staff actions that change what is owed - a void, a discount, a comp - where the
    /// caller is a manager on one tablet looking at one tab, contention on the tab row is not a
    /// real phenomenon, and answering with the new total in the same breath is worth more than
    /// avoiding a collision that does not happen.
    /// </para>
    /// <para>
    /// <b>Order placement no longer comes through here.</b> That is the path where contention is
    /// real - ten phones at one table - and it now inserts the order without touching the tab row
    /// at all, recomputing the cache afterwards through
    /// <see cref="RecomputeTotalsAsync"/>. Payments do not come through here either, for the
    /// opposite reason: they must take the tab row and must <b>not</b> retry, because reserving
    /// twice against the same balance is precisely the failure the reserve exists to prevent.
    /// </para>
    /// <para>
    /// Retrying is correct here for the same reason it is correct there and forbidden on the table
    /// state machine: a recomputation re-reads and re-derives rather than repeating an action. A
    /// retried "seat this table" seats a party at a table somebody else just took; a retried
    /// recomputation produces the same answer over fresher rows.
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
                await SaveAppendedAsync(cancellationToken);

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

    /// <summary>
    /// Recomputes the totals cache from the lines, in its own short transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called after the change has already committed, not as part of it.</b> An order insert
    /// writes the order, its lines and one event, and touches nothing the next order also touches;
    /// this then brings the denormalised columns on the shared <c>Tab</c> row back in line. Two
    /// diners ordering at the same instant therefore cannot collide on the thing that used to make
    /// them collide, and the six-simultaneous-orders failure is gone rather than pushed further out.
    /// </para>
    /// <para>
    /// It still takes the row version, and that is what makes it converge. Suppose one writer
    /// computes a total from nine orders while another commits the tenth: the first loses the
    /// version check, reloads, recomputes against ten and wins. Because the recomputation derives
    /// the whole answer from the lines rather than adding to what is there, repeating it is free
    /// and losing it costs nothing.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> The caller's work is already committed and its answer is already
    /// correct; a diner must not be told their order failed because a cache write lost a race. A
    /// recomputation that cannot win is logged and left, and the next read repairs it - see
    /// <see cref="EnsureTotalsFreshAsync"/>.
    /// </para>
    /// </remarks>
    /// <returns>The bill as last computed, whether or not the cache write landed.</returns>
    public async Task<TabBill> RecomputeTotalsAsync(Guid tabId, CancellationToken cancellationToken)
    {
        var tab = await LoadForWriteAsync(tabId, cancellationToken);

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
            catch (DbUpdateConcurrencyException ex) when (IsTabTotalsClash(ex, tab.Id))
            {
                if (attempt >= TotalsRetryAttempts)
                {
                    // Warning, not an exception. The order is placed and the lines are the truth;
                    // what is stale is a cache, and saying so is more useful than failing a request
                    // that succeeded.
                    logger.LogWarning(
                        "Tab {TabId}'s totals cache could not be refreshed after {Attempts} attempts; "
                        + "it will be recomputed on the next read.",
                        tab.Id, attempt);

                    return bill;
                }

                logger.LogInformation(
                    "Another writer changed tab {TabId} while its totals were being recomputed; "
                    + "reloading and retrying (attempt {Attempt} of {Max}).",
                    tab.Id, attempt, TotalsRetryAttempts);

                await ReloadForRetryAsync(ex, tab, cancellationToken);

                await Task.Delay(Random.Shared.Next(2, 12 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Repairs the cache on read when it disagrees with the lines.
    /// </summary>
    /// <remarks>
    /// The safety net behind <see cref="RecomputeTotalsAsync"/>, for the case where every writer's
    /// recomputation lost its race. It costs a comparison on a bill that was being computed anyway,
    /// and it writes only when the numbers actually differ - so on the overwhelmingly common path
    /// where the cache is already right, this is free and touches no row.
    /// </remarks>
    public async Task<TabBill> EnsureTotalsFreshAsync(Tab tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var bill = Compute(tab);

        if (tab.SubtotalAmd == bill.SubtotalAmd
            && tab.ServiceChargeAmd == bill.ServiceChargeAmd
            && tab.PaidAmd == bill.PaidAmd)
        {
            return bill;
        }

        logger.LogInformation(
            "Tab {TabId}'s totals cache was stale on read ({CachedSubtotal} vs {ComputedSubtotal}); refreshing.",
            tab.Id, tab.SubtotalAmd, bill.SubtotalAmd);

        return await RecomputeTotalsAsync(tab.Id, cancellationToken);
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

        // Pull the winner's rows into the tracker. Queried rather than navigated, so this does not
        // depend on how EF reconciles a reloaded collection with entities we have added and not yet
        // saved - Compute reads Local, which holds both.
        await db.TabOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Where(o => o.TabId == tab.Id)
            .LoadAsync(cancellationToken);

        await db.Payments.Where(p => p.TabId == tab.Id).LoadAsync(cancellationToken);
        await db.TabParticipants.Where(p => p.TabId == tab.Id).LoadAsync(cancellationToken);
        await db.TabAdjustments.Where(a => a.TabId == tab.Id).LoadAsync(cancellationToken);
    }

    /// <summary>The tab's newest event sequence, for a client that wants to know where it stands.</summary>
    public async Task<long> MaxSequenceAsync(Guid tabId, CancellationToken cancellationToken) =>
        await db.TabEvents
            .AsNoTracking()
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
