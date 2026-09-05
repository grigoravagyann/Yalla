using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// What the outbox carries for each message type.
/// </summary>
/// <remarks>
/// <b>Self-contained on purpose.</b> A payload of ids alone would have to re-read the world at send
/// time, and the world has moved on: the booking may have been cancelled, the venue renamed, the
/// diner's name changed. What was true when the message was written is what the message should say.
/// The one thing read at send time is the <i>state</i> that decides whether to send at all.
/// </remarks>
internal sealed record ReservationNotice(
    Guid ReservationId,
    Guid DinerUserId,
    string VenueName,
    string BranchName,
    string TableLabel,
    TimeOnly LocalStartTime,
    string ReservationCode,
    int GraceExtensionMinutes = 0,
    bool Approved = false);

internal sealed record TabNotice(Guid TabId, Guid ParticipantId, Guid DinerUserId, string TableLabel);

/// <summary>
/// Sends the reminder, if the booking is still worth reminding anybody about.
/// </summary>
/// <remarks>
/// The state check happens here rather than at write time, because hours pass in between. Cancelling
/// a booking deletes its unsent reminder, so this is the belt to that braces - a booking seated early
/// or cancelled by staff through some path that forgot still produces no push.
/// </remarks>
internal sealed class ReservationReminderHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationReminderHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationReminder;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            logger.LogInformation(
                "Skipping the reminder for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.Reminder(
            locale, notice.VenueName, notice.BranchName, notice.TableLabel, notice.LocalStartTime);

        // The action lives in the payload, so the diner cancels from the lock screen. That is the
        // whole feature: cancelling has to be easier than not showing up.
        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-reminder",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["code"] = notice.ReservationCode,
                    ["action"] = "cancel",
                    ["branch"] = notice.BranchName,
                },
                CategoryId: "reservation-reminder"),
            cancellationToken);
    }
}

/// <summary>"Still coming?", with the one-tap hold extension behind it.</summary>
internal sealed class ReservationLateNudgeHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationLateNudgeHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationLateNudge;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        // Seated is the case this exists to avoid. Nudging somebody who is already at the table,
        // eating, is the notification that makes a diner turn them all off.
        if (status != ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "Skipping the late nudge for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.LateNudge(
            locale, notice.VenueName, notice.TableLabel, notice.GraceExtensionMinutes);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-late-nudge",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["action"] = "extend-hold",
                    ["extensionMinutes"] = notice.GraceExtensionMinutes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                },
                CategoryId: "late-nudge"),
            cancellationToken);
    }
}

/// <summary>The manager approved or rejected a booking that was waiting.</summary>
internal sealed class ReservationDecidedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationDecided;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = notice.Approved
            ? NotificationText.ReservationApproved(locale, notice.VenueName, notice.LocalStartTime)
            : NotificationText.ReservationRejected(locale, notice.VenueName);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-decided",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["approved"] = notice.Approved ? "true" : "false",
                }),
            cancellationToken);
    }
}

/// <summary>The host let a pending joiner onto the tab.</summary>
internal sealed class ParticipantApprovedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ParticipantApproved;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.ParticipantApproved(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "participant-approved",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}

/// <summary>Food is up. Per-branch, and off by default - noisy in a cafe, useful in a canteen.</summary>
internal sealed class OrderReadyHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.OrderReady;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.OrderReady(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "order-ready",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}

/// <summary>Shared bits every handler needs.</summary>
internal static class Payload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T Read<T>(string payloadJson) =>
        JsonSerializer.Deserialize<T>(payloadJson, Json)
        ?? throw new InvalidOperationException($"An outbox payload did not deserialise to {typeof(T).Name}.");

    /// <summary>
    /// The language to write in: the diner's most recently used device's.
    /// </summary>
    /// <remarks>
    /// <b>Never the branch's.</b> A Russian-speaking regular at an Armenian venue gets Russian.
    /// Falls back to Armenian when the diner has no device on file, which is also the case where
    /// nothing will be delivered anyway - the fallback is there so the message is still renderable
    /// and loggable rather than throwing.
    /// </remarks>
    public static async Task<string> LocaleForAsync(
        YallaDbContext db,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        var locale = await db.DinerDevices
            .AsNoTracking()
            .Where(d => d.DinerUserId == dinerUserId && d.RevokedAtUtc == null)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .Select(d => d.Locale)
            .FirstOrDefaultAsync(cancellationToken);

        return NotificationText.Normalise(locale);
    }
}
