using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Writes the diner's notifications feed (K12), one method per kind, into the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Called beside <c>IOutbox.Enqueue</c></b>, and like it, never saves: the entry commits with the
/// change that caused it and with the push that tells the diner about it, or not at all. It is written
/// whether or not the diner has a device, because the feed is what a diner without push reads.
/// </para>
/// <para>
/// <b>Two kinds have no push to sit beside.</b> A booking the venue lets go
/// (<see cref="BookingCancelledByVenue"/>) and a review moderation takes down (<see cref="ReviewHidden"/>)
/// have never sent a push; their entries are written where the change itself is saved, in the same way.
/// </para>
/// <para>
/// A static helper over the context rather than a service, so the services that write the feed - built
/// by hand in the integration fixture as well as by the container - need no new constructor argument.
/// </para>
/// </remarks>
internal static class DinerNotices
{
    /// <summary>Web defaults, with letters in any script left as they are rather than escaped.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A booking reminder, appearing when the reminder push is due.</summary>
    public static void BookingReminder(
        YallaDbContext db, Reservation reservation, string venueName, string branchName, DateTime dueAtUtc) =>
        Booking(db, DinerNotificationKinds.BookingReminder, reservation, venueName, branchName, dueAtUtc);

    /// <summary>A pending booking approved or declined.</summary>
    public static void BookingDecided(
        YallaDbContext db, Reservation reservation, string venueName, string branchName, bool approved, DateTime atUtc) =>
        Booking(
            db,
            approved ? DinerNotificationKinds.BookingConfirmed : DinerNotificationKinds.BookingDeclined,
            reservation,
            venueName,
            branchName,
            atUtc);

    /// <summary>An accepted booking the venue let go.</summary>
    public static void BookingCancelledByVenue(
        YallaDbContext db, Reservation reservation, string venueName, string branchName, DateTime atUtc) =>
        Booking(db, DinerNotificationKinds.BookingCancelledByVenue, reservation, venueName, branchName, atUtc);

    /// <summary>The diner's order is ready.</summary>
    public static void OrderReady(
        YallaDbContext db,
        Guid dinerUserId,
        Guid branchId,
        string venueName,
        string branchName,
        Guid tabId,
        Guid orderId,
        string tableLabel,
        DateTime atUtc) =>
        db.DinerNotifications.Add(new DinerNotification(
            dinerUserId,
            DinerNotificationKinds.OrderReady,
            Params(new()
            {
                ["venueName"] = venueName,
                ["branchName"] = branchName,
                ["tableLabel"] = tableLabel,
            }),
            atUtc,
            branchId,
            tabId: tabId,
            orderId: orderId));

    /// <summary>The diner's review was taken down.</summary>
    public static void ReviewHidden(
        YallaDbContext db,
        Guid dinerUserId,
        Guid branchId,
        Guid reviewId,
        string venueName,
        string branchName,
        DateTime atUtc) =>
        db.DinerNotifications.Add(new DinerNotification(
            dinerUserId,
            DinerNotificationKinds.ReviewHidden,
            Params(new()
            {
                ["venueName"] = venueName,
                ["branchName"] = branchName,
                ["reviewId"] = reviewId.ToString(),
            }),
            atUtc,
            branchId));

    /// <summary>
    /// Removes a booking's entries that have not appeared yet - its reminder - in the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// Called beside <c>IOutbox.CancelAsync</c> for the same reason: a reminder for a booking cancelled an
    /// hour ago is worse than none. Entries already shown are history and stay.
    /// </remarks>
    public static async Task CancelUnshownAsync(
        YallaDbContext db, Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var unshown = await db.DinerNotifications
            .Where(n => n.ReservationId == reservationId && n.CreatedAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

        if (unshown.Count > 0)
        {
            db.DinerNotifications.RemoveRange(unshown);
        }
    }

    private static void Booking(
        YallaDbContext db,
        string kind,
        Reservation reservation,
        string venueName,
        string branchName,
        DateTime showAtUtc)
    {
        // A booking made on the web with no account has nobody's feed to go into.
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        db.DinerNotifications.Add(new DinerNotification(
            dinerUserId,
            kind,
            Params(new()
            {
                ["venueName"] = venueName,
                ["branchName"] = branchName,
                ["date"] = reservation.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["time"] = reservation.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                ["partySize"] = reservation.PartySize.ToString(CultureInfo.InvariantCulture),
                ["reservationCode"] = reservation.Code,
            }),
            showAtUtc,
            reservation.BranchId,
            reservation.Id));
    }

    private static string Params(Dictionary<string, string> values) => JsonSerializer.Serialize(values, Json);
}
