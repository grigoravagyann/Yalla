namespace Yalla.Infrastructure.Telegram;

/// <summary>
/// The Telegram verification bot, from the <c>Telegram</c> configuration section.
/// </summary>
/// <remarks>
/// <see cref="BotToken"/> and <see cref="WebhookSecret"/> are secrets: user secrets locally,
/// <c>Telegram__BotToken</c> / <c>Telegram__WebhookSecret</c> environment variables elsewhere,
/// never a settings file. The registration refuses to start half-configured - see
/// <c>DependencyInjection.AddTelegramVerification</c>.
/// </remarks>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public const string WebhookMode = "Webhook";

    public const string LongPollingMode = "LongPolling";

    /// <summary>
    /// Whether the Telegram door exists at all. Off, and none of its routes are mapped: the API
    /// has no <c>/api/telegram/webhook</c> and no <c>/api/auth/diner/telegram/*</c>, rather than
    /// routes that answer "not configured".
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The bot token from @BotFather. SECRET.</summary>
    public string? BotToken { get; set; }

    /// <summary>The bot's username without the <c>@</c>, for the <c>https://t.me/{username}?start=</c> link.</summary>
    public string? BotUsername { get; set; }

    /// <summary>
    /// What Telegram sends as <c>X-Telegram-Bot-Api-Secret-Token</c> on every webhook call, and
    /// what the webhook requires. SECRET. Required in <see cref="WebhookMode"/>.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// <see cref="WebhookMode"/> (the default: Telegram calls the API) or <see cref="LongPollingMode"/>
    /// (the API polls Telegram - for a laptop with no public address; refused outside Development).
    /// </summary>
    public string Mode { get; set; } = WebhookMode;

    /// <summary>
    /// The public URL of <c>/api/telegram/webhook</c>. When set in <see cref="WebhookMode"/>, the
    /// host registers it with Telegram at startup; when blank, somebody has run <c>setWebhook</c>
    /// by hand and the host leaves it alone.
    /// </summary>
    public string? WebhookPublicUrl { get; set; }

    /// <summary>Telegram's API origin. Overridable so a test can point the client at a stub.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>How long a deep link stays usable.</summary>
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the app should wait between claims, as told in the start response.</summary>
    public int PollAfterMs { get; set; } = 2000;

    /// <summary>The long-poll wait handed to <c>getUpdates</c>. Telegram allows up to 50.</summary>
    public int LongPollTimeoutSeconds { get; set; } = 25;

    public bool IsLongPolling => string.Equals(Mode, LongPollingMode, StringComparison.OrdinalIgnoreCase);

    public bool IsWebhook => string.Equals(Mode, WebhookMode, StringComparison.OrdinalIgnoreCase);
}
