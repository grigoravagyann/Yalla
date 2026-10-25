using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// What the booking handlers read, from the tracker first: the booking services load the branch with its venue, and the
/// bookings they change, so a handler that queried again would cost a round trip the hand-written code did not. Each read
/// falls back to the database for a path that loaded less - the approval sweep reads its branch untracked.
/// </summary>
internal static class BookingReads
{
    /// <summary>The branch, with its venue, for the names a message carries and the zone it speaks in.</summary>
    public static async Task<Branch> BranchAsync(YallaDbContext db, Guid branchId, CancellationToken cancellationToken)
    {
        if (db.Branches.Local.FirstOrDefault(b => b.Id == branchId) is not { } branch)
        {
            return await db.Branches
                .AsNoTracking()
                .Include(b => b.Venue)
                .FirstAsync(b => b.Id == branchId, cancellationToken);
        }

        var venue = db.Entry(branch).Reference(b => b.Venue);

        if (!venue.IsLoaded)
        {
            await venue.LoadAsync(cancellationToken);
        }

        return branch;
    }

    /// <summary>A table's label as the messages name it, or empty when there is no such table.</summary>
    public static async Task<string> TableLabelAsync(YallaDbContext db, Guid tableId, CancellationToken cancellationToken) =>
        db.DiningTables.Local.FirstOrDefault(t => t.Id == tableId)?.Label
        ?? await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == tableId)
            .Select(t => t.Label)
            .FirstOrDefaultAsync(cancellationToken)
        ?? string.Empty;

    /// <summary>A booking, tracked when the code that raised the event loaded it, else read untracked; null when gone.</summary>
    public static async Task<Reservation?> ReservationAsync(YallaDbContext db, Guid reservationId, CancellationToken cancellationToken) =>
        db.Reservations.Local.FirstOrDefault(r => r.Id == reservationId)
        ?? await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
}
