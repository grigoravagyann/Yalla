using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Application.Telegram;

namespace Yalla.Infrastructure.Telegram;

/// <summary>
/// What the host does for the bot at startup and, in long-polling mode, for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Webhook mode</b> registers the public webhook URL with Telegram once, when one is
/// configured, and then has nothing else to do: Telegram calls the API. <b>Long-polling mode</b>
/// is the laptop case - no public address for Telegram to call - and asks Telegram for updates in
/// a loop, feeding each one to the same <see cref="ITelegramUpdateHandler"/> the webhook uses. It
/// is refused outside Development at registration time, because two instances polling the same
/// bot would each receive half the conversation.
/// </para>
/// <para>
/// A failure here never stops the host. The API serves everything else whether or not Telegram is
/// reachable; what fails is the Telegram door, which is logged.
/// </para>
/// </remarks>
internal sealed class TelegramHostedService(
    IServiceScopeFactory scopes,
    ITelegramBotClient bot,
    TelegramOptions options,
    ILogger<TelegramHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.IsLongPolling)
        {
            await PollAsync(stoppingToken);

            return;
        }

        if (string.IsNullOrWhiteSpace(options.WebhookPublicUrl))
        {
            logger.LogInformation(
                "Telegram verification is on in webhook mode with no Telegram:WebhookPublicUrl; "
                + "the webhook is assumed to be registered already.");

            return;
        }

        try
        {
            await bot.SetWebhookAsync(options.WebhookPublicUrl, options.WebhookSecret!, stoppingToken);

            logger.LogInformation("Telegram webhook registered at {WebhookUrl}.", options.WebhookPublicUrl);
        }
        catch (TelegramApiException ex)
        {
            logger.LogError(
                "Telegram setWebhook failed; the bot will not receive updates until it succeeds: {Reason}", ex.Message);
        }
    }

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning(
            "Telegram verification is LONG-POLLING api.telegram.org. That is the local-development "
            + "mode: one process, no public address. Use Webhook mode anywhere that is deployed.");

        long? offset = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdatesAsync(offset, options.LongPollTimeoutSeconds, stoppingToken);

                foreach (var update in updates)
                {
                    // Acknowledged whatever happens to it: an update that throws every time would
                    // otherwise be fetched for ever and block everything behind it.
                    offset = update.UpdateId + 1;

                    try
                    {
                        using var scope = scopes.CreateScope();
                        var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();

                        await handler.HandleAsync(update, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Telegram update {UpdateId} could not be handled.", update.UpdateId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Including TelegramApiException. Back off rather than spin: the usual cause is a
                // laptop that just lost its wifi.
                logger.LogWarning("Telegram getUpdates failed, retrying in 5 s: {Reason}", ex.Message);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
