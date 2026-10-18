using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The questions one booking asks the booking before it on the same table - "will you have left by
/// 19:40?" - in one place: asking, saying yes, closing, and what happens to them when either booking
/// moves or ends. See <see cref="TableShareRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// A static helper over the caller's unit of work, like <see cref="PendingDecline"/> and
/// <see cref="DinerNotices"/>: nothing here saves. Every change commits with the change to the booking
/// that caused it, and every outcome of a request changes the later booking's row as well - so the later
/// booking's row version is what settles a yes, a no, the sweep and a move racing one another.
/// </para>
/// <para>
/// <b>The earlier guest is never locked.</b> Their account is read, not locked, when they are asked: a
/// booking already holds a table's lock and its own diner's, and taking a second diner's from inside
/// would let two such bookings wait on each other. The cost is a question that can reach an account
/// deleted at that same instant, whose feed then holds one entry nobody reads.
/// </para>
/// </remarks>
internal static class TableShares
{
    /// <summary>
    /// Whether the guest of <paramref name="earlierReservationId"/> can be asked in the app: they booked
    /// from it (not the web), the account is live, and it has a phone to push to.
    /// </summary>
    public static async Task<bool> CanAskInAppAsync(
        YallaDbContext db,
        Guid earlierReservationId,
        CancellationToken cancellationToken)
    {
        var earlier = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == earlierReservationId)
            .Select(r => new { r.Channel, r.DinerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (earlier is not { DinerUserId: { } dinerUserId } || earlier.Channel == ReservationChannel.Web)
        {
            return false;
        }

        return await db.DinerUsers
                   .AsNoTracking()
                   .AnyAsync(u => u.Id == dinerUserId && u.IsActive && u.DeletedAtUtc == null, cancellationToken)
               && await db.DinerDevices
                   .AsNoTracking()
                   .AnyAsync(d => d.DinerUserId == dinerUserId && d.RevokedAtUtc == null, cancellationToken);
    }

    /// <summary>
    /// Asks: stages the request, and - when the earlier guest is asked in the app - their push and feed
    /// entry. Called inside the table's lock, beside the later booking it is for.
    /// </summary>
    public static async Task<TableShareRequest> AskAsync(
        YallaDbContext db,
        IOutbox outbox,
        Branch branch,
        string tableLabel,
        Reservation later,
        Guid earlierReservationId,
        DateTime freeByUtc,
        DateTime expiresAtUtc,
        bool asksEarlierGuest,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = new TableShareRequest(
            earlierReservationId, later.Id, later.StartUtc, freeByUtc, nowUtc, expiresAtUtc, asksEarlierGuest);

        db.TableShareRequests.Add(request);

        if (!asksEarlierGuest)
        {
            return request;
        }

        var earlier = await db.Reservations
            .AsNoTracking()
            .FirstAsync(r => r.Id == earlierReservationId, cancellationToken);

        if (earlier.DinerUserId is not { } dinerUserId)
        {
            return request;
        }

        var zone = BranchZone.For(branch.TimeZoneId);
        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationTableRequest,
            Notice(request, earlier.Id, dinerUserId, venueName, tableLabel, zone),
            nowUtc,
            OutboxMessageTypes.KeyFor("reservation", earlier.Id, $"table-request:{request.Id:N}"));

        DinerNotices.TableRequested(
            db,
            earlier,
            venueName,
            branch.Name,
            request.Id,
            zone.LocalTimeAt(request.RequestedStartUtc),
            zone.LocalTimeAt(request.FreeByUtc),
            nowUtc);

        return request;
    }

    /// <summary>
    /// The later booking's request was said yes to - by the earlier guest, or by the floor for them: the
    /// earlier booking promises to free the table by the time asked (its end cut to it), the request is
    /// accepted, and the earlier guest is told. The caller confirms the later booking.
    /// </summary>
    /// <returns>The request, or null when the later booking was waiting on none.</returns>
    public static async Task<TableShareRequest?> ApplyYesAsync(
        YallaDbContext db,
        IOutbox outbox,
        Guid laterReservationId,
        Branch branch,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = await PendingForLaterAsync(db, laterReservationId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        var earlier = await db.Reservations.FirstOrDefaultAsync(r => r.Id == request.EarlierReservationId, cancellationToken);

        request.Accept(nowUtc);

        // A booking that has gone since needs no promise: the table is free of it.
        if (earlier is null || !ReservationOverlap.Blocks(earlier.Status))
        {
            return request;
        }

        earlier.PromiseToFreeBy(request.FreeByUtc);

        await TellEarlierDecidedAsync(db, outbox, earlier, request, branch, accepted: true, nowUtc, cancellationToken);

        return request;
    }

    /// <summary>
    /// Ends the later booking's open request without a yes: declined, expired, or withdrawn because the
    /// later booking moved or ended.
    /// </summary>
    /// <returns>The request, or null when there was none open.</returns>
    public static async Task<TableShareRequest?> CloseForLaterAsync(
        YallaDbContext db,
        Guid laterReservationId,
        TableShareRequestStatus to,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = await PendingForLaterAsync(db, laterReservationId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        switch (to)
        {
            case TableShareRequestStatus.Declined:
                request.Decline(nowUtc);
                break;

            case TableShareRequestStatus.Expired:
                request.Expire(nowUtc);
                break;

            case TableShareRequestStatus.Withdrawn:
                request.Withdraw(nowUtc);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(to), to, "Not a way to close a request without a yes.");
        }

        return request;
    }

    /// <summary>
    /// Withdraws the later booking's open request at once, inside the caller's transaction - for a move,
    /// which may ask a new question of the same guest in the same save, where the filtered unique index
    /// must see the old one closed first.
    /// </summary>
    public static Task<int> WithdrawForLaterNowAsync(
        YallaDbContext db,
        Guid laterReservationId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        db.TableShareRequests
            .Where(r => r.LaterReservationId == laterReservationId && r.Status == TableShareRequestStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, TableShareRequestStatus.Withdrawn)
                    .SetProperty(r => r.DecidedAtUtc, nowUtc),
                cancellationToken);

    /// <summary>
    /// The earlier booking moved, or ended: the questions open to its guest are settled by where it now
    /// stands.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <b>Out of the later booking's way</b> - cancelled, released, declined, moved to another table, or
    /// now starting at or after the later one: the request is withdrawn, and a later booking that was
    /// waiting on this guest's answer alone is confirmed, its diner told. One the floor was deciding
    /// stays with the floor.
    /// </item>
    /// <item>
    /// <b>Still before it, but promised in time</b> - the move accepted a time to leave by that covers
    /// the one asked: the request is accepted, and a later booking waiting on this guest alone is
    /// confirmed.
    /// </item>
    /// <item>Still before it, without that promise: the question stands.</item>
    /// </list>
    /// A later booking already past its deadline is left for the sweep.
    /// </remarks>
    public static async Task EarlierChangedAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation earlier,
        Branch branch,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var open = await db.TableShareRequests
            .Where(r => r.EarlierReservationId == earlier.Id && r.Status == TableShareRequestStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var request in open)
        {
            var later = await db.Reservations.FirstOrDefaultAsync(r => r.Id == request.LaterReservationId, cancellationToken);

            var outOfTheWay = later is null
                              || !ReservationOverlap.Blocks(earlier.Status)
                              || earlier.DiningTableId != later.DiningTableId
                              || earlier.StartUtc >= later.StartUtc;

            var promisedInTime = !outOfTheWay
                                 && earlier.PromisedFreeByUtc is { } promise
                                 && promise <= request.FreeByUtc;

            if (!outOfTheWay && !promisedInTime)
            {
                continue;
            }

            if (outOfTheWay)
            {
                request.Withdraw(nowUtc);
            }
            else
            {
                request.Accept(nowUtc);
            }

            if (later is { Status: ReservationStatus.PendingApproval }
                && request.AsksEarlierGuest
                && !later.ApprovalHasLapsedAt(nowUtc)
                && later.StartUtc > nowUtc)
            {
                later.Confirm(nowUtc);

                var key = await ReservationService.DecisionKeyAsync(db, later.Id, "approved", nowUtc, cancellationToken);

                ReservationService.TellDinerDecided(db, outbox, later, branch, approved: true, expired: false, key, nowUtc);
            }
        }
    }

    /// <summary>
    /// Tells the earlier guest their answer took effect: a feed entry either way, and - for a yes - the
    /// push that holds them to the time.
    /// </summary>
    public static async Task TellEarlierDecidedAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation earlier,
        TableShareRequest request,
        Branch branch,
        bool accepted,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (earlier.DinerUserId is not { } dinerUserId)
        {
            return;
        }

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

    /// <summary>The later booking's open request, tracked, or null.</summary>
    public static Task<TableShareRequest?> PendingForLaterAsync(
        YallaDbContext db,
        Guid laterReservationId,
        CancellationToken cancellationToken) =>
        db.TableShareRequests
            .Where(r => r.LaterReservationId == laterReservationId && r.Status == TableShareRequestStatus.Pending)
            .OrderBy(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

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
