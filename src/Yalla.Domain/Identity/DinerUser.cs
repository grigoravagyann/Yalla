using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// A diner who has verified a phone number. There is deliberately no password on this row.
/// </summary>
/// <remarks>
/// <para>
/// This account exists for one reason: a booking needs a real phone number so the branch can send
/// the reminder, the "still coming?" nudge, and so no-shows can be recognised across bookings. A
/// password would add nothing to that and would be one more thing to forget at the moment someone
/// is trying to book a table.
/// </para>
/// <para>
/// Note what this is <b>not</b>: it is not what a walk-in gets. Someone who scans the QR on table
/// 7 and orders a coffee never reaches this table - they get a <c>TabParticipant</c> and a
/// tab-scoped token. See <c>docs/auth.md</c>.
/// </para>
/// </remarks>
public sealed class DinerUser : Entity
{
    /// <summary>The verified number, in E.164. Unique across the system and the natural key.</summary>
    public string PhoneE164 { get; private set; } = null!;

    /// <summary>What the branch calls this person on a booking. Optional - the number is enough.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Language for reminders and codes: <c>hy</c>, <c>ru</c> or <c>en</c>.</summary>
    public string LocaleCode { get; private set; } = null!;

    public bool IsActive { get; private set; }

    /// <summary>Last successful code verification. Null until the row has been signed in to once.</summary>
    public DateTime? LastSignInAtUtc { get; private set; }

    private DinerUser()
    {
    }

    public DinerUser(string phoneE164, string localeCode, string? displayName = null)
        : base(Guid.CreateVersion7())
    {
        PhoneE164 = Guard.NotBlank(phoneE164, nameof(phoneE164), FieldLengths.PhoneE164);
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);
        DisplayName = Guard.OptionalText(displayName, nameof(displayName), FieldLengths.DisplayName);
        IsActive = true;
    }

    public void SetDisplayName(string? displayName) =>
        DisplayName = Guard.OptionalText(displayName, nameof(displayName), FieldLengths.DisplayName);

    public void SetLocale(string localeCode) =>
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);

    public void SetActive(bool isActive) => IsActive = isActive;

    public void RecordSignIn(DateTime atUtc) =>
        LastSignInAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
}
