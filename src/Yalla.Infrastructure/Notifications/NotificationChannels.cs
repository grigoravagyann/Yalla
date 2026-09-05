using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>Which channel is wired up, and how to reach Expo.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// <c>Expo</c> or <c>Log</c>. Same selection pattern as Prompt 3's verification-code sender.
    /// </summary>
    /// <remarks>
    /// <c>Log</c> is the default because it is what makes the scheduler runnable and testable on a
    /// machine with no phone attached to it. Switching to <c>Expo</c> needs no code change and no
    /// deployment - Expo's push service is reachable from localhost.
    /// </remarks>
    public string Channel { get; set; } = "Log";

    /// <summary>Expo's push endpoint. Overridable so a test can point it at a stub.</summary>
    public string ExpoPushUrl { get; set; } = "https://exp.host/--/api/v2/push/send";

    /// <summary>Optional Expo access token, for projects with push security switched on.</summary>
    public string? ExpoAccessToken { get; set; }
}

/// <summary>
/// Writes the rendered message and its target to the log instead of sending it.
/// </summary>
/// <remarks>
/// <para>
/// Not a stub that throws away its argument. It resolves the diner's real devices and logs exactly
/// what each one would have received, in the language it would have received it - so the whole
/// scheduler, including locale selection, is exercised end to end without a phone in your hand.
/// </para>
/// <para>
/// It reports delivery, so a message dispatched through it is marked sent and not retried. A
/// development channel that failed would fill the dead-letter list with noise and hide the real
/// failures.
/// </para>
/// </remarks>
internal sealed class LoggingNotificationChannel(
    YallaDbContext db,
    IClock clock,
    ILogger<LoggingNotificationChannel> logger) : INotificationChannel
{
    public string Name => "Log";

    public async Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var devices = await LiveDevices.ForAsync(db, message.DinerUserId, cancellationToken);

        if (devices.Count == 0)
        {
            logger.LogInformation(
                "[notification] {Title} - {Body} (diner {DinerUserId} has no live device)",
                message.Title, message.Body, message.DinerUserId);

            return [];
        }

        foreach (var device in devices)
        {
            device.Touch(clock.UtcNow);

            logger.LogInformation(
                "[notification -> {Platform} {Token} ({Locale})] {Title} - {Body} | data: {Data}",
                device.Platform,
                Redact(device.PushToken),
                device.Locale,
                message.Title,
                message.Body,
                JsonSerializer.Serialize(message.Data));
        }

        await db.SaveChangesAsync(cancellationToken);

        return [.. devices.Select(d => new NotificationDelivery(d.Id, d.PushToken, true, false, null))];
    }

    /// <summary>A push token is a credential. The tail is enough to tell two devices apart.</summary>
    private static string Redact(string token) =>
        token.Length <= 12 ? "***" : $"***{token[^8..]}";
}

/// <summary>
/// Sends through Expo's push service, and acts on what it says back.
/// </summary>
/// <remarks>
/// <para>
/// Reachable from localhost with no deployment, which is why it is the one real channel in this
/// task: a phone running the diner app through Expo Go receives these from a laptop.
/// </para>
/// <para>
/// <b>A <c>DeviceNotRegistered</c> ticket revokes the token rather than retrying it.</b> That
/// response means the app was uninstalled, so no number of retries will ever succeed - and a queue
/// that retries dead tokens for ever is how push queues fill with garbage that hides the real
/// failures.
/// </para>
/// </remarks>
internal sealed class ExpoNotificationChannel(
    YallaDbContext db,
    IClock clock,
    HttpClient http,
    NotificationOptions options,
    ILogger<ExpoNotificationChannel> logger) : INotificationChannel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "Expo";

    public async Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var devices = await LiveDevices.ForAsync(db, message.DinerUserId, cancellationToken);

        if (devices.Count == 0)
        {
            return [];
        }

        var payload = devices.Select(d => new ExpoPush(
            d.PushToken, message.Title, message.Body, message.Data, message.CategoryId)).ToList();

        using var request = new HttpRequestMessage(HttpMethod.Post, options.ExpoPushUrl)
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        if (!string.IsNullOrWhiteSpace(options.ExpoAccessToken))
        {
            request.Headers.Authorization = new("Bearer", options.ExpoAccessToken);
        }

        using var response = await http.SendAsync(request, cancellationToken);

        // A transport failure is worth retrying, so it throws and the outbox backs off. A per-ticket
        // error is not: those are handled below, one device at a time.
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ExpoResponse>(Json, cancellationToken)
                   ?? throw new InvalidOperationException("Expo returned no body.");

        var results = new List<NotificationDelivery>(devices.Count);

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var ticket = i < body.Data.Count ? body.Data[i] : null;

            if (ticket is null)
            {
                results.Add(new NotificationDelivery(
                    device.Id, device.PushToken, false, false, "Expo returned fewer tickets than tokens."));

                continue;
            }

            if (string.Equals(ticket.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                device.Touch(clock.UtcNow);
                results.Add(new NotificationDelivery(device.Id, device.PushToken, true, false, null));

                continue;
            }

            // The one error worth acting on rather than retrying: the app is gone.
            var dead = string.Equals(
                ticket.Details?.Error, "DeviceNotRegistered", StringComparison.OrdinalIgnoreCase);

            if (dead)
            {
                device.Revoke("Expo reported DeviceNotRegistered.", clock.UtcNow);

                logger.LogInformation(
                    "Revoked push token for device {DeviceId}: Expo says the app is no longer installed.",
                    device.Id);
            }

            results.Add(new NotificationDelivery(
                device.Id, device.PushToken, false, dead, ticket.Message ?? ticket.Status));
        }

        await db.SaveChangesAsync(cancellationToken);

        return results;
    }

    private sealed record ExpoPush(
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data,
        [property: JsonPropertyName("categoryId")] string? CategoryId);

    private sealed record ExpoResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<ExpoTicket> Data);

    private sealed record ExpoTicket(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("details")] ExpoTicketDetails? Details);

    private sealed record ExpoTicketDetails(
        [property: JsonPropertyName("error")] string? Error);
}

/// <summary>The devices a message can actually reach. Shared, so both channels agree on "live".</summary>
internal static class LiveDevices
{
    public static async Task<List<DinerDevice>> ForAsync(
        YallaDbContext db,
        Guid dinerUserId,
        CancellationToken cancellationToken) =>
        await db.DinerDevices
            .Where(d => d.DinerUserId == dinerUserId && d.RevokedAtUtc == null)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .ToListAsync(cancellationToken);
}
