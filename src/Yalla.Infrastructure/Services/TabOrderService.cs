using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Ordering;
using Yalla.Application.Reservations;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Ordering, voiding and adjusting - the venue's till.
/// </summary>
/// <remarks>
/// <para>
/// A diner's phone and a waiter's tablet come through <see cref="PlaceOrderAsync"/> together. That
/// is the hard dependency the live bill rests on: the total is only right if <b>every</b> order
/// goes through the system, including the ones spoken to a waiter, and a separate path for those
/// would be a second source of truth that the bill could not see.
/// </para>
/// <para>
/// Every mutation here goes through <see cref="TabLedger"/>, so the totals cache and the tab's
/// event stream are written in the same <c>SaveChanges</c> as the change itself.
/// </para>
/// </remarks>
internal sealed class TabOrderService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    StaffBranchGuard branchGuard,
    TabLedger ledger,
    IOutbox outbox,
    ILogger<TabOrderService> logger) : ITabOrderService
{
    /// <summary>
    /// How many attempts the last totals recomputation needed.
    /// </summary>
    /// <remarks>
    /// Exposed so the concurrency test can assert the retry is doing real work rather than being
    /// dead code that happens never to fire. Note that this counts the <b>cache refresh</b>, which
    /// happens after the order has already committed - it is not a count of attempts to place the
    /// order, which never retries because it no longer contends with anything.
    /// </remarks>
    internal int RetryAttemptsUsed => ledger.LastAttemptCount;

    // ---------------------------------------------------------------- placing

    public async Task<OrderView> PlaceOrderAsync(
        PlaceOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Items.Count == 0)
        {
            throw new ArgumentException("An order must contain at least one item.", nameof(command));
        }

        if (await FindReplayAsync(command.TabId, command.ClientCommandId, cancellationToken) is { } replay)
        {
            logger.LogInformation(
                "Order command {ClientCommandId} was already applied as {OrderId}; returning the original.",
                command.ClientCommandId, replay);

            return await BuildOrderViewAsync(replay, wasReplay: true, cancellationToken);
        }

        var tab = await ledger.LoadForWriteAsync(command.TabId, cancellationToken);

        if (tab.Status != TabStatus.Open)
        {
            throw new TabNotAcceptingOrdersException(tab.Id, tab.Status);
        }

        var nowUtc = clock.UtcNow;
        var order = await BuildOrderAsync(tab, command, nowUtc, cancellationToken);

        // A tab a scan opened with no proved number behind it takes no PHONE order until a waiter has seen
        // the table and admitted it (decided 2026-10-20). After BuildOrderAsync, so who may order at all is
        // answered first - a pending guest is still the 403 it always was. The phone is told to wait: 409,
        // not 403 - its flags are fine, the table is not let in yet.
        //
        // Staff may always order, on any tab, and a staff order never admits one: it is not a phone order,
        // and admitting on it would hand a real party's sitting to whoever scanned a photographed code from
        // home before the party did - host of the only tab, approving the party's own joins. Admission is
        // the Admit tap, a booking, a proved scanner, or the booker arriving; nothing else.
        if (!tab.IsAdmitted && order.PlacedByStaffId is null)
        {
            throw new TabAwaitingAdmissionException(tab.Id);
        }

        db.TabOrders.Add(order);

        ledger.Append(tab.Id, TabEventType.OrderPlaced, new
        {
            orderId = order.Id,
            placedByParticipantId = order.PlacedByParticipantId,
            placedByStaffId = order.PlacedByStaffId,
            onBehalfOfParticipantId = order.OnBehalfOfParticipantId,
            estimatedReadyAtUtc = order.EstimatedReadyAtUtc,
            lines = order.Lines.Select(l => new
            {
                lineId = l.Id,
                name = l.NameSnapshot,
                unitPriceAmd = l.UnitPriceAmdSnapshot,
                quantity = l.Quantity,
                isShared = l.IsShared,
                isTableAttributed = l.IsTableAttributed,

                // Each line's guest, as the tab reads it: its own when the waiter named one for it,
                // otherwise the order's, and nobody's on a shared line. A phone catching up
                // attributes the line from this.
                onBehalfOfParticipantId = OnBehalfOf(order, l),
            }),
        });

        // A waiter's order is in the kitchen the moment it is sent (decided by the user, 2026-10-20).
        // The waiter who took it at the table is already the check a phone's order waits at New for,
        // so a second "send" tap could only be forgotten. Told as the rail's own event, right after
        // the placing, so every phone and tablet reading the stream sees the move it already knows.
        if (order.PlacedByStaffId is { } placedBy)
        {
            order.MoveTo(TabOrderStatus.InKitchen);

            ledger.Append(tab.Id, TabEventType.OrderStatusChanged, new
            {
                orderId = order.Id,
                fromStatus = (int)TabOrderStatus.New,
                toStatus = (int)TabOrderStatus.InKitchen,
                byStaffId = placedBy,
            });
        }

        // The order, its lines and the event, in one transaction - and deliberately nothing else.
        // The tab's totals are a cache, and the authoritative total is the sum of the lines, so the
        // insert does not need to touch the tab row to be correct. Not touching it is what makes
        // ten simultaneous orders safe: they write nothing in common, so there is no row version to
        // lose and no concurrency error for a diner to see. See TabLedger and docs/tab-totals.md.
        try
        {
            await ledger.SaveAppendedAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabOrderClientCommand))
        {
            // The same command, from a request that passed the replay check at the same moment and
            // committed first - a retry racing its original. Nothing of this attempt was written, so
            // answer with the order that won, exactly as a replay would.
            db.ChangeTracker.Clear();
            ledger.DiscardPending();

            var winner = await FindReplayAsync(command.TabId, command.ClientCommandId, cancellationToken)
                         ?? throw new InvalidOperationException(
                             $"Order command {command.ClientCommandId} lost a uniqueness race on tab "
                             + $"{command.TabId} to an order that cannot be read back.");

            logger.LogInformation(
                "Order command {ClientCommandId} raced its own retry on tab {TabId}; answering with {OrderId}.",
                command.ClientCommandId, command.TabId, winner);

            return await BuildOrderViewAsync(winner, wasReplay: true, cancellationToken);
        }

        // And then the cache, in its own short transaction. This can lose a race against another
        // order and simply try again; it never throws, because the diner's order is already placed
        // and telling them otherwise would be a lie about work that succeeded.
        await ledger.RecomputeTotalsAsync(tab.Id, cancellationToken);

        logger.LogInformation(
            "Order {OrderId} placed on tab {TabId} with {LineCount} lines, due about {EstimatedReadyAtUtc}.",
            order.Id, tab.Id, order.Lines.Count, order.EstimatedReadyAtUtc);

        return await BuildOrderViewAsync(order.Id, wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// Turns the command into an order, deciding who each line belongs to and snapshotting the menu.
    /// </summary>
    private async Task<TabOrder> BuildOrderAsync(
        Tab tab,
        PlaceOrderCommand command,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var (order, ordererId) = await AuthoriseAndCreateAsync(tab, command, nowUtc, cancellationToken);

        var wanted = command.Items.Select(i => i.MenuItemId).Distinct().ToList();

        var items = await db.MenuItems
            .AsNoTracking()
            .Where(i => wanted.Contains(i.Id))
            .Select(i => new { i.Id, i.Name, i.PriceAmd, i.IsAvailable, i.PrepMinutes, i.MenuCategory.BranchId })
            .ToListAsync(cancellationToken);

        // The diner menu never publishes an unfinished item - no photo, no allergens - so a phone
        // could only hold its id from somewhere other than the menu. Refused on the diner path by
        // name, as a sold-out dish is. A waiter may still key one in from the console, where a dish
        // being entered is visible and ordering it is deliberately allowed.
        var unpublished = actor.Type == ActorType.Staff
            ? new HashSet<Guid>()
            : (await db.MenuItems
                    .AsNoTracking()
                    .Where(i => wanted.Contains(i.Id))
                    .Where(MenuItemCompleteness.Incomplete)
                    .Select(i => i.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

        // Whoever is at the table right now, in join order. Snapshotted onto every shared line, so
        // a friend who joins ten minutes later is not on the bottle that has already been poured.
        var presentNow = tab.Participants
            .Where(p => p.Status == ParticipantStatus.Approved)
            .OrderBy(p => p.JoinedAtUtc)
            .Select(p => p.Id)
            .ToList();

        var longestPrep = 0;

        foreach (var input in command.Items)
        {
            var item = items.FirstOrDefault(i => i.Id == input.MenuItemId)
                       ?? throw new KeyNotFoundException($"Menu item {input.MenuItemId} was not found.");

            if (item.BranchId != tab.BranchId)
            {
                throw new DomainStateException("That item is on another branch's menu.");
            }

            // Refused whole, not silently trimmed - a partial order is a decision made on the
            // diner's behalf, and they find out about it when the food arrives.
            if (!item.IsAvailable || unpublished.Contains(item.Id))
            {
                throw new MenuItemUnavailableException(item.Id, item.Name);
            }

            if (input.Quantity <= 0)
            {
                throw new ArgumentException(
                    $"{item.Name} was ordered with a quantity of {input.Quantity}.", nameof(command));
            }

            // A line nobody owns splits like a shared one. The flag keeps the two distinguishable:
            // "the table shared a bottle" and "we do not know who ordered this" are the same
            // arithmetic and completely different facts. A line the waiter named a guest for is
            // that guest's, whoever the rest of the order is for.
            var tableAttributed = (input.ParticipantId ?? ordererId) is null && !input.IsShared;

            var line = order.AddLine(
                item.Id, item.Name, item.PriceAmd, input.Quantity,
                isShared: input.IsShared,
                isTableAttributed: tableAttributed,
                note: input.Note,
                onBehalfOfParticipantId: input.ParticipantId);

            if (line.IsSplitAcrossParticipants)
            {
                foreach (var participantId in presentNow)
                {
                    line.AddShare(participantId);
                }
            }

            // An item with no prep time contributes nothing to the estimate rather than defaulting
            // to some invented number. It can only be one a diner never saw - the diner-facing menu
            // drops incomplete items - so this is a waiter ordering from the console for a dish
            // still being entered, and a made-up "ready in 15 minutes" would be worse than an
            // estimate drawn from the items that do know how long they take.
            longestPrep = Math.Max(longestPrep, item.PrepMinutes ?? 0);
        }

        // The longest, not the sum: a kitchen cooks an order together and sends it out together.
        order.EstimateReadyAt(nowUtc.AddMinutes(longestPrep));

        return order;
    }

    /// <summary>
    /// Decides whether this caller may order here, and creates the order in their name.
    /// </summary>
    /// <returns>The order, and who owns its lines - null when it belongs to the table.</returns>
    private async Task<(TabOrder Order, Guid? OrdererId)> AuthoriseAndCreateAsync(
        Tab tab,
        PlaceOrderCommand command,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (actor.Type == ActorType.Staff)
        {
            var staffId = RequireStaff("Place an order");
            await branchGuard.RequireAsync(
                staffId, tab.BranchId, "Acting on this tab", "tab", cancellationToken);

            // Here, after the replay lookup and the caller's kind, not before: a retry answers with
            // the order it already placed whatever its body says now, and a phone naming anybody is
            // refused as a phone (403) before the shape of what it named is looked at.
            RequireNoGuestOnASharedLine(command.Items);

            // The order's guest and each line's own, by one rule: somebody approved on this tab.
            // The order's first, then the lines in order, so a refusal names the first field at fault.
            if (command.OnBehalfOfParticipantId is { } onBehalfOf)
            {
                RequireNameable(tab, onBehalfOf, "onBehalfOfParticipantId");
            }

            for (var i = 0; i < command.Items.Count; i++)
            {
                if (command.Items[i].ParticipantId is { } named)
                {
                    RequireNameable(tab, named, $"items[{i}].participantId");
                }
            }

            var order = TabOrder.PlacedByStaffMember(
                tab.Id, staffId, nowUtc, command.OnBehalfOfParticipantId, command.ClientCommandId);

            return (order, command.OnBehalfOfParticipantId);
        }

        // A diner. The participant id comes from their own token, never from the body: a phone
        // must not be able to order in somebody else's name by sending their id.
        //
        // From ParticipantId, not DinerUserId. This line read DinerUserId until Prompt 11, and
        // ClaimsCurrentActor returns null for that unless the principal is a Diner - so with real
        // auth every participant token fell straight through to the refusal below, and the one
        // feature this product is built around had never functioned outside a test. The test double
        // put the participant id into DinerUserId, which production never does, so 522 green tests
        // said otherwise. See docs/auth.md and CurrentActorContract.
        var participantId = actor.ParticipantId
                            ?? throw new TabPermissionException("Ordering", "somebody on this tab");

        var participant = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                          ?? throw new TabPermissionException("Ordering", "somebody on this tab");

        // A line in somebody's name is the order-level field one line at a time, and refused the
        // same way - their own id included, since that is what ordering from this phone already is.
        if (command.OnBehalfOfParticipantId is not null || command.Items.Any(i => i.ParticipantId is not null))
        {
            throw new TabPermissionException("Ordering on somebody else's behalf", "staff");
        }

        if (!TabPermissions.MayOrder(participant.Status, participant.CanOrder, tab.Status))
        {
            throw new TabPermissionException(
                "Adding items to this tab",
                participant.Status == ParticipantStatus.PendingApproval
                    ? "guests the host has approved - you are still waiting to be let on"
                    : "guests the host has allowed to order");
        }

        return (TabOrder.PlacedByDiner(tab.Id, participantId, nowUtc, command.ClientCommandId), participantId);
    }

    /// <summary>
    /// Refuses a guest a waiter may not put an order, or one line of one, in the name of.
    /// </summary>
    /// <remarks>
    /// One check for <c>onBehalfOfParticipantId</c> and every line's <c>participantId</c>, so the two
    /// cannot drift: somebody who is not on this tab - made up, or at another table - is not found;
    /// somebody still waiting on the host is not on the tab yet, and somebody the host took off it is
    /// not on it any more. Each refusal names the field and the guest, so a tablet can tell it from
    /// the other refusals that share its status and code.
    /// </remarks>
    private static void RequireNameable(Tab tab, Guid participantId, string field)
    {
        var named = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                    ?? throw new NamedGuestNotOnTabException(tab.Id, participantId, field);

        if (named.Status != ParticipantStatus.Approved)
        {
            throw new NamedGuestNotOrderableException(participantId, named.Status, field);
        }
    }

    /// <summary>
    /// Refuses a line that is both the table's and one guest's.
    /// </summary>
    /// <remarks>
    /// The rule the order-level field already follows, made explicit for a line. The order's guest
    /// owns the lines that have an owner, and a shared line has none: an order for Narine with the
    /// table's bottle on it splits the bottle. A line that names a guest <b>and</b> says it is
    /// shared contradicts itself, and keeping either half quietly bills somebody for what the waiter
    /// did not mean - so it is refused, naming the line, before any guest it names is looked up.
    /// </remarks>
    private static void RequireNoGuestOnASharedLine(IReadOnlyList<OrderItemInput> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is { IsShared: true, ParticipantId: not null })
            {
                throw new ArgumentException(
                    "A shared line belongs to the table, so it cannot also be put in one guest's name. "
                    + "Send it shared, or for that guest.",
                    $"items[{i}].participantId");
            }
        }
    }

    // ---------------------------------------------------------------- voiding

    public async Task<OrderView> VoidLineAsync(
        VoidLineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = RequireStaff("Void a line");

        var tab = await ledger.LoadForWriteAsync(command.TabId, cancellationToken);
        await branchGuard.RequireAsync(
                staffId, tab.BranchId, "Acting on this tab", "tab", cancellationToken);

        var order = tab.Orders.FirstOrDefault(o => o.Lines.Any(l => l.Id == command.LineId))
                    ?? throw new KeyNotFoundException($"Line {command.LineId} is not on tab {command.TabId}.");

        var line = order.Lines.First(l => l.Id == command.LineId);

        if (line.IsVoided)
        {
            // Already gone. Answering with the tab as it stands is what a second tap on a flaky
            // connection should get, not an error about a state the caller was trying to reach.
            return await BuildOrderViewAsync(order.Id, wasReplay: true, cancellationToken);
        }

        // Money has already changed hands against this tab, so taking an item off it is a refund -
        // a different thing, with its own rail and its own conversation with a manager.
        if (tab.Payments.Any(p => p.Status is PaymentStatus.Succeeded or PaymentStatus.Reserved))
        {
            throw new LineAlreadyPaidException(tab.Id, line.Id);
        }

        line.Void(clock.UtcNow, command.Reason, staffId);

        ledger.Append(tab.Id, TabEventType.LineVoided, new
        {
            lineId = line.Id,
            orderId = order.Id,
            name = line.NameSnapshot,
            reason = command.Reason,
            voidedByStaffId = staffId,
        });

        await ledger.SaveWithTotalsAsync(tab, cancellationToken);

        logger.LogInformation(
            "Line {LineId} ({Name}) voided on tab {TabId} by staff {StaffId}: {Reason}",
            line.Id, line.NameSnapshot, tab.Id, staffId, command.Reason);

        return await BuildOrderViewAsync(order.Id, wasReplay: false, cancellationToken);
    }

    // ---------------------------------------------------------------- adjusting

    public async Task<AdjustmentView> AddAdjustmentAsync(
        AddAdjustmentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = RequireManager("Discount or comp");

        var tab = await ledger.LoadForWriteAsync(command.TabId, cancellationToken);
        await branchGuard.RequireAsync(
                staffId, tab.BranchId, "Acting on this tab", "tab", cancellationToken);

        if (command.TabOrderLineId is { } lineId
            && !tab.Orders.Any(o => o.Lines.Any(l => l.Id == lineId)))
        {
            throw new KeyNotFoundException($"Line {lineId} is not on tab {command.TabId}.");
        }

        var adjustment = command.Percent is { } percent
            ? TabAdjustment.ByPercent(
                tab.Id, command.TabOrderLineId, command.Kind, percent, command.Reason, staffId, clock.UtcNow)
            : TabAdjustment.ByAmount(
                tab.Id, command.TabOrderLineId, command.Kind,
                command.AmountAmd ?? throw new ArgumentException(
                    "An adjustment is either a percentage or a flat amount.", nameof(command)),
                command.Reason, staffId, clock.UtcNow);

        // Before the Add, not after: Compute reads the change tracker, so an adjustment already
        // sitting in Local is already reflected and "what did this take off" would come out zero.
        var before = ledger.Compute(tab).SubtotalAmd;

        db.TabAdjustments.Add(adjustment);

        ledger.Append(tab.Id, TabEventType.AdjustmentAdded, new
        {
            adjustmentId = adjustment.Id,
            tabOrderLineId = adjustment.TabOrderLineId,
            kind = (int)adjustment.Kind,
            percent = adjustment.Percent,
            amountAmd = adjustment.AmountAmd,
            reason = adjustment.Reason,
        });

        var bill = await ledger.SaveWithTotalsAsync(tab, cancellationToken);

        logger.LogInformation(
            "{Kind} applied to tab {TabId} by manager {StaffId}: {Reason}. Subtotal {Before} to {After}.",
            adjustment.Kind, tab.Id, staffId, adjustment.Reason, before, bill.SubtotalAmd);

        return ToView(adjustment, before - bill.SubtotalAmd);
    }

    public async Task<AdjustmentView> VoidAdjustmentAsync(
        Guid adjustmentId,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireManager("Reverse an adjustment");

        var adjustment = await db.TabAdjustments
            .FirstOrDefaultAsync(a => a.Id == adjustmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Adjustment {adjustmentId} was not found.");

        var tab = await ledger.LoadForWriteAsync(adjustment.TabId, cancellationToken);
        await branchGuard.RequireAsync(
                staffId, tab.BranchId, "Acting on this tab", "tab", cancellationToken);

        adjustment.Void(clock.UtcNow, staffId);

        ledger.Append(tab.Id, TabEventType.AdjustmentVoided, new
        {
            adjustmentId = adjustment.Id,
            reason = adjustment.Reason,
        });

        await ledger.SaveWithTotalsAsync(tab, cancellationToken);

        return ToView(adjustment, 0L);
    }

    // ---------------------------------------------------------------- the kitchen rail

    public async Task<KitchenOrderView> MoveOrderStatusAsync(
        Guid orderId,
        TabOrderStatus next,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Move an order");

        // Who sent it, read with the order: the name alone, projected beside the tracked order, so the
        // answer below costs no second round trip and nothing of the staff row but the name is loaded.
        var loaded = await db.TabOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Include(o => o.Tab).ThenInclude(t => t.DiningTable)
            .Include(o => o.Tab).ThenInclude(t => t.Branch).ThenInclude(b => b.Venue)
            .Include(o => o.Tab).ThenInclude(t => t.Participants)
            .Where(o => o.Id == orderId)
            .Select(o => new
            {
                Order = o,
                PlacedByName = o.PlacedByStaff != null ? o.PlacedByStaff.FullName : null,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Order {orderId} was not found.");

        var order = loaded.Order;

        await branchGuard.RequireAsync(
            staffId, order.Tab.BranchId, "Acting on this tab", "tab", cancellationToken);
        RequireKitchenMayMake(order.Status, next);

        var from = order.Status;
        order.MoveTo(next);

        // Food is up. Per-branch and off by default: noisy in a cafe where a waiter carries the
        // plate ten feet, useful in a canteen where the diner collects it. Defaulting it on would
        // train a city to switch our notifications off, taking the reminder and the nudge with them.
        var recipients = next == TabOrderStatus.Ready && order.Tab.Branch.NotifyOnOrderReady
            ? ReadyRecipients(order)
            : [];

        // The place was read above, and the diner can have deleted the account since - which cut the
        // place loose and emptied their feed. The entry and its push are the person's, so they are
        // written under the account's lock (DinerAccountLock), held to the save below, and only for a
        // live account: a deletion that comes after waits for this commit and removes the entry.
        await using var feedTransaction = recipients.Count == 0
            ? null
            : await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var (recipient, dinerUserId) in recipients)
        {
            if (!await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken))
            {
                continue;
            }

            outbox.Enqueue(
                OutboxMessageTypes.OrderReady,
                new
                {
                    tabId = order.TabId,
                    participantId = recipient.Id,
                    dinerUserId,
                    tableLabel = order.Tab.DiningTable.Label,
                },
                clock.UtcNow,

                // The order's own guest keeps the key a single-owner order always had; a guest with
                // a line of their own on it gets one of their own beside it.
                OutboxMessageTypes.KeyFor(
                    "order", order.Id, recipient.Id == order.OwningParticipantId ? "ready" : $"ready:{recipient.Id}"));

            // Into the diner's feed beside the push, saved with it by the ledger below (K12).
            DinerNotices.OrderReady(
                db,
                dinerUserId,
                order.Tab.BranchId,
                order.Tab.Branch.Venue?.Name ?? order.Tab.Branch.Name,
                order.Tab.Branch.Name,
                order.TabId,
                order.Id,
                order.Tab.DiningTable.Label,
                clock.UtcNow);
        }

        ledger.Append(order.TabId, TabEventType.OrderStatusChanged, new
        {
            orderId = order.Id,
            fromStatus = (int)from,
            toStatus = (int)next,
            byStaffId = staffId,
        });

        // The rail does not touch money, so no totals recomputation - there is nothing on this path
        // that can change what the tab owes. It still goes through the ledger, because it appends an
        // event and the numbering is what the stream rests on.
        await ledger.SaveAppendedAsync(cancellationToken);

        if (feedTransaction is not null)
        {
            await feedTransaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Order {OrderId} moved {From} to {To} by staff {StaffId}.", order.Id, from, next, staffId);

        return ToKitchenView(order, order.Tab.DiningTable.Label, loaded.PlacedByName, clock.UtcNow);
    }

    /// <summary>
    /// Who to tell that an order is ready: everybody with an account it is for.
    /// </summary>
    /// <remarks>
    /// The diner who ordered it from their phone, as it always was, and each guest who owns a line of
    /// it that is still on the bill - named on the line, or by the order for a line that names nobody
    /// (<see cref="TabOrder.OwnerOf"/>). One round for the table can be three people's dinners, and
    /// each of them is waiting for theirs. The order-level guest is <b>not</b> told as such: an order
    /// "for Ani" whose every line the waiter put on Bob, or whose lines for Ani were all voided, has
    /// nothing of hers left to be ready. One entry per account, in account order, which is the order
    /// the account locks are then taken in.
    /// </remarks>
    private static List<(TabParticipant Participant, Guid DinerUserId)> ReadyRecipients(TabOrder order)
    {
        var owners = order.Lines
            .Where(l => !l.IsVoided)
            .Select(order.OwnerOf)
            .Prepend(order.PlacedByParticipantId)
            .OfType<Guid>()
            .ToHashSet();

        return
        [
            .. order.Tab.Participants
                .Where(p => owners.Contains(p.Id) && p.UserId is not null)
                .Select(p => (Participant: p, DinerUserId: p.UserId!.Value))
                .GroupBy(r => r.DinerUserId)
                .Select(g => g.OrderBy(r => r.Participant.Id != order.OwningParticipantId).First())
                .OrderBy(r => r.DinerUserId),
        ];
    }

    /// <summary>
    /// The Kitchen role cooks and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>InKitchen -> Ready</c> is the whole of what that role does. Serving is a floor action and
    /// belongs to whoever carried the plate; letting the kitchen mark food served would make the
    /// floor screen say a table has its order when nobody has walked it over.
    /// </remarks>
    private void RequireKitchenMayMake(TabOrderStatus from, TabOrderStatus to)
    {
        if (actor.Role != StaffRole.Kitchen)
        {
            return;
        }

        if (from != TabOrderStatus.InKitchen || to != TabOrderStatus.Ready)
        {
            throw new StaffPermissionException(
                $"Moving an order from {from} to {to}", StaffRole.Kitchen, StaffRole.Waiter);
        }
    }

    public async Task<IReadOnlyList<KitchenOrderView>> GetBranchOrdersAsync(
        Guid branchId,
        TabOrderStatus? status,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Read the order queue");
        await branchGuard.RequireAsync(
            staffId, branchId, "Acting on this tab", "tab", cancellationToken);

        var nowUtc = clock.UtcNow;

        var orders = await db.TabOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Where(o => o.Tab.BranchId == branchId)
            .Where(o => status == null
                ? o.Status != TabOrderStatus.Served && o.Status != TabOrderStatus.Voided
                : o.Status == status)
            .OrderBy(o => o.PlacedAtUtc)
            .Select(o => new
            {
                Order = o,
                TableLabel = o.Tab.DiningTable.Label,

                // Who sent it, joined in this one query - never looked up per ticket. The name and
                // nothing else of the staff row; null for a diner's own order.
                PlacedByName = o.PlacedByStaff != null ? o.PlacedByStaff.FullName : null,
            })
            .ToListAsync(cancellationToken);

        return [.. orders.Select(row => ToKitchenView(row.Order, row.TableLabel, row.PlacedByName, nowUtc))];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The order this command already created on this tab, if it has one.</summary>
    /// <remarks>
    /// <para>
    /// Nullable on purpose. <c>Guid</c> is a value type, so a plain <c>Guid</c> return would make
    /// the caller's <c>is { }</c> pattern match <c>Guid.Empty</c> as well - and every first-time
    /// order would be treated as a replay of an order that does not exist.
    /// </para>
    /// <para>
    /// <b>Scoped to the tab, and to who may be retrying.</b> This matched the command id alone,
    /// across every tab there is, so another tab presenting the same id - and any client that left
    /// the field out, binding <c>Guid.Empty</c> - got the first order back with that tab's totals. A
    /// phone's replay is the same participant sending the same order again; another participant
    /// presenting the id is a client bug, and the honest answer is that it is taken.
    /// </para>
    /// <para>
    /// <b>A staff replay is any staff-placed order on the tab, whichever waiter placed it.</b> The
    /// tablet queues per device, not per waiter, and sends under whoever is signed in when the
    /// connection returns - so an order whose answer was lost comes back under the next waiter's
    /// PIN. Matching the waiter as well answered that with 409, which the tablet files as refused,
    /// and the next step was keying the order in again and the kitchen cooking it twice. The
    /// branch check runs first, because it is now the only thing between the order and a staff
    /// caller who holds its tab id and command id.
    /// </para>
    /// </remarks>
    /// <exception cref="ClientCommandIdAlreadyUsedException">
    /// The order this command id names is not one this caller can be retrying: another
    /// participant's, a phone's when the caller is staff, or staff's when the caller is a phone.
    /// </exception>
    /// <exception cref="Yalla.Domain.Staff.StaffBranchScopeException">
    /// A staff caller, and the tab is not on a branch they may act on.
    /// </exception>
    private async Task<Guid?> FindReplayAsync(Guid tabId, Guid clientCommandId, CancellationToken cancellationToken)
    {
        var existing = await db.TabOrders
            .AsNoTracking()
            .Where(o => o.TabId == tabId && o.ClientCommandId == clientCommandId)
            .Select(o => new { o.Id, o.PlacedByParticipantId, o.PlacedByStaffId, o.Tab.BranchId })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        bool isReplay;

        if (actor.Type == ActorType.Staff)
        {
            await branchGuard.RequireAsync(
                RequireStaff("Place an order"), existing.BranchId, "Acting on this tab", "tab", cancellationToken);

            isReplay = existing.PlacedByStaffId is not null;
        }
        else
        {
            isReplay = existing.PlacedByParticipantId is { } participantId && participantId == actor.ParticipantId;
        }

        return isReplay
            ? existing.Id
            : throw new ClientCommandIdAlreadyUsedException(
                clientCommandId,
                "That clientCommandId already belongs to another order on this tab. Generate a fresh "
                + "one per order, and reuse it only when retrying that same order.");
    }

    private async Task<OrderView> BuildOrderViewAsync(
        Guid orderId,
        bool wasReplay,
        CancellationToken cancellationToken)
    {
        var order = await db.TabOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Include(o => o.Tab)
            .FirstAsync(o => o.Id == orderId, cancellationToken);

        var tab = order.Tab;
        var totalsVisible = await CallerMaySeeTableTotalAsync(order.TabId, cancellationToken);

        return new OrderView(
            order.Id,
            order.TabId,
            order.Status,
            order.PlacedByParticipantId,
            order.PlacedByStaffId,
            order.OnBehalfOfParticipantId,
            order.PlacedAtUtc,
            order.EstimatedReadyAtUtc,
            [.. order.Lines.Select(l => ToLineView(order, l))],
            totalsVisible,
            totalsVisible
                ? new TabTotalsSnapshot(
                    tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd)
                : null,
            await ledger.MaxSequenceAsync(order.TabId, cancellationToken),
            wasReplay);
    }

    /// <summary>
    /// Whether whoever is calling may be answered with the tab's totals.
    /// </summary>
    /// <remarks>
    /// Staff always: they are not participants and the host's flags are not about them. A diner by
    /// <see cref="TabPermissions.MaySeeTableTotal"/> on their own row, the rule that decides
    /// <c>tableTotalVisible</c> on their tab - the answer to placing a coffee used to carry the whole
    /// table's bill to a guest the host had hidden it from. Anybody else gets none: there is no
    /// caller here the rule has not been asked about.
    /// </remarks>
    private async Task<bool> CallerMaySeeTableTotalAsync(Guid tabId, CancellationToken cancellationToken)
    {
        if (actor.Type == ActorType.Staff)
        {
            return true;
        }

        if (actor.ParticipantId is not { } participantId)
        {
            return false;
        }

        var me = await db.TabParticipants
            .AsNoTracking()
            .Where(p => p.TabId == tabId && p.Id == participantId)
            .Select(p => new { p.Status, p.CanSeeTableTotal })
            .FirstOrDefaultAsync(cancellationToken);

        return me is not null && TabPermissions.MaySeeTableTotal(me.Status, me.CanSeeTableTotal);
    }

    private static OrderLineView ToLineView(TabOrder order, TabOrderLine line) =>
        new(
            line.Id,
            line.MenuItemId,
            line.NameSnapshot,
            line.UnitPriceAmdSnapshot,
            line.Quantity,
            line.LineTotalAmd,
            line.IsShared,
            line.IsTableAttributed,
            line.Note,
            [.. line.Shares.Select(s => s.TabParticipantId)],
            OnBehalfOf(order, line));

    /// <summary>
    /// The guest a waiter keyed this line in for, as every read reports it: the line's own, else the
    /// order's - and nobody on a split line, which belongs to the table whatever the order says.
    /// </summary>
    private static Guid? OnBehalfOf(TabOrder order, TabOrderLine line) =>
        line.IsSplitAcrossParticipants ? null : line.OnBehalfOfParticipantId ?? order.OnBehalfOfParticipantId;

    private static KitchenOrderView ToKitchenView(
        TabOrder order, string tableLabel, string? placedByName, DateTime nowUtc) =>
        new(
            order.Id,
            order.TabId,
            tableLabel,
            order.Status,
            order.PlacedAtUtc,
            order.EstimatedReadyAtUtc,
            (int)Math.Max(0d, (nowUtc - order.PlacedAtUtc).TotalMinutes),
            [.. order.Lines.Select(l => ToLineView(order, l))],
            placedByName);

    private static AdjustmentView ToView(TabAdjustment adjustment, long reductionAmd) =>
        new(
            adjustment.Id,
            adjustment.TabOrderLineId,
            adjustment.Kind,
            adjustment.Percent,
            adjustment.AmountAmd,
            reductionAmd,
            adjustment.Reason,
            adjustment.CreatedAtUtc,
            !adjustment.IsActive);

    private Guid RequireStaff(string operation) =>
        actor.Type == ActorType.Staff && actor.StaffMemberId is { } id
            ? id
            : throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);

    private Guid RequireManager(string operation)
    {
        var staffId = RequireStaff(operation);

        // A decision about money rather than about the floor, so it needs the role that answers
        // for the takings at the end of the evening.
        if (actor.Role is not (StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        return staffId;
    }

}
