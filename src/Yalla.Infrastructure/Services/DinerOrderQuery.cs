using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The Orders tab: every order the signed-in diner is on, across every tab they have joined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which orders are "mine".</b> An order placed from the diner's own phone, or keyed in by a waiter
/// on their behalf - through any tab participant row that carries their account - and an order a
/// waiter keyed in for the whole table while the diner was at it, when they may see the table total.
/// The last is how most spoken orders land, and a diner who ate it should see it; the conditions are
/// the tab read's, see <c>MineAsync</c>.
/// </para>
/// <para>
/// <b>The timeline is the tab's event stream</b>, not a column: "confirmed" is when the order was
/// placed, and every later step is an <c>OrderStatusChanged</c> event with the instant it was written.
/// </para>
/// </remarks>
internal sealed class DinerOrderQuery(YallaDbContext db, ICurrentActor actor) : IDinerOrderQuery
{
    public const int MaxOrders = 100;

    public async Task<IReadOnlyList<DinerOrderView>> ListAsync(string? status, CancellationToken cancellationToken = default)
    {
        var filter = status?.Trim().ToLowerInvariant();

        if (filter is not (null or "" or DinerOrderStatuses.ActiveFilter or DinerOrderStatuses.HistoryFilter))
        {
            throw new ArgumentException("status is 'active' or 'history'.", nameof(status));
        }

        var dinerUserId = RequireDiner();
        var mine = await MineAsync(dinerUserId, cancellationToken);

        var takeaway = await TakeawayAsync(dinerUserId, filter, null, cancellationToken);

        if (mine is null)
        {
            return Merge([], takeaway);
        }

        var orders = mine.Orders;

        // Active is "still on the kitchen rail, on a tab that is still open". A settled or written-off
        // tab ends every order on it whatever the rail says: most kitchens never tap "served" (the
        // waiter carries the plate), and without the tab's say the order sat under Active for good.
        orders = filter switch
        {
            DinerOrderStatuses.ActiveFilter => orders.Where(o =>
                (o.Status == TabOrderStatus.New || o.Status == TabOrderStatus.InKitchen || o.Status == TabOrderStatus.Ready)
                && (o.Tab.Status == TabStatus.Open || o.Tab.Status == TabStatus.Closing)),
            DinerOrderStatuses.HistoryFilter => orders.Where(o =>
                o.Status == TabOrderStatus.Served || o.Status == TabOrderStatus.Voided
                || o.Tab.Status == TabStatus.Closed || o.Tab.Status == TabStatus.Abandoned),
            _ => orders,
        };

        return Merge(
            await LoadAsync(orders.OrderByDescending(o => o.PlacedAtUtc).Take(MaxOrders), mine.Seats, cancellationToken),
            takeaway);
    }

    /// <summary>
    /// Dine-in and takeaway on one list: the ones still moving first - an order being cooked is what the Orders tab is
    /// opened for - then newest first, at most <see cref="MaxOrders"/>.
    /// </summary>
    private static List<DinerOrderView> Merge(IReadOnlyList<Loaded> dineIn, IReadOnlyList<Loaded> takeaway) =>
    [
        .. dineIn.Concat(takeaway)
            .OrderByDescending(o => o.Active)
            .ThenByDescending(o => o.View.PlacedAtUtc)
            .Take(MaxOrders)
            .Select(o => o.View),
    ];

    /// <summary>
    /// The diner's takeaway orders as Orders-tab entries: <c>active</c> is Sent, Accepted and Ready; <c>history</c> every
    /// end. One statement for the orders, one for their lines.
    /// </summary>
    private async Task<List<Loaded>> TakeawayAsync(
        Guid dinerUserId, string? filter, Guid? orderId, CancellationToken cancellationToken)
    {
        var orders = db.TakeawayOrders.AsNoTracking().Where(o => o.DinerUserId == dinerUserId);

        if (orderId is { } id)
        {
            orders = orders.Where(o => o.Id == id);
        }

        orders = filter switch
        {
            DinerOrderStatuses.ActiveFilter => orders.Where(o =>
                o.Status == TakeawayStatus.Sent || o.Status == TakeawayStatus.Accepted || o.Status == TakeawayStatus.Ready),
            DinerOrderStatuses.HistoryFilter => orders.Where(o =>
                o.Status != TakeawayStatus.Sent && o.Status != TakeawayStatus.Accepted && o.Status != TakeawayStatus.Ready),
            _ => orders,
        };

        var rows = await orders
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(MaxOrders)
            .Select(o => new
            {
                Order = o,
                Lines = o.Lines.OrderBy(l => l.Position).ToList(),
                Branch = db.Branches
                    .Where(b => b.Id == o.BranchId)
                    .Select(b => new
                    {
                        b.Name,
                        VenueName = b.Venue.Name,
                        b.CoverPhotoId,
                        CoverIsExternal = (bool?)b.CoverPhoto!.IsExternallyHosted,
                        CoverThumbnailPath = b.CoverPhoto!.ThumbnailPath,
                        CoverCardPath = b.CoverPhoto!.CardPath,
                        CoverFullPath = b.CoverPhoto!.FullPath,
                        CoverWidth = b.CoverPhoto!.Width,
                        CoverHeight = b.CoverPhoto!.Height,
                    })
                    .First(),
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r =>
            {
                var o = r.Order;
                var items = r.Lines
                    .Select(l => new DinerOrderItem(
                        l.Id, l.NameSnapshot, l.Quantity, l.UnitPriceAmdSnapshot, l.LineTotalAmd, null, false))
                    .ToList();

                var timeline = new List<DinerOrderTimelineEntry> { new(DinerOrderStatuses.InProgress, o.CreatedAtUtc) };

                Step(timeline, DinerOrderStatuses.Preparing, o.AcceptedAtUtc);
                Step(timeline, DinerOrderStatuses.Ready, o.MarkedReadyAtUtc);
                Step(timeline, DinerOrderStatuses.Completed, o.CollectedAtUtc);
                Step(timeline, DinerOrderStatuses.Cancelled, o.DeclinedAtUtc ?? o.CancelledAtUtc ?? o.NoShowAtUtc);

                var view = new DinerOrderView(
                    o.Id,
                    null,
                    o.BranchId,
                    r.Branch.VenueName,
                    r.Branch.Name,
                    r.Branch.CoverPhotoId is { } photoId
                        ? PhotoView.From(
                            photoId, r.Branch.CoverIsExternal == true, r.Branch.CoverThumbnailPath!, r.Branch.CoverCardPath!,
                            r.Branch.CoverFullPath!, r.Branch.CoverWidth, r.Branch.CoverHeight)
                        : null,
                    Kind: "takeaway",
                    DinerOrderStatuses.FromTakeaway(o.Status),
                    o.Status switch
                    {
                        TakeawayStatus.Sent => TabOrderStatus.New,
                        TakeawayStatus.Accepted => TabOrderStatus.InKitchen,
                        TakeawayStatus.Ready => TabOrderStatus.Ready,
                        TakeawayStatus.Collected => TabOrderStatus.Served,
                        _ => TabOrderStatus.Voided,
                    },
                    TableLabel: string.Empty,
                    PartySize: 0,
                    o.CreatedAtUtc,
                    o.ReadyAtUtc ?? o.RequestedPickupUtc,
                    items.Sum(i => i.LineTotalAmd),
                    items,
                    timeline,
                    CanCancel: o.Status == TakeawayStatus.Sent,
                    new DinerTakeawaySummary(
                        o.Code, o.Status, o.PickupAsap, o.RequestedPickupUtc, o.ReadyAtUtc, items.Sum(i => i.Quantity)));

                return new Loaded(view, TakeawayRules.IsActive(o.Status));
            }),
        ];

        static void Step(List<DinerOrderTimelineEntry> timeline, string status, DateTime? atUtc)
        {
            if (atUtc is { } at)
            {
                timeline.Add(new DinerOrderTimelineEntry(status, at));
            }
        }
    }

    /// <summary>An order for the list, and whether it is still moving - active orders go first.</summary>
    private sealed record Loaded(DinerOrderView View, bool Active);

    public async Task<DinerOrderView> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();
        var mine = await MineAsync(dinerUserId, cancellationToken);

        var found = mine is null
            ? []
            : await LoadAsync(mine.Orders.Where(o => o.Id == orderId), mine.Seats, cancellationToken);

        if (found.Count == 0)
        {
            found = await TakeawayAsync(dinerUserId, null, orderId, cancellationToken);
        }

        // Somebody else's order and no order at all answer the same.
        return found.Count == 1 ? found[0].View : throw new KeyNotFoundException($"Order {orderId} was not found.");
    }

    private Guid RequireDiner() =>
        actor.DinerUserId ?? throw new UnauthorizedAccessException("Only a signed-in diner has orders.");

    /// <summary>The diner's orders as a query, or null when the account has never been on a tab.</summary>
    /// <remarks>
    /// <para>
    /// <b>Starts from the diner, not from the order history.</b> Their participant rows come first, by
    /// the <c>TabParticipants(UserId)</c> index, and the orders are then four seeks - placed by, a
    /// line named for them, a line the order names them for, and whole-table on a tab - rather than
    /// a scan of every order on the platform.
    /// </para>
    /// <para>
    /// <b>A waiter's order is theirs by its lines, not by its header.</b> The tablet names a guest per
    /// line, so one round can be several people's; each finds it on their list by a line that is
    /// theirs (<see cref="TabOrder.OwnerOf"/>): unsplit, and named for them on the line, or by the
    /// order on a line that names nobody. An order "for Ani" whose every line the waiter put on Bob
    /// has nothing of Ani's on it, so it is not on her list; nor is one for her that carries only the
    /// table's bottle - a shared line is nobody's, as on a phone's order. A voided line still counts:
    /// the order stays on the list with the line labelled, as it always did. What they are shown of
    /// it is <see cref="LoadAsync"/>'s to decide.
    /// </para>
    /// <para>
    /// <b>A whole-table order follows the tab view's rule</b> (<see cref="TabProjection.IsOwnLine"/>).
    /// A waiter's order for nobody in particular is table-attributed lines, and the tab itemises
    /// those on somebody's own lines only when <see cref="TabPermissions.MaySeeTableTotal"/> allows -
    /// approved, and not hidden from by the host - <b>and</b> they were at the table when it was
    /// ordered, which is who <c>/shares</c> splits it across. A guest the host hid the total from, one
    /// still pending, one removed from the tab, and one who arrived after it was ordered do not get
    /// it here either.
    /// </para>
    /// <para>
    /// One difference is deliberate. A whole-table order can carry a shared line as well (a waiter
    /// can key in a shared bottle), and the tab shows a guest without the total that line on their
    /// own lines like any shared item. This list is of orders, not lines, and the same order's
    /// table-attributed lines are exactly what that guest may not see itemised - so the order stays
    /// off their list, and the bottle is on their bill.
    /// </para>
    /// </remarks>
    private async Task<Mine?> MineAsync(Guid dinerUserId, CancellationToken cancellationToken)
    {
        var seats = await db.TabParticipants
            .AsNoTracking()
            .Where(p => p.UserId == dinerUserId)
            .Select(p => new { p.Id, p.TabId, p.Status, p.CanSeeTableTotal })
            .ToListAsync(cancellationToken);

        if (seats.Count == 0)
        {
            return null;
        }

        var participantIds = seats.Select(s => s.Id).ToList();

        var wholeTableTabIds = seats
            .Where(s => TabPermissions.MaySeeTableTotal(s.Status, s.CanSeeTableTotal))
            .Select(s => s.TabId)
            .Distinct()
            .ToList();

        var candidates = db.TabOrders
            .Where(o => o.PlacedByParticipantId != null && participantIds.Contains(o.PlacedByParticipantId.Value))
            .Select(o => o.Id)
            .Union(db.TabOrderLines
                .Where(l => !l.IsShared
                            && !l.IsTableAttributed
                            && l.OnBehalfOfParticipantId != null
                            && participantIds.Contains(l.OnBehalfOfParticipantId.Value))
                .Select(l => l.TabOrderId))
            .Union(db.TabOrderLines
                .Where(l => !l.IsShared
                            && !l.IsTableAttributed
                            && l.OnBehalfOfParticipantId == null
                            && l.TabOrder.OnBehalfOfParticipantId != null
                            && participantIds.Contains(l.TabOrder.OnBehalfOfParticipantId.Value))
                .Select(l => l.TabOrderId))
            .Union(db.TabOrders
                .Where(o => wholeTableTabIds.Contains(o.TabId)
                            && o.PlacedByParticipantId == null
                            && o.OnBehalfOfParticipantId == null
                            && o.Lines.Any(l => l.Shares.Any(s => participantIds.Contains(s.TabParticipantId))))
                .Select(o => o.Id));

        return new Mine(
            db.TabOrders
                .AsNoTracking()
                .Where(o => candidates.Contains(o.Id)),
            [.. seats.Select(s => new Seat(s.Id, s.TabId, TabPermissions.MaySeeTableTotal(s.Status, s.CanSeeTableTotal)))]);
    }

    /// <summary>The diner's orders as a query, and the seats they hold on those orders' tabs.</summary>
    private sealed record Mine(IQueryable<TabOrder> Orders, IReadOnlyList<Seat> Seats);

    /// <summary>One of the diner's participant rows, and whether it may see that tab's total.</summary>
    private sealed record Seat(Guid ParticipantId, Guid TabId, bool SeesTableTotal);

    /// <summary>The orders, each with the lines this diner may be shown of it.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every line, to a diner who may see the table's total</b> on that tab: the whole bill is
    /// theirs to read, and a whole-table order is shown to them whole.
    /// </para>
    /// <para>
    /// <b>Their own lines, to anybody else</b> - <see cref="TabProjection.IsOwnLine"/>, the rule the
    /// tab's <c>myLines</c> and the event stream follow. Until a waiter could name a guest per line,
    /// every line of an order on this list was the diner's own or a bottle they shared, so showing
    /// the order whole was the same thing. A round with Narine's steak and Aram's coffee on it is
    /// not: shown whole to Narine, with the host hiding the total from her, it would itemise Aram's
    /// coffee, price and all. An order with nothing of theirs left on it is not listed.
    /// </para>
    /// </remarks>
    private async Task<List<Loaded>> LoadAsync(
        IQueryable<TabOrder> orders,
        IReadOnlyList<Seat> seats,
        CancellationToken cancellationToken)
    {
        var rows = await orders
            .Select(o => new
            {
                o.Id,
                o.TabId,
                o.Tab.BranchId,
                VenueName = o.Tab.Branch.Venue.Name,
                BranchName = o.Tab.Branch.Name,
                o.Tab.Branch.CoverPhotoId,
                CoverIsExternal = (bool?)o.Tab.Branch.CoverPhoto!.IsExternallyHosted,
                CoverThumbnailPath = o.Tab.Branch.CoverPhoto!.ThumbnailPath,
                CoverCardPath = o.Tab.Branch.CoverPhoto!.CardPath,
                CoverFullPath = o.Tab.Branch.CoverPhoto!.FullPath,
                CoverWidth = o.Tab.Branch.CoverPhoto!.Width,
                CoverHeight = o.Tab.Branch.CoverPhoto!.Height,
                o.Status,
                TabStatus = o.Tab.Status,
                TableLabel = o.Tab.DiningTable.Label,
                o.Tab.TableSession.PartySize,
                o.PlacedAtUtc,
                o.EstimatedReadyAtUtc,
                o.PlacedByParticipantId,
                o.OnBehalfOfParticipantId,
                Lines = o.Lines
                    .OrderBy(l => l.Id)
                    .Select(l => new
                    {
                        l.Id,
                        l.MenuItemId,
                        l.NameSnapshot,
                        l.Quantity,
                        l.UnitPriceAmdSnapshot,
                        l.Note,
                        l.VoidedAtUtc,
                        l.VoidReason,
                        l.IsShared,
                        l.IsTableAttributed,
                        l.OnBehalfOfParticipantId,
                        Shares = l.Shares.Select(s => s.TabParticipantId).ToList(),
                    })
                    .ToList(),
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var tabIds = rows.Select(r => r.TabId).Distinct().ToList();

        var events = await db.TabEvents
            .AsNoTracking()
            .Where(e => tabIds.Contains(e.TabId) && e.Type == TabEventType.OrderStatusChanged)
            .OrderBy(e => e.TabId)
            .ThenBy(e => e.Sequence)
            .Select(e => new { e.PayloadJson, e.AtUtc })
            .ToListAsync(cancellationToken);

        var steps = events
            .Select(e => (Step: ParseStep(e.PayloadJson), e.AtUtc))
            .Where(e => e.Step is not null)
            .ToLookup(e => e.Step!.Value.OrderId, e => new DinerOrderTimelineEntry(
                DinerOrderStatuses.From(e.Step!.Value.To), e.AtUtc));

        var seatsByTab = seats.ToLookup(s => s.TabId);

        return
        [
            .. rows.Select(r =>
            {
                var mySeats = seatsByTab[r.TabId].ToList();
                var wholeOrder = mySeats.Any(s => s.SeesTableTotal);

                var items = r.Lines
                    .Where(l =>
                    {
                        if (wholeOrder)
                        {
                            return true;
                        }

                        var line = new TabLineSnapshot(
                            l.Id,
                            r.Id,
                            l.MenuItemId,
                            r.PlacedByParticipantId,
                            l.NameSnapshot,
                            l.UnitPriceAmdSnapshot,
                            l.Quantity,
                            l.Note,
                            r.Status,
                            r.PlacedAtUtc,
                            l.IsShared,
                            IsVoided: l.VoidedAtUtc is not null,
                            l.VoidedAtUtc,
                            l.VoidReason,
                            l.Shares,
                            l.IsShared || l.IsTableAttributed ? null : l.OnBehalfOfParticipantId ?? r.OnBehalfOfParticipantId,
                            l.IsTableAttributed);

                        return mySeats.Any(seat => TabProjection.IsOwnLine(line, seat.ParticipantId, tableTotalVisible: false));
                    })
                    .Select(l => new DinerOrderItem(
                        l.Id,
                        l.NameSnapshot,
                        l.Quantity,
                        l.UnitPriceAmdSnapshot,
                        l.VoidedAtUtc is null ? l.UnitPriceAmdSnapshot * l.Quantity : 0L,
                        l.Note,
                        l.VoidedAtUtc is not null))
                    .ToList();

                if (items.Count == 0)
                {
                    return null;
                }

                return new Loaded(
                    new DinerOrderView(
                    r.Id,
                    r.TabId,
                    r.BranchId,
                    r.VenueName,
                    r.BranchName,
                    r.CoverPhotoId is { } photoId
                        ? PhotoView.From(
                            photoId, r.CoverIsExternal == true, r.CoverThumbnailPath!, r.CoverCardPath!, r.CoverFullPath!,
                            r.CoverWidth, r.CoverHeight)
                        : null,
                    Kind: "dineIn",
                    DinerOrderStatuses.From(r.Status),
                    r.Status,
                    r.TableLabel,
                    r.PartySize,
                    r.PlacedAtUtc,
                    r.EstimatedReadyAtUtc,
                    items.Sum(i => i.LineTotalAmd),
                    items,
                    [new DinerOrderTimelineEntry(DinerOrderStatuses.Confirmed, r.PlacedAtUtc), .. steps[r.Id]],
                    CanCancel: false),
                    DinerOrderStatuses.IsActive(r.Status)
                    && (r.TabStatus == TabStatus.Open || r.TabStatus == TabStatus.Closing));
            }).OfType<Loaded>(),
        ];
    }

    /// <summary>The order and destination status out of an <c>OrderStatusChanged</c> payload, or null if unreadable.</summary>
    private static (Guid OrderId, TabOrderStatus To)? ParseStep(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            return Property(root, "orderId") is { ValueKind: JsonValueKind.String } id
                   && id.TryGetGuid(out var orderId)
                   && Property(root, "toStatus") is { ValueKind: JsonValueKind.Number } to
                   && to.TryGetInt32(out var status)
                   && Enum.IsDefined((TabOrderStatus)status)
                ? (orderId, (TabOrderStatus)status)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
