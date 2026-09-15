using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The Orders tab: every order the signed-in diner is on, across every tab they have joined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which orders are "mine".</b> An order placed from the diner's own phone, or keyed in by a waiter
/// on their behalf - through any tab participant row that carries their account - and an order a
/// waiter keyed in for the whole table on a tab the diner was approved onto. The last is how most
/// spoken orders land, and a diner who ate it should see it.
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

        var orders = await MineAsync(RequireDiner(), cancellationToken);

        if (orders is null)
        {
            return [];
        }

        orders = filter switch
        {
            DinerOrderStatuses.ActiveFilter => orders.Where(o =>
                o.Status == TabOrderStatus.New || o.Status == TabOrderStatus.InKitchen || o.Status == TabOrderStatus.Ready),
            DinerOrderStatuses.HistoryFilter => orders.Where(o =>
                o.Status == TabOrderStatus.Served || o.Status == TabOrderStatus.Voided),
            _ => orders,
        };

        return await LoadAsync(orders.OrderByDescending(o => o.PlacedAtUtc).Take(MaxOrders), cancellationToken);
    }

    public async Task<DinerOrderView> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var orders = await MineAsync(RequireDiner(), cancellationToken);

        var found = orders is null
            ? []
            : await LoadAsync(orders.Where(o => o.Id == orderId), cancellationToken);

        // Somebody else's order and no order at all answer the same.
        return found.Count == 1 ? found[0] : throw new KeyNotFoundException($"Order {orderId} was not found.");
    }

    private Guid RequireDiner() =>
        actor.DinerUserId ?? throw new UnauthorizedAccessException("Only a signed-in diner has orders.");

    /// <summary>The diner's orders as a query, or null when the account has never been on a tab.</summary>
    /// <remarks>
    /// <para>
    /// <b>Starts from the diner, not from the order history.</b> Their participant rows come first, by
    /// the <c>TabParticipants(UserId)</c> index, and the orders are then three seeks - placed by, on
    /// behalf of, and whole-table on a tab - rather than a scan of every order on the platform.
    /// </para>
    /// <para>
    /// <b>A whole-table order follows the tab view's rule.</b> In the tab those lines are the table's
    /// bill, shown only to a participant <see cref="TabPermissions.MaySeeTableTotal"/> allows: approved,
    /// and not hidden from by the host. A guest the host hid the total from, one still pending, and
    /// one removed from the tab do not get it here either.
    /// </para>
    /// </remarks>
    private async Task<IQueryable<TabOrder>?> MineAsync(Guid dinerUserId, CancellationToken cancellationToken)
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
            .Union(db.TabOrders
                .Where(o => o.OnBehalfOfParticipantId != null && participantIds.Contains(o.OnBehalfOfParticipantId.Value))
                .Select(o => o.Id))
            .Union(db.TabOrders
                .Where(o => wholeTableTabIds.Contains(o.TabId)
                            && o.PlacedByParticipantId == null
                            && o.OnBehalfOfParticipantId == null)
                .Select(o => o.Id));

        return db.TabOrders
            .AsNoTracking()
            .Where(o => candidates.Contains(o.Id));
    }

    private async Task<List<DinerOrderView>> LoadAsync(IQueryable<TabOrder> orders, CancellationToken cancellationToken)
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
                TableLabel = o.Tab.DiningTable.Label,
                o.Tab.TableSession.PartySize,
                o.PlacedAtUtc,
                o.EstimatedReadyAtUtc,
                Lines = o.Lines
                    .OrderBy(l => l.Id)
                    .Select(l => new
                    {
                        l.Id,
                        l.NameSnapshot,
                        l.Quantity,
                        l.UnitPriceAmdSnapshot,
                        l.Note,
                        l.VoidedAtUtc,
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

        return
        [
            .. rows.Select(r =>
            {
                var items = r.Lines
                    .Select(l => new DinerOrderItem(
                        l.Id,
                        l.NameSnapshot,
                        l.Quantity,
                        l.UnitPriceAmdSnapshot,
                        l.VoidedAtUtc is null ? l.UnitPriceAmdSnapshot * l.Quantity : 0L,
                        l.Note,
                        l.VoidedAtUtc is not null))
                    .ToList();

                return new DinerOrderView(
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
                    CanCancel: false);
            }),
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
