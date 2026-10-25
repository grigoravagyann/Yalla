using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// What the guest before a later booking on the table is told about its request for their table (see
/// <see cref="TableShareRequest"/>): asked in the app - "will you have left by 19:40?" - a push
/// (<c>reservation.table-request</c>) and a feed entry; their yes taken effect - theirs or the floor's for them - the push
/// that holds them to it (<c>reservation.table-request-decided</c>) and a feed entry; their no, a feed entry.
/// </summary>
/// <remarks>
/// <b>Their account is read, never locked</b>: a booking already holds a table's lock and its own diner's, and taking a
/// second diner's from inside would let two such bookings wait on each other. The cost is a question that can reach an
/// account deleted at that same instant, whose feed then holds one entry nobody reads.
/// </remarks>
internal sealed class EarlierGuestNotice(YallaDbContext db, IOutbox outbox) :
    IDomainEventHandler<TableShareAsked>,
    IDomainEventHandler<ReservationPromisedToFreeBy>,
    IDomainEventHandler<ReservationDeclined>
{
    /// <summary>Asked in the app: the push and the feed entry. One the floor asks by phone tells nobody here.</summary>
    public async Task HandleAsync(TableShareAsked domainEvent, CancellationToken cancellationToken)
    {
        var request = domainEvent.Request;

        if (!request.AsksEarlierGuest)
        {
            return;
        }

        var earlier = await db.Reservations
            .AsNoTracking()
            .FirstAsync(r => r.Id == request.EarlierReservationId, cancellationToken);

        if (earlier.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        // The table the later booking asks for - the one the earlier booking has.
        var later = await BookingReads.ReservationAsync(db, request.LaterReservationId, cancellationToken)
                    ?? throw new InvalidOperationException($"The booking {request.LaterReservationId} asking for a table was not found.");
        var branch = await BookingReads.BranchAsync(db, later.BranchId, cancellationToken);
        var tableLabel = await BookingReads.TableLabelAsync(db, later.DiningTableId, cancellationToken);
        var zone = BranchZone.For(branch.TimeZoneId);
        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationTableRequest,
            Notice(request, earlier.Id, dinerUserId, venueName, tableLabel, zone),
            domainEvent.OccurredAtUtc,
            OutboxMessageTypes.KeyFor("reservation", earlier.Id, $"table-request:{request.Id:N}"));

        DinerNotices.TableRequested(
            db,
            earlier,
            venueName,
            branch.Name,
            request.Id,
            zone.LocalTimeAt(request.RequestedStartUtc),
            zone.LocalTimeAt(request.FreeByUtc),
            domainEvent.OccurredAtUtc);
    }

    /// <summary>Their yes took effect - theirs, or the floor's for them: the feed entry and the push that holds them to it.</summary>
    public Task HandleAsync(ReservationPromisedToFreeBy domainEvent, CancellationToken cancellationToken) =>
        TellDecidedAsync(domainEvent.Reservation, domainEvent.Request, accepted: true, domainEvent.OccurredAtUtc, cancellationToken);

    /// <summary>Their no declined the later booking: their own record of it in the feed.</summary>
    public async Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AnsweredRequest is not { } request)
        {
            return;
        }

        var earlier = await BookingReads.ReservationAsync(db, request.EarlierReservationId, cancellationToken);

        if (earlier is not null)
        {
            await TellDecidedAsync(earlier, request, accepted: false, domainEvent.OccurredAtUtc, cancellationToken);
        }
    }

    /// <summary>A feed entry either way, and - for a yes - the push that holds them to the time.</summary>
    private async Task TellDecidedAsync(
        Reservation earlier,
        TableShareRequest request,
        bool accepted,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (earlier.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        var branch = await BookingReads.BranchAsync(db, earlier.BranchId, cancellationToken);
        var zone = BranchZone.For(branch.TimeZoneId);
        var venueName = branch.Venue?.Name ?? branch.Name;

        DinerNotices.TableRequestDecided(
            db,
            earlier,
            venueName,
            branch.Name,
            request.Id,
            accepted,
            zone.LocalTimeAt(request.RequestedStartUtc),
            zone.LocalTimeAt(request.FreeByUtc),
            nowUtc);

        if (!accepted)
        {
            return;
        }

        var tableLabel = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == earlier.DiningTableId)
            .Select(t => t.Label)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationTableRequestDecided,
            Notice(request, earlier.Id, dinerUserId, venueName, tableLabel, zone),
            nowUtc,
            OutboxMessageTypes.KeyFor("reservation", earlier.Id, $"table-request-decided:{request.Id:N}"));
    }

    private static object Notice(
        TableShareRequest request,
        Guid earlierReservationId,
        Guid dinerUserId,
        string venueName,
        string tableLabel,
        BranchZone zone) =>
        new
        {
            requestId = request.Id,
            reservationId = earlierReservationId,
            dinerUserId,
            venueName,
            tableLabel,
            requestedLocalTime = zone.LocalTimeAt(request.RequestedStartUtc),
            freeByLocalTime = zone.LocalTimeAt(request.FreeByUtc),
            freeByUtc = request.FreeByUtc,
            requestedStartUtc = request.RequestedStartUtc.ToString("O", CultureInfo.InvariantCulture),
        };
}
