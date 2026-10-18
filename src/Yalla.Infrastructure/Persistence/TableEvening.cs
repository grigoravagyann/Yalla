using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// One table's evening, read from the database and judged by <see cref="TableSequencing"/> - the one
/// read every writer of a booking's slot shares: an insert and a move under the table's lock, going back
/// to a declined change's slot, and deciding again a booking whose question to the guest before it went
/// away.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reads.</b> The live bookings around the start - the window the rule can see
/// (<see cref="TableSequencing.Lookback"/>, <see cref="TableSequencing.Lookahead"/>) by one indexed range
/// read, plus a booking seated there now whatever its booked times, because a party seated late runs past
/// them - each with the promise its guest made and whether its guest is being asked already; and the open
/// sitting. The availability read fetches a wider window over the same rows and so sees all of these.
/// </para>
/// <para>
/// <b>What it leaves out.</b> The booking being judged, and every booking waiting on a question to it:
/// a booking changing its own slot is not made to promise a time, nor refused, because of somebody who
/// is only asking for its table - that question is settled afterwards, from where the booking ends up
/// (<c>TableShares.EarlierChangedAsync</c>). A question the booking asked itself does not make the guest
/// before it "already asked".
/// </para>
/// <para>
/// <b>An overlay</b> stands in for a booking whose change is staged but not yet saved, so a booking
/// judged inside the same transaction sees it where it is going.
/// </para>
/// </remarks>
internal static class TableEvening
{
    public static async Task<(TableSequencingVerdict Verdict, TableSession? Sitting)> JudgeAsync(
        YallaDbContext db,
        Guid tableId,
        DateTime startUtc,
        ReservationPolicy policy,
        Guid excludingReservationId,
        CancellationToken cancellationToken,
        Reservation? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var fromUtc = startUtc - TableSequencing.Lookback(policy.BufferMinutes);
        var toUtc = startUtc + TableSequencing.Lookahead(policy.TurnTimeMinutes, policy.BufferMinutes);
        var overlayId = overlay?.Id ?? Guid.Empty;

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && r.Id != excludingReservationId
                        && r.Id != overlayId
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval
                            || r.Status == ReservationStatus.Seated)
                        && (r.EndUtc > fromUtc || r.Status == ReservationStatus.Seated)
                        && r.StartUtc < toUtc

                        // Not a booking that is only asking for the judged booking's table.
                        && !db.TableShareRequests.Any(q => q.EarlierReservationId == excludingReservationId
                                                           && q.LaterReservationId == r.Id
                                                           && q.Status == TableShareRequestStatus.Pending))
            .Select(r => new
            {
                r.Id,
                r.StartUtc,
                r.EndUtc,
                r.Status,
                r.PromisedFreeByUtc,
                IsBeingAsked = db.TableShareRequests.Any(q => q.EarlierReservationId == r.Id
                                                              && q.Status == TableShareRequestStatus.Pending
                                                              && q.LaterReservationId != excludingReservationId),
            })
            .ToListAsync(cancellationToken);

        var bookings = rows
            .Select(r => new SequencedBooking(r.Id, r.StartUtc, r.EndUtc, r.Status, r.PromisedFreeByUtc, r.IsBeingAsked))
            .ToList();

        if (overlay is not null
            && overlay.Id != excludingReservationId
            && overlay.DiningTableId == tableId
            && ReservationOverlap.Blocks(overlay.Status))
        {
            var askedAlready = await db.TableShareRequests.AnyAsync(
                q => q.EarlierReservationId == overlay.Id
                     && q.Status == TableShareRequestStatus.Pending
                     && q.LaterReservationId != excludingReservationId,
                cancellationToken);

            bookings.Add(new SequencedBooking(
                overlay.Id, overlay.StartUtc, overlay.EndUtc, overlay.Status, overlay.PromisedFreeByUtc, askedAlready));
        }

        var open = await db.TableSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null, cancellationToken);

        var verdict = TableSequencing.Judge(
            startUtc,
            policy.TurnTimeMinutes,
            policy.BufferMinutes,
            bookings,
            open is null ? null : new OpenSitting(open.SeatedAtUtc, open.ReservationId));

        return (verdict, open);
    }

    /// <summary>
    /// Whether a booking can keep - or go back to - the slot the verdict was asked about, ending at
    /// <paramref name="endUtc"/>, as it stands: nothing refuses it, nobody has to be asked, and any time
    /// the next booking needs it gone by is no sooner than it already ends.
    /// </summary>
    public static bool Holds(TableSequencingVerdict verdict, DateTime endUtc) =>
        verdict.Refusal is null
        && !verdict.NeedsEarlierGuest
        && (verdict.MustFreeByUtc is not { } freeBy || endUtc <= freeBy);
}
