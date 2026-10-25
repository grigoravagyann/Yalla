using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A booking's entries in the activity log, one shape for every handler that writes one - the bookings'
/// <c>ReservationActivity</c> and the pre-orders' answers. See <c>docs/activity.md</c>.
/// </summary>
internal static class BookingActivity
{
    /// <summary>Adds the entry to the caller's unit of work.</summary>
    /// <param name="activity">The log.</param>
    /// <param name="db">The context, for the table's label when it is not tracked.</param>
    /// <param name="reservation">The booking, as it now stands.</param>
    /// <param name="action">A <c>booking.*</c> code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="extra">Parameters beside the booking's own (its date, time, party and status).</param>
    /// <param name="reason">Why, as somebody typed it.</param>
    /// <param name="before">What it was, for a change.</param>
    /// <param name="bySystem">Nobody did it - a deadline passed.</param>
    /// <param name="tableSessionId">The sitting it belongs to, for a seat.</param>
    /// <param name="byGuest">The guest did it - from the app or from the booking's link.</param>
    public static async Task RecordAsync(
        IActivityLog activity,
        YallaDbContext db,
        Reservation reservation,
        string action,
        CancellationToken cancellationToken,
        object? extra = null,
        string? reason = null,
        object? before = null,
        bool bySystem = false,
        Guid? tableSessionId = null,
        bool byGuest = false)
    {
        var label = db.DiningTables.Local.FirstOrDefault(t => t.Id == reservation.DiningTableId)?.Label
                    ?? await db.DiningTables
                        .AsNoTracking()
                        .Where(t => t.Id == reservation.DiningTableId)
                        .Select(t => t.Label)
                        .FirstOrDefaultAsync(cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Booking, action, ActivitySubjects.Reservation, reservation.Id)
            {
                BranchId = reservation.BranchId,
                TableSessionId = tableSessionId,
                TableLabel = label,
                GuestName = reservation.GuestName,
                SubjectCode = reservation.Code,
                Reason = reason,
                Before = before,
                After = before is null ? null : Slot(reservation),
                BySystem = bySystem,
                ByGuest = byGuest,
                Summary = new
                {
                    localDate = reservation.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    localTime = reservation.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    partySize = reservation.PartySize,
                    status = (int)reservation.Status,
                    tableId = reservation.DiningTableId,
                    extra,
                },
            },
            cancellationToken);
    }

    /// <summary>Where and when a booking stands - a move's before and after.</summary>
    public static object Slot(Reservation reservation) => new
    {
        localDate = reservation.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        localTime = reservation.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        partySize = reservation.PartySize,
        tableId = reservation.DiningTableId,
    };

    /// <summary>Where and when a booking stood before a change, in the same shape.</summary>
    public static object Slot(ReservationStanding standing) => new
    {
        localDate = standing.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        localTime = standing.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        partySize = standing.PartySize,
        tableId = standing.DiningTableId,
    };

    /// <summary>
    /// Seconds from a booking being made to being decided, for "approved in 4 min". Null for a change of a booking
    /// that was already confirmed once - its wait started when it was changed, which is not recorded.
    /// </summary>
    public static int? DecisionSeconds(Reservation reservation, DateTime nowUtc) =>
        reservation.PreviousStartUtc is null && reservation.PreviousPartySize is null
            ? (int)Math.Max(0d, (nowUtc - reservation.CreatedAtUtc).TotalSeconds)
            : null;
}
