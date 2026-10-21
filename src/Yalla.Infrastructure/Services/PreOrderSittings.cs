using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A sent pre-order arriving on the party's tab: the held kitchen ticket becomes the first order on their bill.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a held ticket at all.</b> A tab hangs off a sitting, and a sitting is a party at the table. Opening one when
/// the waiter taps "send to kitchen" twenty minutes before anybody arrives would mark the table occupied and the booking
/// seated; making an order that belongs to no tab would touch every bill, split and payment query in the product. So
/// until the party sits the pre-order itself is the ticket the kitchen works from, and nothing is owed - a party that
/// never comes is never billed.
/// </para>
/// <para>
/// <b>When they sit</b> - a waiter seats the booking or the held party, or the guest opens it with the booking code -
/// the seating calls <see cref="OpenTabWithOrder"/> inside its own lock and save: the sitting gets a tab as a waiter's
/// "Open tab" makes one (nobody on it, admitted, the first phone becomes host), and the pre-order becomes an order on it
/// with <b>the same id</b>, the locked prices, the waiter who sent it as its sender, and whatever the kitchen has done
/// with it since. The kitchen screen's ticket gains a <c>tabId</c> and keeps its identity.
/// </para>
/// </remarks>
internal static class PreOrderSittings
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// When the booking being seated has a held pre-order: opens the new sitting's tab and puts it on, in the caller's
    /// unit of work. Does nothing otherwise - nor for a held ticket the kitchen voided, which is never billed.
    /// </summary>
    /// <returns>The tab opened, or null when there was nothing to put on one.</returns>
    public static async Task<Guid?> PutHeldOnNewSittingAsync(
        YallaDbContext db,
        Reservation reservation,
        TableSession session,
        decimal serviceChargePercent,
        ActorType seatedBy,
        Guid? seatingStaffId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var preOrder = await db.PreOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken);

        // A held ticket the kitchen voided before the party sat is not food anybody will eat or pay for: the party
        // sits with nothing on a tab, as any booking without a pre-order does.
        if (preOrder is not { IsHeld: true, IsVoidedHeld: false } || session.TabId is not null)
        {
            return null;
        }

        // Admitted: a waiter sent this order and the party is on a booking - nothing here is a stranger's scan. Opened
        // by staff when staff seated them; the booker's own when they opened it with the booking code.
        return seatedBy == ActorType.Staff
            ? OpenTabWithOrder(db, preOrder, session, serviceChargePercent, TabAdmissionReason.OpenedByStaff, seatingStaffId, nowUtc)
            : OpenTabWithOrder(db, preOrder, session, serviceChargePercent, TabAdmissionReason.Booker, null, nowUtc);
    }

    /// <summary>
    /// Opens the sitting's tab with the sent pre-order on it, in the caller's unit of work: the tab admitted and with
    /// nobody on it, the order with the pre-order's id, its two events numbered from the start of the new tab's stream -
    /// told as the sending waiter's, who placed it - and its totals.
    /// </summary>
    public static Guid OpenTabWithOrder(
        YallaDbContext db,
        PreOrder preOrder,
        TableSession session,
        decimal serviceChargePercent,
        TabAdmissionReason admittedBecause,
        Guid? admittedByStaffId,
        DateTime nowUtc)
    {
        var tab = new Tab(
            session.BranchId,
            session.DiningTableId,
            session.Id,
            nowUtc,
            serviceChargePercent,
            admittedBecause,
            admittedByStaffId: admittedByStaffId);

        session.AttachTab(tab.Id);
        db.Tabs.Add(tab);

        var order = BuildOrder(preOrder, tab.Id);
        db.TabOrders.Add(order);

        var sequence = 1L;

        foreach (var (type, payload) in Events(order))
        {
            var record = new TabEvent(
                tab.Id, type, JsonSerializer.Serialize(payload, PayloadJson), ActorType.Staff, preOrder.SentByStaffId, nowUtc);
            record.PlaceAt(sequence++);
            db.TabEvents.Add(record);
        }

        // Nobody is on the tab yet, so the lines are the table's and there is nothing paid: the bill is the lines, and
        // the service charge on them, by the one arithmetic every bill uses.
        var bill = TabBilling.Compute(
            [.. order.Lines.Select(l => new BillingLine(l.Id, null, l.UnitPriceAmdSnapshot, l.Quantity, l.IsVoided, l.IsSplitAcrossParticipants, []))],
            [],
            [],
            tab.ServiceChargePercentSnapshot,
            0L);

        tab.ApplyComputedTotals(bill.SubtotalAmd, bill.ServiceChargeAmd, bill.PaidAmd);
        preOrder.MarkAttached(nowUtc);

        return tab.Id;
    }

    /// <summary>
    /// Puts the sent pre-order on a tab the sitting already has, through the ledger, which numbers its events after the
    /// tab's own. The caller saves through the ledger and recomputes the totals after committing, as an order does.
    /// </summary>
    /// <exception cref="TabNotAcceptingOrdersException">The bill has been asked for, or the tab is closed.</exception>
    public static void AddToTab(YallaDbContext db, TabLedger ledger, PreOrder preOrder, Tab tab, DateTime nowUtc)
    {
        if (tab.Status != TabStatus.Open)
        {
            throw new TabNotAcceptingOrdersException(tab.Id, tab.Status);
        }

        var order = BuildOrder(preOrder, tab.Id);
        db.TabOrders.Add(order);

        foreach (var (type, payload) in Events(order))
        {
            ledger.Append(tab.Id, type, payload);
        }

        preOrder.MarkAttached(nowUtc);
    }

    /// <summary>
    /// The order: the pre-order's id, placed by the waiter who sent it when they sent it, its lines the table's at their
    /// locked prices, and the kitchen's progress replayed onto it through the rail's own transitions.
    /// </summary>
    private static TabOrder BuildOrder(PreOrder preOrder, Guid tabId)
    {
        var order = TabOrder.FromPreOrder(preOrder.Id, tabId, preOrder.SentByStaffId!.Value, preOrder.SentAtUtc!.Value);

        foreach (var line in preOrder.Lines.OrderBy(l => l.Position))
        {
            // The table's, like a waiter's order keyed for nobody in particular: nobody is on the tab yet to own it,
            // and the split settles it as it settles any table line.
            order.AddLine(
                line.MenuItemId, line.NameSnapshot, line.UnitPriceAmdSnapshot, line.Quantity,
                isTableAttributed: true, note: line.Note);
        }

        foreach (var step in StepsTo(preOrder.KitchenStatus ?? TabOrderStatus.InKitchen))
        {
            order.MoveTo(step);
        }

        return order;
    }

    /// <summary>The rail's moves from New to where the held ticket got.</summary>
    private static IEnumerable<TabOrderStatus> StepsTo(TabOrderStatus reached) => reached switch
    {
        TabOrderStatus.InKitchen => [TabOrderStatus.InKitchen],
        TabOrderStatus.Ready => [TabOrderStatus.InKitchen, TabOrderStatus.Ready],
        TabOrderStatus.Served => [TabOrderStatus.InKitchen, TabOrderStatus.Ready, TabOrderStatus.Served],
        TabOrderStatus.Voided => [TabOrderStatus.Voided],
        _ => [],
    };

    /// <summary>What a phone reading the tab's stream learns: the order placed, then where it is on the rail.</summary>
    private static IEnumerable<(TabEventType Type, object Payload)> Events(TabOrder order)
    {
        yield return (TabEventType.OrderPlaced, new
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
                onBehalfOfParticipantId = (Guid?)null,
            }),
        });

        if (order.Status != TabOrderStatus.New)
        {
            yield return (TabEventType.OrderStatusChanged, new
            {
                orderId = order.Id,
                fromStatus = (int)TabOrderStatus.New,
                toStatus = (int)order.Status,
                byStaffId = order.PlacedByStaffId,
            });
        }
    }
}
