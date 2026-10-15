using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Telegram;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Telegram;

/// <summary>
/// The bot's conversation: <c>/start nonce</c> binds a challenge and asks for the contact; the
/// contact verifies it; anything else is explained away.
/// </summary>
/// <remarks>
/// <para>
/// Everything it says comes from <see cref="AuthMessages"/> in the challenge's language - the one
/// the app asked for - falling back to the Telegram client's language when there is no challenge
/// to take it from. Every path ends in one reply and a 200, including the ones that refuse:
/// Telegram retries anything else, and a person who forwarded a dead link deserves a sentence,
/// not silence.
/// </para>
/// <para>
/// What it logs: challenge ids, Telegram user ids and outcomes. Never the number.
/// </para>
/// </remarks>
internal sealed class TelegramUpdateHandler(
    YallaDbContext db,
    IClock clock,
    ITelegramBotClient bot,
    ILogger<TelegramUpdateHandler> logger) : ITelegramUpdateHandler
{
    private const string StartCommand = "/start";

    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Not a message, or one Telegram attributes to nobody: nothing to bind and nobody to answer.
        if (update.Message is not { From: { } from } message)
        {
            return;
        }

        var chatId = message.Chat.Id;
        var fallbackLocale = AuthMessages.Normalise(from.LanguageCode, "en");

        try
        {
            if (message.Contact is { } contact)
            {
                await OnContactAsync(from, chatId, contact, fallbackLocale, ct);
            }
            else if (message.Text is { } text && text.StartsWith(StartCommand, StringComparison.Ordinal))
            {
                await OnStartAsync(from, chatId, text, fallbackLocale, ct);
            }
            else
            {
                await ReplyAsync(chatId, AuthMessages.TelegramUnknownCommand(fallbackLocale), null, ct);
            }
        }
        catch (TelegramApiException ex)
        {
            // The state change, if any, is already saved. Telegram will deliver the next message;
            // it will not deliver this reply, and retrying a chat message is not worth a 500.
            logger.LogWarning("Telegram reply to chat {ChatId} failed: {Reason}", chatId, ex.Message);
        }
    }

    private async Task OnStartAsync(TelegramUser from, long chatId, string text, string fallbackLocale, CancellationToken ct)
    {
        var nonce = text.Length > StartCommand.Length ? text[StartCommand.Length..].Trim() : string.Empty;
        var nowUtc = clock.UtcNow;

        // Hashed here, outside the query: the index is on the hash, and the plaintext never
        // reaches the database or its log.
        var nonceHash = nonce.Length == 0 ? null : Secrets.Hash(nonce);

        var challenge = nonceHash is null
            ? null
            : await db.TelegramVerifications.FirstOrDefaultAsync(v => v.NonceHash == nonceHash, ct);

        // Unknown, expired, spent or somebody else's: one sentence for all of them. Which one it was
        // is not something the holder of a forwarded link gets to find out.
        if (challenge is null || !challenge.TryBind(from.Id, chatId, nowUtc))
        {
            if (challenge is not null && challenge.Status == TelegramVerificationStatus.Pending && nowUtc >= challenge.ExpiresAtUtc)
            {
                challenge.MarkExpired(nowUtc);
                await db.SaveChangesAsync(ct);
            }

            logger.LogInformation(
                "Telegram /start refused for user {TelegramUserId}: {Reason}.",
                from.Id, challenge is null ? "no such challenge" : $"challenge {challenge.Id} is {challenge.Status} or bound to another user");

            await ReplyAsync(chatId, AuthMessages.TelegramLinkExpired(challenge?.LocaleCode ?? fallbackLocale), null, ct);

            return;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Telegram challenge {ChallengeId} bound to user {TelegramUserId}.", challenge.Id, from.Id);

        await ReplyAsync(
            chatId,
            AuthMessages.TelegramAskForContact(challenge.LocaleCode),
            new TelegramRequestContactKeyboard(AuthMessages.TelegramShareContactButton(challenge.LocaleCode)),
            ct);
    }

    private async Task OnContactAsync(TelegramUser from, long chatId, TelegramContact contact, string fallbackLocale, CancellationToken ct)
    {
        var nowUtc = clock.UtcNow;

        // The newest live challenge this account opened. Older ones for the same account are
        // simply never verified; the app only ever polls the one it started last.
        var challenge = await db.TelegramVerifications
            .Where(v => v.TelegramUserId == from.Id
                        && v.Status == TelegramVerificationStatus.Pending
                        && v.ExpiresAtUtc > nowUtc)
            .OrderByDescending(v => v.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (challenge is null)
        {
            await ReplyAsync(chatId, AuthMessages.TelegramLinkExpired(fallbackLocale), null, ct);

            return;
        }

        // The one check that makes this a proof: the card is the sender's own. Telegram sets
        // contact.user_id to the account the number belongs to, and only the "share my contact"
        // button produces a card where that is the sender. A forwarded card, or a card for a
        // number with no Telegram account, names somebody else or nobody.
        if (contact.UserId != from.Id)
        {
            logger.LogInformation(
                "Telegram challenge {ChallengeId}: contact from user {TelegramUserId} was not their own card.",
                challenge.Id, from.Id);

            await ReplyAsync(chatId, AuthMessages.TelegramContactMismatch(challenge.LocaleCode), null, ct);

            return;
        }

        string phone;

        try
        {
            // Telegram sends the number as digits, sometimes with the plus and sometimes without.
            // The same normaliser the code flow uses, so the two doors agree on what a number is.
            var raw = contact.PhoneNumber.Trim();
            phone = PhoneNumber.Normalise(raw.StartsWith('+') ? raw : "+" + raw, "phoneNumber");
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "Telegram challenge {ChallengeId}: the contact's number was not a usable E.164 number.", challenge.Id);

            await ReplyAsync(chatId, AuthMessages.TelegramContactMismatch(challenge.LocaleCode), null, ct);

            return;
        }

        try
        {
            challenge.Verify(phone, nowUtc);
        }
        catch (DomainStateException)
        {
            await ReplyAsync(chatId, AuthMessages.TelegramLinkExpired(challenge.LocaleCode), null, ct);

            return;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Telegram challenge {ChallengeId} verified by user {TelegramUserId}.", challenge.Id, from.Id);

        // Two replies, because Telegram allows one markup per message: the first takes the contact
        // keyboard away, the second carries the button that opens the app on this challenge so it
        // resumes polling even when the person came to Telegram from somewhere else.
        await ReplyAsync(chatId, AuthMessages.TelegramVerified(challenge.LocaleCode), new TelegramRemoveKeyboard(), ct);

        await ReplyAsync(
            chatId,
            AuthMessages.TelegramOpenApp(challenge.LocaleCode),
            new TelegramInlineLinkKeyboard(AuthMessages.TelegramOpenAppButton(challenge.LocaleCode), AppLink(challenge.Id)),
            ct);
    }

    /// <summary>The app's deep link back into this challenge: <c>yalla://verify?tg={challengeId}</c>.</summary>
    internal static string AppLink(Guid challengeId) => $"yalla://verify?tg={challengeId}";

    private Task ReplyAsync(long chatId, string text, TelegramReplyMarkup? markup, CancellationToken ct) =>
        bot.SendMessageAsync(chatId, text, markup, ct);
}
