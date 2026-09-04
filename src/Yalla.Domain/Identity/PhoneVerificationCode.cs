using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// A one-time code sent to a phone number, stored only as a hash.
/// </summary>
/// <remarks>
/// <para>
/// Six digits is 20 bits of entropy, which is only safe because all three of the limits below
/// hold at once: it dies after <see cref="Lifetime"/>, it dies after <see cref="MaxAttempts"/>
/// wrong guesses, and it dies on first success. Drop any one of them and the code is guessable.
/// </para>
/// <para>
/// The code itself is never persisted. A dump of this table cannot be used to sign in as anyone,
/// which is the whole point of hashing something that only lives for five minutes.
/// </para>
/// </remarks>
public sealed class PhoneVerificationCode : Entity
{
    /// <summary>How long a freshly issued code stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How many wrong guesses a code tolerates before it is dead.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The number the code was sent to, in E.164.</summary>
    public string PhoneE164 { get; private set; } = null!;

    /// <summary>Hash of the code. Never the code.</summary>
    public string CodeHash { get; private set; } = null!;

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Verification attempts made against this code, successful or not.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Set the moment the code is used. A code is good exactly once.</summary>
    public DateTime? ConsumedAtUtc { get; private set; }

    /// <summary>The address the code was requested from, for abuse diagnostics only.</summary>
    public string? RequestedFromAddress { get; private set; }

    private PhoneVerificationCode()
    {
    }

    public PhoneVerificationCode(
        string phoneE164,
        string codeHash,
        DateTime issuedAtUtc,
        string? requestedFromAddress = null)
        : base(Guid.CreateVersion7())
    {
        PhoneE164 = Guard.NotBlank(phoneE164, nameof(phoneE164), FieldLengths.PhoneE164);
        CodeHash = Guard.NotBlank(codeHash, nameof(codeHash), FieldLengths.PinHash);
        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(Lifetime);
        RequestedFromAddress =
            Guard.OptionalText(requestedFromAddress, nameof(requestedFromAddress), FieldLengths.ClientAddress);
    }

    /// <summary>Whether this code may still be checked at all.</summary>
    public bool IsUsableAt(DateTime atUtc) =>
        ConsumedAtUtc is null && AttemptCount < MaxAttempts && atUtc < ExpiresAtUtc;

    /// <summary>
    /// Whether the attempt limit is the reason this code is unusable, which the caller reports
    /// differently from "expired" so a diner knows to ask for a new one.
    /// </summary>
    public bool IsAttemptExhausted => AttemptCount >= MaxAttempts;

    /// <summary>Counts a wrong guess. The fifth one kills the code.</summary>
    public void RecordFailedAttempt() => AttemptCount++;

    /// <summary>Counts the successful guess and retires the code.</summary>
    public void Consume(DateTime atUtc)
    {
        AttemptCount++;
        ConsumedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    /// <summary>
    /// Retires a code without spending an attempt, used when a newer code is issued for the same
    /// number so only the most recent one can ever be verified.
    /// </summary>
    public void Supersede(DateTime atUtc) =>
        ConsumedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
}
