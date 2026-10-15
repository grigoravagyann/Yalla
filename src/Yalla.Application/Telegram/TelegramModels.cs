using System.Text.Json;

namespace Yalla.Application.Telegram;

/// <summary>
/// One Telegram <c>Update</c>, reduced to exactly what the verification bot reads.
/// </summary>
/// <remarks>
/// Telegram's update object is large and grows every release. Only a message's sender, chat, text
/// and contact matter here, so only those are modelled - and parsed leniently, because an update
/// of a kind this bot does not handle (an edited message, a channel post) must be acknowledged
/// and ignored rather than refused, or Telegram retries it for a day.
/// </remarks>
/// <param name="UpdateId">Telegram's sequence number, used as the long-polling offset.</param>
/// <param name="Message">The message, or null for an update of some other kind.</param>
public sealed record TelegramUpdate(long UpdateId, TelegramMessage? Message)
{
    /// <summary>
    /// Parses an update, or returns null when the body is not JSON or not an update at all.
    /// </summary>
    public static TelegramUpdate? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return TryParse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses one update out of an element already read - a member of <c>getUpdates</c>' result.</summary>
    public static TelegramUpdate? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("update_id", out var updateId)
            || !updateId.TryGetInt64(out var id))
        {
            return null;
        }

        return new TelegramUpdate(id, ParseMessage(element));
    }

    private static TelegramMessage? ParseMessage(JsonElement update)
    {
        if (!update.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("id", out var chatId)
            || !chatId.TryGetInt64(out var chatIdValue))
        {
            return null;
        }

        TelegramUser? from = null;

        if (message.TryGetProperty("from", out var sender)
            && sender.ValueKind == JsonValueKind.Object
            && sender.TryGetProperty("id", out var senderId)
            && senderId.TryGetInt64(out var senderIdValue))
        {
            from = new TelegramUser(senderIdValue, OptionalString(sender, "language_code"));
        }

        TelegramContact? contact = null;

        if (message.TryGetProperty("contact", out var card)
            && card.ValueKind == JsonValueKind.Object
            && OptionalString(card, "phone_number") is { } phone)
        {
            long? userId = card.TryGetProperty("user_id", out var cardUser) && cardUser.TryGetInt64(out var cardUserValue)
                ? cardUserValue
                : null;

            contact = new TelegramContact(phone, userId);
        }

        return new TelegramMessage(from, new TelegramChat(chatIdValue), OptionalString(message, "text"), contact);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <param name="From">Who sent it. Null for messages Telegram attributes to nobody (a channel).</param>
/// <param name="Chat">Where to reply.</param>
/// <param name="Text">The text, for <c>/start</c>. Null for a contact message.</param>
/// <param name="Contact">The shared contact card, when there is one.</param>
public sealed record TelegramMessage(TelegramUser? From, TelegramChat Chat, string? Text, TelegramContact? Contact);

/// <param name="Id">Telegram's user id.</param>
/// <param name="LanguageCode">The user's Telegram client language, as an IETF tag, when Telegram sends one.</param>
public sealed record TelegramUser(long Id, string? LanguageCode);

public sealed record TelegramChat(long Id);

/// <param name="PhoneNumber">As Telegram sends it - digits, with or without a leading plus.</param>
/// <param name="UserId">
/// The Telegram account the card belongs to, when it has one. Equal to the sender's id only for
/// the sender's own card, which is the one thing that proves the number is theirs.
/// </param>
public sealed record TelegramContact(string PhoneNumber, long? UserId);

/// <summary>What the bot attaches to a reply: a keyboard, or the instruction to take one away.</summary>
public abstract record TelegramReplyMarkup;

/// <summary>A one-button reply keyboard whose button asks Telegram to share the user's own contact.</summary>
/// <param name="ButtonText">The button's label, in the person's language.</param>
public sealed record TelegramRequestContactKeyboard(string ButtonText) : TelegramReplyMarkup;

/// <summary>Removes the reply keyboard once it has done its job.</summary>
public sealed record TelegramRemoveKeyboard : TelegramReplyMarkup;
