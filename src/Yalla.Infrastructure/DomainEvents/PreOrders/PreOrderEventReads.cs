using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// What the pre-order handlers read, from the tracker first: every path that raises has loaded the booking it is about,
/// so a handler that queried again would cost the change a round trip the hand-written code did not. Each read falls
/// back to the database for an event raised on a path that loaded less.
/// </summary>
internal static class PreOrderEventReads
{
    /// <summary>The pre-order's booking.</summary>
    public static async Task<Reservation> ReservationAsync(YallaDbContext db, Guid reservationId, CancellationToken cancellationToken) =>
        db.Reservations.Local.FirstOrDefault(r => r.Id == reservationId)
        ?? await db.Reservations.FirstAsync(r => r.Id == reservationId, cancellationToken);

    /// <summary>A table's label, as the History names it.</summary>
    public static async Task<string> TableLabelAsync(YallaDbContext db, Guid diningTableId, CancellationToken cancellationToken) =>
        db.DiningTables.Local.FirstOrDefault(t => t.Id == diningTableId)?.Label
        ?? await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == diningTableId)
            .Select(t => t.Label)
            .FirstAsync(cancellationToken);
}
