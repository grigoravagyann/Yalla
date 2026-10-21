using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reads pre-orders for many bookings at once - the lists' way, not one query per booking - and attaches them to
/// booking views.
/// </summary>
/// <remarks>
/// Two statements whatever the number of bookings: the pre-orders with their lines and their bookings' facts, then the
/// dishes they name (whether each can still be had). Skipped entirely when no booking on the page has one. A static
/// helper over the context, like <see cref="GuestRecords"/>, so every service that answers with a booking can use it
/// without a new constructor argument.
/// </remarks>
internal static class PreOrderReads
{
    /// <summary>The pre-orders of these bookings, by booking id. Bookings without one are absent.</summary>
    public static async Task<IReadOnlyDictionary<Guid, PreOrderView>> ForReservationsAsync(
        YallaDbContext db, IEnumerable<Guid> reservationIds, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var ids = reservationIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, PreOrderView>();
        }

        var rows = await db.PreOrders
            .AsNoTracking()
            .Where(p => ids.Contains(p.ReservationId))
            .Select(p => new Row
            {
                Id = p.Id,
                ReservationId = p.ReservationId,
                BranchId = p.BranchId,
                Note = p.Note,
                UpdatedAtUtc = p.UpdatedAtUtc,
                SentAtUtc = p.SentAtUtc,
                KitchenStatus = p.KitchenStatus,
                TabOrderId = p.TabOrderId,
                NotComingAtUtc = p.NotComingAtUtc,
                BookingStatus = p.Reservation.Status,
                StartUtc = p.Reservation.StartUtc,
                MinutesBefore = p.Reservation.Branch.PreOrderMinutesBefore,

                // Once on the tab, the tab order's own place on the rail is the truth.
                OrderStatus = db.TabOrders.Where(o => o.Id == p.Id).Select(o => (TabOrderStatus?)o.Status).FirstOrDefault(),
                Lines = p.Lines
                    .OrderBy(l => l.Position)
                    .Select(l => new LineRow(
                        l.Id, l.MenuItemId, l.NameSnapshot, l.UnitPriceAmdSnapshot, l.Quantity, l.Note))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new Dictionary<Guid, PreOrderView>();
        }

        var dishes = await DishesAsync(db, rows.SelectMany(r => r.Lines.Select(l => l.MenuItemId)), cancellationToken);

        return rows.ToDictionary(r => r.ReservationId, r => ToView(r, dishes, nowUtc));
    }

    /// <summary>One booking's pre-order, or null.</summary>
    public static async Task<PreOrderView?> ForReservationAsync(
        YallaDbContext db, Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken) =>
        (await ForReservationsAsync(db, [reservationId], nowUtc, cancellationToken)).GetValueOrDefault(reservationId);

    /// <summary>
    /// The views with their pre-orders and - on the guest's own reads (<paramref name="forGuest"/>) - where the branch
    /// takes pre-orders and the booking may still have one, until when the guest may change it. Three statements at
    /// most, whatever the number of views; a staff read skips the third, it offers nobody an editor.
    /// </summary>
    public static async Task<IReadOnlyList<ReservationView>> AttachAsync(
        YallaDbContext db,
        IReadOnlyList<ReservationView> views,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool forGuest = true)
    {
        if (views.Count == 0)
        {
            return views;
        }

        var preOrders = await ForReservationsAsync(db, views.Select(v => v.Id), nowUtc, cancellationToken);

        var mayHaveOne = forGuest
            ? views.Where(v => PreOrderRules.BookingTakesEdits(v.Status) && v.StartUtc > nowUtc).ToList()
            : [];
        var branchIds = mayHaveOne.Select(v => v.BranchId).Distinct().ToList();

        var accepting = branchIds.Count == 0
            ? []
            : await db.Branches
                .AsNoTracking()
                .Where(b => branchIds.Contains(b.Id) && b.AcceptsPreOrders)
                .Select(b => b.Id)
                .ToListAsync(cancellationToken);

        return
        [
            .. views.Select(v => v with
            {
                PreOrder = preOrders.GetValueOrDefault(v.Id),
                PreOrderEditableUntilUtc = accepting.Contains(v.BranchId)
                                           && PreOrderRules.BookingTakesEdits(v.Status)
                                           && v.StartUtc > nowUtc
                    ? PreOrderRules.LockedAtUtc(v.StartUtc)
                    : null,
            }),
        ];
    }

    /// <inheritdoc cref="AttachAsync(YallaDbContext, IReadOnlyList{ReservationView}, DateTime, CancellationToken, bool)"/>
    public static async Task<ReservationView> AttachAsync(
        YallaDbContext db, ReservationView view, DateTime nowUtc, CancellationToken cancellationToken, bool forGuest = true) =>
        (await AttachAsync(db, [view], nowUtc, cancellationToken, forGuest))[0];

    /// <summary>
    /// Whether a dish can go on a pre-order at this branch now, and if not why: what a diner sees on the menu
    /// (complete, this branch's), not sold out, not excluded by the venue. Null when it can.
    /// </summary>
    public static PreOrderItemRefusal? RefusalFor(Dish? dish, Guid branchId) =>
        dish is null || dish.BranchId != branchId || !dish.IsComplete
            ? PreOrderItemRefusal.NotOnMenu
            : !dish.IsAvailable
                ? PreOrderItemRefusal.SoldOut
                : !dish.AvailableForPreOrder
                    ? PreOrderItemRefusal.NotForPreOrder
                    : null;

    /// <summary>The dishes named, with what decides whether each can be pre-ordered. One statement.</summary>
    public static async Task<IReadOnlyDictionary<Guid, Dish>> DishesAsync(
        YallaDbContext db, IEnumerable<Guid> menuItemIds, CancellationToken cancellationToken)
    {
        var ids = menuItemIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Dish>();
        }

        // The item itself, so completeness is decided by MenuItemCompleteness over it - as the diner's menu does -
        // rather than restated in a projection that could drift from it.
        var items = await db.MenuItems
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { Item = m, m.MenuCategory.BranchId })
            .ToListAsync(cancellationToken);

        return items.ToDictionary(
            i => i.Item.Id,
            i => new Dish(
                i.Item.Id,
                i.BranchId,
                i.Item.Name,
                i.Item.PriceAmd,
                MenuItemCompleteness.IsComplete(i.Item),
                i.Item.IsAvailable,
                i.Item.AvailableForPreOrder));
    }

    private static PreOrderView ToView(Row row, IReadOnlyDictionary<Guid, Dish> dishes, DateTime nowUtc)
    {
        var lines = row.Lines
            .Select(l => new PreOrderLineView(
                l.Id,
                l.MenuItemId,
                l.Name,
                l.UnitPriceAmd,
                l.Quantity,
                l.UnitPriceAmd * l.Quantity,
                l.Note,
                RefusalFor(dishes.GetValueOrDefault(l.MenuItemId), row.BranchId) is null))
            .ToList();

        return new PreOrderView(
            row.Id,
            row.ReservationId,
            PreOrderRules.StateOf(row.BookingStatus, row.StartUtc, row.SentAtUtc is not null, row.NotComingAtUtc is not null, nowUtc),
            lines,
            row.Note,
            lines.Sum(l => l.LineTotalAmd),
            PreOrderRules.LockedAtUtc(row.StartUtc),
            PreOrderRules.DueAtUtc(row.StartUtc, row.MinutesBefore),
            row.SentAtUtc,
            row.SentAtUtc is null ? null : row.OrderStatus ?? row.KitchenStatus,
            row.TabOrderId is not null,
            row.NotComingAtUtc,
            row.UpdatedAtUtc);
    }

    /// <summary>A dish as a pre-order needs to judge it.</summary>
    internal sealed record Dish(
        Guid Id,
        Guid BranchId,
        string Name,
        long PriceAmd,
        bool IsComplete,
        bool IsAvailable,
        bool AvailableForPreOrder);

    private sealed record LineRow(Guid Id, Guid MenuItemId, string Name, long UnitPriceAmd, int Quantity, string? Note);

    private sealed class Row
    {
        public Guid Id { get; init; }

        public Guid ReservationId { get; init; }

        public Guid BranchId { get; init; }

        public string? Note { get; init; }

        public DateTime UpdatedAtUtc { get; init; }

        public DateTime? SentAtUtc { get; init; }

        public TabOrderStatus? KitchenStatus { get; init; }

        public Guid? TabOrderId { get; init; }

        public DateTime? NotComingAtUtc { get; init; }

        public ReservationStatus BookingStatus { get; init; }

        public DateTime StartUtc { get; init; }

        public int MinutesBefore { get; init; }

        public TabOrderStatus? OrderStatus { get; init; }

        public List<LineRow> Lines { get; init; } = [];
    }
}
