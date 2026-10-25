using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

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
