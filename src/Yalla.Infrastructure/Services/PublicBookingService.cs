using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Public;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reading and cancelling one booking with nothing but the manage link.
/// </summary>
/// <remarks>
/// <para>
/// <b>The narrowest surface in the API.</b> One row, reached by one unguessable token, projected
/// into a record that cannot carry anything sensitive - see <see cref="PublicBookingView"/>. There
/// is no list, no search and no second row reachable from here, so a stolen link leaks exactly the
/// booking it is for and nothing beside it.
/// </para>
/// <para>
/// Cancelling is <c>IReservationService.CancelByManageTokenAsync</c> rather than a write of its
/// own. The deadline rule, the lateness record and cancelling the reminder are the same code the
/// app's cancel runs; a second path here is how a web cancel would eventually stop cancelling the
/// reminder, and the diner would get a push about a table they had already given back.
/// </para>
/// </remarks>
internal sealed class PublicBookingService(
    YallaDbContext db,
    IClock clock,
    IReservationService reservations) : IPublicBookingService
{
    public async Task<PublicBookingView> GetAsync(
        string manageToken,
        CancellationToken cancellationToken = default)
    {
        // A blank token is answered like any other bad one - see ManageBookingFailure.
        if (string.IsNullOrWhiteSpace(manageToken))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(manageToken);

        // Projected in the query rather than loaded and mapped, so the fields this route does not
        // publish are never read out of the database at all.
        var row = await db.Reservations
            .AsNoTracking()
            .Where(r => r.ManageTokenHash == hash)
            .Select(r => new Row(
                r.Branch.Venue.Name,
                r.Branch.Name,
                r.Branch.Address,
                r.Branch.TimeZoneId,
                r.DiningTable.Label,
                r.LocalDate,
                r.LocalStartTime,
                r.PartySize,
                r.Status,
                r.Code,
                r.StartUtc,
                r.EndUtc,
                r.CancelledAfterDeadline,
                r.Branch.ReservationPolicy.CancellationDeadlineMinutes))
            .FirstOrDefaultAsync(cancellationToken);

        // Unknown and expired, answered identically. The expiry is recomputed here from the same
        // rule the entity uses rather than trusted from the row, and it raises the same exception
        // as no row at all, so nothing on the wire tells the two apart.
        if (row is null || clock.UtcNow > row.EndUtc.AddDays(Reservation.ManageTokenGraceDays))
        {
            throw ManageBookingFailure.Raise();
        }

        return Project(row);
    }

    public async Task<PublicBookingView> CancelAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // The one cancellation path, shared with the app's. Everything this route adds is how the
        // caller proved they may: they are holding the token.
        await reservations.CancelByManageTokenAsync(manageToken, reason, cancellationToken);

        // Re-read through the same projection the GET uses, so the two routes cannot come to
        // disagree about what a manage link is allowed to show.
        return await GetAsync(manageToken, cancellationToken);
    }

    private static PublicBookingView Project(Row row) => new(
        row.VenueName,
        row.BranchName,
        row.BranchAddress,
        row.TimeZoneId,
        row.TableLabel,
        row.LocalDate,
        row.LocalStartTime,
        row.PartySize,
        row.Status,
        row.Code,

        // Absolute, computed from the interval the diner booked and the deadline in force. The page
        // does not reimplement this arithmetic and a later policy edit cannot move a deadline the
        // diner has already been shown, because the start it is measured from never changes.
        CancellationDeadlineUtc: row.StartUtc.AddMinutes(-row.CancellationDeadlineMinutes),

        // Whether cancelling would do anything - not whether it would be free. Cancelling past the
        // deadline is allowed and merely recorded, which is the whole point of the deadline being
        // a record rather than a block.
        CanCancel: ReservationService.CanStillCancel(row.Status),
        CancelledAfterDeadline: row.CancelledAfterDeadline);

    private sealed record Row(
        string VenueName,
        string BranchName,
        string BranchAddress,
        string TimeZoneId,
        string TableLabel,
        DateOnly LocalDate,
        TimeOnly LocalStartTime,
        int PartySize,
        Domain.Enums.ReservationStatus Status,
        string Code,
        DateTime StartUtc,
        DateTime EndUtc,
        bool CancelledAfterDeadline,
        int CancellationDeadlineMinutes);
}
