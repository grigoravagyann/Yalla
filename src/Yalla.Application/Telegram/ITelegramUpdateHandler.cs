namespace Yalla.Application.Telegram;

/// <summary>
/// The bot's whole conversation, one update at a time.
/// </summary>
/// <remarks>
/// One handler, two feeds. In production Telegram posts each update to the webhook; in local
/// development a background loop long-polls for them. Both hand what they receive here, so the
/// behaviour a developer sees on a laptop is the behaviour a deployment runs.
/// </remarks>
public interface ITelegramUpdateHandler
{
    /// <summary>
    /// Acts on one update: binds a challenge on <c>/start</c>, verifies it on a contact, and
    /// explains itself otherwise. Never throws for anything the update said; a failure to reach
    /// Telegram with the reply is logged and swallowed, because Telegram would only retry.
    /// </summary>
    Task HandleAsync(TelegramUpdate update, CancellationToken ct);
}
