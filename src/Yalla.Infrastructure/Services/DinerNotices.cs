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
    /// <remarks>
    /// <para>
    /// A decline carries <c>expired</c> - <c>"true"</c> when nobody at the venue answered a short-notice
    /// booking in time, <c>"false"</c> when somebody said no - so the app apologises for the right
    /// thing. Declines written before it existed have no such key and were all somebody's no.
    /// </para>
    /// <para>
    /// It also carries <c>wasChange</c> and <c>originalKept</c>: a declined <i>change</i> of a
    /// confirmed booking either left the booking standing at the slot it had (<c>originalKept</c>
    /// <c>"true"</c>; <c>date</c> and <c>time</c> are that slot) or ended it because that slot had gone
    /// too. A change also carries <c>requestedDate</c> and <c>requestedTime</c>, the slot it asked for.
    /// </para>
    /// </remarks>
    public static void BookingDecided(
        YallaDbContext db,
        Reservation reservation,
        string venueName,
        string branchName,
        bool approved,
        bool expired,
        DateTime atUtc,
        DeclinedChange? change = null,
        Yalla.Domain.Enums.DeclineReason? because = null)
    {
        Dictionary<string, string>? extra = null;

        if (!approved)
        {
            extra = new Dictionary<string, string>
            {
                ["expired"] = expired ? "true" : "false",
                ["wasChange"] = change is not null ? "true" : "false",
                ["originalKept"] = change?.OriginalKept == true ? "true" : "false",
            };

            // Whose no, or whose silence, as the DeclineReason number: "3" the guest with the table
            // before said they would not have left in time, "4" they did not answer.
            if (because is { } reason)
            {
                extra["declinedBecause"] = ((int)reason).ToString(CultureInfo.InvariantCulture);
            }

            if (change is not null)
            {
                extra["requestedDate"] = change.RequestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                extra["requestedTime"] = change.RequestedTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        }

        Booking(
            db,
            approved ? DinerNotificationKinds.BookingConfirmed : DinerNotificationKinds.BookingDeclined,
            reservation,
            venueName,
            branchName,
            atUtc,
            extra);
    }

    /// <summary>
    /// Somebody would like the table after this booking: "will you have left by 19:40?". The entry is the
    /// record; the answer is given on the booking's <c>incomingTableRequest</c>.
    /// </summary>
    /// <remarks>
    /// Carries the booking's own <c>date</c> and <c>time</c> like every booking entry, plus
    /// <c>requestedTime</c> (when the other guest would sit down), <c>freeByTime</c> (the time to have left
    /// by) and <c>requestId</c>. The other guest stays anonymous.
    /// </remarks>
    public static void TableRequested(
        YallaDbContext db,
        Reservation earlier,
        string venueName,
        string branchName,
        Guid requestId,
        TimeOnly requestedLocalTime,
        TimeOnly freeByLocalTime,
        DateTime atUtc) =>
        Booking(
            db,
            DinerNotificationKinds.TableRequest,
            earlier,
            venueName,
            branchName,
            atUtc,
            new Dictionary<string, string>
            {
                ["requestId"] = requestId.ToString(),
                ["requestedTime"] = requestedLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                ["freeByTime"] = freeByLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// The answer to a request for this booking's table took effect: <c>accepted</c> <c>"true"</c> - the
    /// guest promised to leave by <c>freeByTime</c>, by their own yes or the floor's for them - or
    /// <c>"false"</c>, they said no.
    /// </summary>
    public static void TableRequestDecided(
        YallaDbContext db,
        Reservation earlier,
        string venueName,
        string branchName,
        Guid requestId,
        bool accepted,
        TimeOnly requestedLocalTime,
        TimeOnly freeByLocalTime,
        DateTime atUtc) =>
        Booking(
            db,
            DinerNotificationKinds.TableRequestDecided,
            earlier,
            venueName,
            branchName,
            atUtc,
            new Dictionary<string, string>
            {
                ["requestId"] = requestId.ToString(),
                ["accepted"] = accepted ? "true" : "false",
                ["requestedTime"] = requestedLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                ["freeByTime"] = freeByLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            });

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
    /// The venue answered the diner's review. Like <see cref="ReviewHidden"/>, no push: the entry is
    /// written with the reply.
    /// </summary>
    public static void ReviewReplied(
        YallaDbContext db,
        Guid dinerUserId,
        Guid branchId,
        Guid reviewId,
        string venueName,
        string branchName,
        DateTime atUtc) =>
        db.DinerNotifications.Add(new DinerNotification(
            dinerUserId,
            DinerNotificationKinds.ReviewReplied,
            Params(new()
            {
                ["venueName"] = venueName,
                ["branchName"] = branchName,
                ["reviewId"] = reviewId.ToString(),
            }),
            atUtc,
            branchId));

    /// <summary>
    /// Takes back the diner's unread "the venue replied" entry for a branch, in the caller's unit of work,
    /// when the reply it announced is removed - most often the venue's undo a few seconds later.
    /// </summary>
    /// <remarks>
    /// By the branch: a diner has one review per branch, so the author and the branch name the review.
    /// An entry already read is history and stays.
    /// </remarks>
    public static async Task TakeBackUnreadReplyAsync(
        YallaDbContext db, Guid dinerUserId, Guid branchId, CancellationToken cancellationToken)
    {
        var unread = await db.DinerNotifications
            .Where(n => n.DinerUserId == dinerUserId
                        && n.BranchId == branchId
                        && n.Kind == DinerNotificationKinds.ReviewReplied
                        && n.ReadAtUtc == null)
            .ToListAsync(cancellationToken);

        if (unread.Count > 0)
        {
            db.DinerNotifications.RemoveRange(unread);
        }
    }

    /// <summary>
    /// Removes a booking's entries that have not appeared yet - its reminder - in the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// Called beside <c>IOutbox.CancelAsync</c> for the same reason: a reminder for a booking cancelled an
    /// hour ago is worse than none. Also called when a booking is seated or completed, where the push is
    /// left to its handler's status check but the feed has no such check. Entries already shown are
    /// history and stay.
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
        DateTime showAtUtc,
        Dictionary<string, string>? extra = null)
    {
        // A booking made on the web with no account has nobody's feed to go into.
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        var values = new Dictionary<string, string>
        {
            ["venueName"] = venueName,
            ["branchName"] = branchName,
            ["date"] = reservation.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["time"] = reservation.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["partySize"] = reservation.PartySize.ToString(CultureInfo.InvariantCulture),
            ["reservationCode"] = reservation.Code,
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                values[key] = value;
            }
        }

        db.DinerNotifications.Add(new DinerNotification(
            dinerUserId,
            kind,
            Params(values),
            showAtUtc,
            reservation.BranchId,
            reservation.Id));
    }

    private static string Params(Dictionary<string, string> values) => JsonSerializer.Serialize(values, Json);
}
