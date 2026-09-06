using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
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

        if (await FindReplayAsync(command.ClientCommandId, cancellationToken) is { } replay)
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
            }),
        });

        // The order, its lines and the event, in one transaction - and deliberately nothing else.
        // The tab's totals are a cache, and the authoritative total is the sum of the lines, so the
        // insert does not need to touch the tab row to be correct. Not touching it is what makes
        // ten simultaneous orders safe: they write nothing in common, so there is no row version to
        // lose and no concurrency error for a diner to see. See TabLedger and docs/tab-totals.md.
        await ledger.SaveAppendedAsync(cancellationToken);

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
            if (!item.IsAvailable)
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
            // arithmetic and completely different facts.
            var tableAttributed = ordererId is null && !input.IsShared;

            var line = order.AddLine(
                item.Id, item.Name, item.PriceAmd, input.Quantity,
                isShared: input.IsShared,
                isTableAttributed: tableAttributed,
                note: input.Note);

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
            await RequireBranchAsync(staffId, tab.BranchId, cancellationToken);

            if (command.OnBehalfOfParticipantId is { } onBehalfOf)
            {
                var named = tab.Participants.FirstOrDefault(p => p.Id == onBehalfOf)
                            ?? throw new KeyNotFoundException(
                                $"Participant {onBehalfOf} is not on tab {tab.Id}.");

                if (named.Status != ParticipantStatus.Approved)
                {
                    throw new DomainStateException(
                        "That guest is not on the tab yet, so an order cannot be put in their name.");
                }
            }

            var order = TabOrder.PlacedByStaffMember(
                tab.Id, staffId, nowUtc, command.OnBehalfOfParticipantId, command.ClientCommandId);

            return (order, command.OnBehalfOfParticipantId);
        }

        // A diner. The participant id comes from their own token, never from the body: a phone
        // must not be able to order in somebody else's name by sending their id.
        var participantId = actor.DinerUserId
                            ?? throw new TabPermissionException("Ordering", "somebody on this tab");

        var participant = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                          ?? throw new TabPermissionException("Ordering", "somebody on this tab");

        if (command.OnBehalfOfParticipantId is not null)
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

    // ---------------------------------------------------------------- voiding

    public async Task<OrderView> VoidLineAsync(
        VoidLineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = RequireStaff("Void a line");

        var tab = await ledger.LoadForWriteAsync(command.TabId, cancellationToken);
        await RequireBranchAsync(staffId, tab.BranchId, cancellationToken);

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
        await RequireBranchAsync(staffId, tab.BranchId, cancellationToken);

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
        await RequireBranchAsync(staffId, tab.BranchId, cancellationToken);

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

        var order = await db.TabOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Shares)
            .Include(o => o.Tab).ThenInclude(t => t.DiningTable)
            .Include(o => o.Tab).ThenInclude(t => t.Branch)
            .Include(o => o.Tab).ThenInclude(t => t.Participants)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Order {orderId} was not found.");

        await RequireBranchAsync(staffId, order.Tab.BranchId, cancellationToken);
        RequireKitchenMayMake(order.Status, next);

        var from = order.Status;
        order.MoveTo(next);

        // Food is up. Per-branch and off by default: noisy in a cafe where a waiter carries the
        // plate ten feet, useful in a canteen where the diner collects it. Defaulting it on would
        // train a city to switch our notifications off, taking the reminder and the nudge with them.
        if (next == TabOrderStatus.Ready && order.Tab.Branch.NotifyOnOrderReady)
        {
            var recipient = order.OwningParticipantId is { } participantId
                ? order.Tab.Participants.FirstOrDefault(p => p.Id == participantId)
                : null;

            if (recipient?.UserId is { } dinerUserId)
            {
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
                    OutboxMessageTypes.KeyFor("order", order.Id, "ready"));
            }
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

        logger.LogInformation(
            "Order {OrderId} moved {From} to {To} by staff {StaffId}.", order.Id, from, next, staffId);

        return ToKitchenView(order, order.Tab.DiningTable.Label, clock.UtcNow);
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
        await RequireBranchAsync(staffId, branchId, cancellationToken);

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
            })
            .ToListAsync(cancellationToken);

        return [.. orders.Select(row => ToKitchenView(row.Order, row.TableLabel, nowUtc))];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The order this command already created, if it has one.</summary>
    /// <remarks>
    /// Nullable on purpose. <c>Guid</c> is a value type, so a plain <c>Guid</c> return would make
    /// the caller's <c>is { }</c> pattern match <c>Guid.Empty</c> as well - and every first-time
    /// order would be treated as a replay of an order that does not exist.
    /// </remarks>
    private Task<Guid?> FindReplayAsync(Guid clientCommandId, CancellationToken cancellationToken) =>
        db.TabOrders
            .AsNoTracking()
            .Where(o => o.ClientCommandId == clientCommandId)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);

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

        return new OrderView(
            order.Id,
            order.TabId,
            order.Status,
            order.PlacedByParticipantId,
            order.PlacedByStaffId,
            order.OnBehalfOfParticipantId,
            order.PlacedAtUtc,
            order.EstimatedReadyAtUtc,
            [.. order.Lines.Select(ToLineView)],
            new TabTotalsSnapshot(
                tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd),
            await ledger.MaxSequenceAsync(order.TabId, cancellationToken),
            wasReplay);
    }

    private static OrderLineView ToLineView(TabOrderLine line) =>
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
            [.. line.Shares.Select(s => s.TabParticipantId)]);

    private static KitchenOrderView ToKitchenView(TabOrder order, string tableLabel, DateTime nowUtc) =>
        new(
            order.Id,
            order.TabId,
            tableLabel,
            order.Status,
            order.PlacedAtUtc,
            order.EstimatedReadyAtUtc,
            (int)Math.Max(0d, (nowUtc - order.PlacedAtUtc).TotalMinutes),
            [.. order.Lines.Select(ToLineView)]);

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
        if (actor.Role is not (StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        return staffId;
    }

    /// <summary>
    /// Confines a staff member to their own branch.
    /// </summary>
    /// <remarks>
    /// The route-level <c>BranchScoped</c> policy cannot help here: these routes are addressed by
    /// tab or by order id, so there is no branch route value to compare a claim against and the
    /// policy would fail closed on every one of them. An owner or manager is venue-scoped rather
    /// than branch-scoped, which is the point of that account.
    /// </remarks>
    private async Task RequireBranchAsync(Guid staffId, Guid branchId, CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException("Acting on this tab", actor.Role, StaffRole.Waiter);
        }

        if (staff.Role == StaffRole.PlatformAdmin)
        {
            return;
        }

        if (staff.BranchId == branchId)
        {
            return;
        }

        var venueOwnsBranch = await db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.VenueId == staff.VenueId, cancellationToken);

        if (venueOwnsBranch && staff.Role is StaffRole.Owner or StaffRole.Manager)
        {
            return;
        }

        throw new DomainStateException(
            "That tab belongs to another branch. Staff act on the branch they are enrolled at, and "
            + "an owner or manager on the branches of their own venue.");
    }
}
