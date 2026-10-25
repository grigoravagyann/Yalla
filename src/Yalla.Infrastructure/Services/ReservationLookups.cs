using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Public;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What every booking command starts from: who is asking, and the rows it acts on - the branch, the
/// table, the booking, or the booking behind a manage link.
/// </summary>
internal sealed class ReservationLookups(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAuthorizationQueries authorization)
{
    // ---------------------------------------------------------------- permissions

    /// <summary>
    /// The calling diner.
    /// </summary>
    /// <remarks>
    /// Reads identity from <see cref="ICurrentActor"/> and nowhere else, which is now the token's
    /// claims by way of <c>ClaimsCurrentActor</c>. The <c>VerifiedDiner</c> policy on the route has
    /// already refused anyone who is not a diner with an account; this is the second half of that
    /// answer - the id the booking is filed under - and it fails closed if the two ever disagree.
    /// Staff taking a booking over the phone is a separate operation and is not this one.
    /// </remarks>
    internal Guid RequireDiner(string operation)
    {
        if (actor.Type != ActorType.Diner || actor.DinerUserId is not { } dinerUserId || dinerUserId == Guid.Empty)
        {
            throw new UnauthorizedAccessException($"{operation} requires a verified diner account.");
        }

        return dinerUserId;
    }

    /// <summary>
    /// A waiter or above, at this branch. The lighter cousin of the manager check below.
    /// </summary>
    /// <remarks>
    /// Releasing a late booking is floor work, not a decision about money: the person standing at
    /// the table is the one who knows nobody came. Requiring a manager would mean the table stays
    /// held until somebody senior walks past, which is how the feature ends up unused. The same goes
    /// for saying yes or no to a booking, and for the list of the ones waiting: a short-notice request
    /// is a question about a table only the floor can see, with fifteen minutes to answer it.
    /// </remarks>
    internal Task<Guid> RequireStaffForBranchAsync(
        Guid branchId,
        string operation,
        CancellationToken cancellationToken) =>
        FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, branchId, operation, cancellationToken);

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// The branch's hours for a sitting on <paramref name="localDate"/>: that date and the evening before it,
    /// whose block may run past midnight - special days included.
    /// </summary>
    internal Task<OpeningCalendar> HoursAroundAsync(Guid branchId, DateOnly localDate, CancellationToken cancellationToken) =>
        OpeningCalendars.ForBranchAsync(db, branchId, localDate.AddDays(-1), localDate, cancellationToken);

    internal async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .Include(b => b.Venue)
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

        return branch ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");
    }

    internal async Task<DiningTable> LoadTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken)
    {
        // With its room, which the booking's view names.
        var table = await db.DiningTables
            .AsNoTracking()
            .Include(t => t.FloorArea)
            .FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken);

        if (table is null || !table.IsActive)
        {
            throw new KeyNotFoundException($"Table {tableId} was not found.");
        }

        if (table.BranchId != branchId)
        {
            // A branch mismatch is a bad request, not a missing table: answering 404 would tell a
            // caller the table does not exist when it does.
            throw new ArgumentException(
                $"Table {tableId} does not belong to branch {branchId}.", nameof(branchId));
        }

        return table;
    }

    internal async Task<Reservation> LoadReservationAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        return reservation ?? throw new KeyNotFoundException($"Reservation {reservationId} was not found.");
    }

    // ---------------------------------------------------------------- manage links

    /// <summary>
    /// Resolves a manage token to its booking, or throws the one refusal every failure shares.
    /// </summary>
    /// <remarks>
    /// Unknown, expired and purged are three facts here and one answer on the wire. The expiry
    /// check runs after the row is found and raises the same exception as not finding one, and
    /// nothing branches on the token's contents before the lookup.
    /// </remarks>
    internal async Task<Reservation> FindByManageTokenAsync(
        string manageToken,
        CancellationToken cancellationToken)
    {
        // A blank token is answered like any other bad one rather than as a different error: the
        // caller learns nothing either way, which is the whole rule.
        if (string.IsNullOrWhiteSpace(manageToken))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(manageToken);

        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.ManageTokenHash == hash, cancellationToken);

        // The link outlives the booking by ManageTokenGraceDays so somebody can still read what
        // happened, then stops working - a bearer capability sitting in a WhatsApp thread should
        // not be live forever.
        if (reservation is null || !reservation.ManageTokenIsLiveAt(clock.UtcNow))
        {
            throw ManageBookingFailure.Raise();
        }

        return reservation;
    }
}
