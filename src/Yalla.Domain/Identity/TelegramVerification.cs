using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Identity;

/// <summary>
/// One attempt to prove a phone number through the Telegram bot: a single-use deep link, bound to
/// the first Telegram account that opens it, verified by that account's own contact card, and
/// claimed once by the app that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// The SMS code proves a number by sending something to it. This proves a number by asking
/// Telegram, which already verified it when the account was created, to hand it over - a contact
/// card that Telegram marks as the sender's own. Nothing is sent to the number and nothing is
/// typed, so there is no guess budget to protect; what has to hold instead is <b>who</b> the
/// proof came from.
/// </para>
/// <para>
/// Two bindings make that hold. The nonce in the link is stored only as a hash and binds to the
/// first Telegram user who presents it, so a forwarded link is dead on arrival for everyone else.
/// And the contact that verifies it must be that same user's own card - Telegram's
/// <c>contact.user_id</c> equals the sender - so forwarding somebody else's card proves nothing.
/// </para>
/// <para>
/// The app never sees the number until it claims the challenge, and claims are answered
/// identically for unknown, expired and spent ids - see <c>TelegramVerificationExpiredException</c>.
/// </para>
/// </remarks>
public sealed class TelegramVerification : Entity
{
    /// <summary>
    /// How long a verified challenge stays claimable after the contact arrives, whatever the
    /// original deadline said.
    /// </summary>
    /// <remarks>
    /// The app polls every couple of seconds, so a contact that arrives ten seconds before the
    /// deadline would otherwise be verified and unclaimable in the same breath. Two minutes is
    /// long enough for the app to come back from the background, short enough that a verified
    /// challenge nobody claims does not sit around.
    /// </remarks>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(2);

    /// <summary>SHA-256 of the start parameter in the deep link. Never the nonce.</summary>
    public string NonceHash { get; private set; } = null!;

    public TelegramVerificationStatus Status { get; private set; }

    /// <summary>The Telegram account that opened the link first. Null until somebody does.</summary>
    public long? TelegramUserId { get; private set; }

    /// <summary>The private chat with that account, where the bot's replies go.</summary>
    public long? ChatId { get; private set; }

    /// <summary>The number Telegram handed over, in E.164. Set only on verification.</summary>
    public string? PhoneE164 { get; private set; }

    /// <summary>Language the app asked for; the bot speaks it and the account is created in it.</summary>
    public string LocaleCode { get; private set; } = null!;

    /// <summary>The address the app started from, for abuse diagnostics only.</summary>
    public string? RequestedFromAddress { get; private set; }

    /// <summary>
    /// The diner account whose token the start request carried, or null for an anonymous start.
    /// The claim must come from the same account, or from nobody, if this is set.
    /// </summary>
    public Guid? CallerDinerUserId { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? VerifiedAtUtc { get; private set; }

    public DateTime? ConsumedAtUtc { get; private set; }

    private TelegramVerification()
    {
    }

    public TelegramVerification(
        string nonceHash,
        string localeCode,
        DateTime issuedAtUtc,
        TimeSpan lifetime,
        string? requestedFromAddress,
        Guid? callerDinerUserId)
        : base(Guid.CreateVersion7())
    {
        NonceHash = Guard.NotBlank(nonceHash, nameof(nonceHash), FieldLengths.TokenHash);
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A challenge needs a positive lifetime.");
        }

        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(lifetime);
        RequestedFromAddress =
            Guard.OptionalText(requestedFromAddress, nameof(requestedFromAddress), FieldLengths.ClientAddress);
        CallerDinerUserId = callerDinerUserId;
        Status = TelegramVerificationStatus.Pending;
    }

    /// <summary>Whether the link can still be opened or, once bound, can still receive a contact.</summary>
    public bool IsPendingAt(DateTime atUtc) =>
        Status == TelegramVerificationStatus.Pending && atUtc < ExpiresAtUtc;

    /// <summary>Whether the app can turn this into a sign-in right now.</summary>
    public bool IsClaimableAt(DateTime atUtc) =>
        Status == TelegramVerificationStatus.Verified && atUtc < ExpiresAtUtc;

    /// <summary>
    /// Binds the challenge to the Telegram account presenting the nonce.
    /// </summary>
    /// <returns>
    /// True when this account now holds the challenge - including the same account opening the
    /// link again. False when the challenge is not pending, or is already another account's.
    /// </returns>
    public bool TryBind(long telegramUserId, long chatId, DateTime atUtc)
    {
        if (!IsPendingAt(Guard.NotLocalTime(atUtc, nameof(atUtc))))
        {
            return false;
        }

        if (TelegramUserId is { } bound)
        {
            return bound == telegramUserId;
        }

        TelegramUserId = telegramUserId;
        ChatId = chatId;

        return true;
    }

    /// <summary>
    /// Records the number the bound account handed over. The caller has already checked that the
    /// contact is that account's own.
    /// </summary>
    /// <exception cref="DomainStateException">Not pending, not bound, or past its deadline.</exception>
    public void Verify(string phoneE164, DateTime atUtc)
    {
        var verifiedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        if (TelegramUserId is null)
        {
            throw new DomainStateException("The challenge has not been opened by anyone, so there is nobody to take a contact from.");
        }

        if (!IsPendingAt(verifiedAtUtc))
        {
            throw new DomainStateException($"The challenge is {Status} and cannot be verified.");
        }

        PhoneE164 = Guard.NotBlank(phoneE164, nameof(phoneE164), FieldLengths.PhoneE164);
        VerifiedAtUtc = verifiedAtUtc;
        Status = TelegramVerificationStatus.Verified;

        // Never earlier than it was, and at least the grace from now - see ClaimGrace.
        var graceEnd = verifiedAtUtc.Add(ClaimGrace);

        if (graceEnd > ExpiresAtUtc)
        {
            ExpiresAtUtc = graceEnd;
        }
    }

    /// <summary>Spends a verified challenge. Exactly once.</summary>
    /// <exception cref="DomainStateException">Not verified, already consumed, or past its deadline.</exception>
    public void Consume(DateTime atUtc)
    {
        var consumedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        if (!IsClaimableAt(consumedAtUtc))
        {
            throw new DomainStateException($"The challenge is {Status} and cannot be claimed.");
        }

        ConsumedAtUtc = consumedAtUtc;
        Status = TelegramVerificationStatus.Consumed;
    }

    /// <summary>
    /// Retires a challenge found past its deadline. A finished one - consumed or already
    /// expired - is left as it is, so the record of what happened to it survives.
    /// </summary>
    public void MarkExpired(DateTime atUtc)
    {
        Guard.NotLocalTime(atUtc, nameof(atUtc));

        if (Status is TelegramVerificationStatus.Pending or TelegramVerificationStatus.Verified)
        {
            Status = TelegramVerificationStatus.Expired;
        }
    }
}
