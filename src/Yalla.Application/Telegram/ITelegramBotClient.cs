namespace Yalla.Application.Telegram;

/// <summary>
/// The three Bot API calls the verification bot makes. Nothing else.
/// </summary>
/// <remarks>
/// An interface rather than a library: the surface is three methods, the tests need a bot that
/// records instead of sending, and the one thing that must be true of the real implementation -
/// that the bot token never reaches a log line or an exception message - is easier to prove of
/// forty lines than of a package.
/// </remarks>
public interface ITelegramBotClient
{
    /// <summary>Sends a text message to a chat, optionally with a reply keyboard or its removal.</summary>
    Task SendMessageAsync(long chatId, string text, TelegramReplyMarkup? replyMarkup, CancellationToken ct);

    /// <summary>
    /// Points Telegram at <paramref name="url"/>, to be called with
    /// <c>X-Telegram-Bot-Api-Secret-Token: <paramref name="secretToken"/></c> on every update.
    /// </summary>
    Task SetWebhookAsync(string url, string secretToken, CancellationToken ct);

    /// <summary>
    /// Long-polls for updates after <paramref name="offset"/>, waiting up to
    /// <paramref name="timeoutSeconds"/> for one to arrive. Development only; see the hosted service.
    /// </summary>
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct);
}
