using System.Security.Cryptography;
using System.Text;
using Yalla.Api.ApplicationExtensions;
using Yalla.Application.Auth;
using Yalla.Application.Telegram;
using Yalla.Infrastructure.Telegram;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The second way a diner proves a phone number: the Telegram bot.
/// </summary>
/// <remarks>
/// <para>
/// Three routes. <c>start</c> and <c>claim</c> are the app's, under <c>/api/auth</c> beside the
/// code flow they are an alternative to; the webhook is Telegram's, under its own prefix. All
/// three are mapped only when <c>Telegram:Enabled</c> is true: switched off, they do not exist,
/// rather than existing and saying "not configured". See <c>docs/auth.md</c>, "Telegram
/// verification".
/// </para>
/// <para>
/// All three are anonymous. Start and claim for the same reason <c>request-code</c> and
/// <c>verify-code</c> are - the person has no token yet, and the challenge id is the credential.
/// The webhook because Telegram's servers hold no Yalla token; the secret header, compared in
/// constant time, is theirs.
/// </para>
/// </remarks>
public static class TelegramEndpoints
{
    /// <summary>The header Telegram sets to the value given to <c>setWebhook</c>.</summary>
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    public static WebApplication MapTelegramEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<TelegramOptions>();

        if (!options.Enabled)
        {
            app.Logger.LogInformation(
                "Telegram verification is off (Telegram:Enabled is false); its routes are not mapped.");

            return app;
        }

        var auth = app.MapGroup("/api/auth/diner/telegram")
            .WithTags(EndpointConventions.AuthTag)
            .AllowAnonymous();

        auth.MapPost("/start", StartAsync)
            .WithName("startTelegramVerification")
            .WithSummary("Start proving a phone number through the Telegram bot")
            .WithDescription(
                "Issues a challenge and a single-use deep link into the bot. Open `deepLinkUrl`; "
                + "Telegram starts the bot, which asks the person to share their contact. Then poll "
                + "`claim` with `challengeId` every `pollAfterMs` until it answers `verified`.\n\n"
                + "**The number never passes through the app.** Telegram hands the bot the number it "
                + "verified when the account was created, and only the sender's own contact card "
                + "counts - a forwarded card does not.\n\n"
                + "Send the `Authorization: Bearer` token `register` returned when verifying a "
                + "registered number from the same app, exactly as for `verify-code`: the claim then "
                + "keeps the password. Without it the registrant is displaced. The route stays "
                + "anonymous; an invalid token is ignored.\n\n"
                + "Rate limited per address like `request-code`. Not mapped at all when the Telegram "
                + "door is switched off in configuration.")
            .Produces<TelegramVerificationStart>()
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests, "Too many starts from this address. Wait, then retry.")
            .RequireRateLimiting(RateLimitingExtensions.CodeRequestPolicy);

        auth.MapPost("/{challengeId:guid}/claim", ClaimAsync)
            .WithName("claimTelegramVerification")
            .WithSummary("Poll a Telegram verification and, once verified, exchange it for tokens")
            .WithDescription(
                "`pending` while the bot has not been handed the number; `verified` exactly once, "
                + "with `signIn` set to **exactly what `verify-code` returns** - the account is "
                + "created on a first number, a registered number's account is signed in and its "
                + "number marked verified, with the same displacement rule - and `phoneE164`, the "
                + "number Telegram handed over, so the app can show it without reading the profile. "
                + "After that the challenge is spent.\n\n"
                + "Unknown, expired and spent challenges - and one started under a different account "
                + "than the one claiming it - all answer **410 `telegram-verification-expired`**: "
                + "start again. Links last five minutes; a verified challenge stays claimable for two "
                + "more.\n\n"
                + "Rate limited per address by its own `auth-telegram-claim` policy, sized for one "
                + "app polling as told.")
            .Produces<TelegramVerificationClaim>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "`invalid-credentials`: the number was proved but its account is deactivated. The "
                + "challenge is spent and no token is issued.")
            .ProducesProblemDetails(
                StatusCodes.Status410Gone,
                "`telegram-verification-expired`: the challenge is unknown, past its deadline, already "
                + "claimed, or not this caller's. Start again.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests, "Polling faster than `pollAfterMs`. Slow down.")
            .RequireRateLimiting(RateLimitingExtensions.TelegramClaimPolicy);

        app.MapPost("/api/telegram/webhook", WebhookAsync)
            .WithTags(EndpointConventions.AuthTag)
            .AllowAnonymous()
            .WithName("telegramWebhook")
            .WithSummary("Telegram's webhook - not for clients")
            .WithDescription(
                "Called by Telegram's servers with one `Update` per message to the bot. Requires the "
                + "`X-Telegram-Bot-Api-Secret-Token` header to equal the configured webhook secret; "
                + "anything else is a bare 401. Always answers 200 once the header is right, "
                + "including for a body it cannot read, so Telegram does not retry it.")
            .ExcludeFromDescription()
            .RequireRateLimiting(RateLimitingExtensions.TelegramWebhookPolicy);

        return app;
    }

    private static async Task<IResult> StartAsync(
        StartTelegramVerificationRequest request,
        ITelegramVerificationService service,
        Application.Abstractions.ICurrentActor actor,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        // As verify-code: a valid diner token on the anonymous route names the account that will
        // keep its password when the claim proves the number. Anything else is nobody.
        var result = await service.StartAsync(
            request.LocaleCode ?? http.Request.Headers.AcceptLanguage.ToString(),
            http.Connection.RemoteIpAddress?.ToString(),
            http.User.Identity?.IsAuthenticated == true ? actor.DinerUserId : null,
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ClaimAsync(
        Guid challengeId,
        ClaimTelegramVerificationRequest request,
        ITelegramVerificationService service,
        Application.Abstractions.ICurrentActor actor,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await service.ClaimAsync(
            challengeId,
            request.LocaleCode ?? http.Request.Headers.AcceptLanguage.ToString(),
            http.User.Identity?.IsAuthenticated == true ? actor.DinerUserId : null,
            cancellationToken);

        return Results.Ok(result);
    }

    /// <summary>
    /// Telegram's call. The body is read raw - it is Telegram's shape, not ours - and the secret is
    /// compared in constant time, so a wrong guess costs the same as a nearly right one.
    /// </summary>
    private static async Task<IResult> WebhookAsync(
        HttpContext http,
        TelegramOptions options,
        ITelegramUpdateHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Yalla.Api.Telegram.Webhook");

        if (!SecretMatches(http.Request.Headers[SecretHeader].ToString(), options.WebhookSecret))
        {
            // A bare 401 with no body: a forged caller learns nothing, not even the error envelope.
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        string body;

        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var update = TelegramUpdate.TryParse(body);

        if (update is null)
        {
            // Not an update. Acknowledged anyway: refusing it would only make Telegram send it again.
            logger.LogWarning("Telegram webhook received a body that is not an Update; ignored.");

            return Results.Ok();
        }

        try
        {
            await handler.HandleAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged here and swallowed: the one thing this endpoint must do is answer 200, or
            // Telegram queues the update and redelivers it for up to a day.
            logger.LogError(ex, "Telegram update {UpdateId} could not be handled.", update.UpdateId);
        }

        return Results.Ok();
    }

    private static bool SecretMatches(string presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);

        return expectedBytes.Length == presentedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
