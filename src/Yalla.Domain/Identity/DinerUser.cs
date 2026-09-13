using Yalla.Domain.Common;
using Yalla.Domain.Media;

namespace Yalla.Domain.Identity;

/// <summary>
/// A diner's account: a phone number, and optionally a username, an email address, a password
/// and a profile photo.
/// </summary>
/// <remarks>
/// <para>
/// This row exists for one reason first: a booking needs a real phone number so the branch can
/// send the reminder, the "still coming?" nudge, and so no-shows can be recognised across
/// bookings. The one-time code flow still creates it with nothing but a number, and that path is
/// not going anywhere - a separate registration step is a step people abandon.
/// </para>
/// <para>
/// The rest is for the people who <i>want</i> an account they can recognise: a username to sign
/// in with when there is no signal for an SMS, an email, a picture the host sees at the door.
/// Registering that way does <b>not</b> verify the number - <see cref="PhoneVerifiedAtUtc"/> stays
/// null until the diner passes a code once - so everything that needs a real number, reminders
/// included, reads that stamp rather than assuming the row implies it.
/// </para>
/// <para>
/// Note what this is <b>not</b>: it is not what a walk-in gets. Someone who scans the QR on table
/// 7 and orders a coffee never reaches this table - they get a <c>TabParticipant</c> and a
/// tab-scoped token. See <c>docs/auth.md</c>.
/// </para>
/// </remarks>
public sealed class DinerUser : Entity
{
    /// <summary>The number, in E.164. Unique across the system and the natural key.</summary>
    public string PhoneE164 { get; private set; } = null!;

    /// <summary>
    /// Sign-in name, stored lowercased. Null for an account the code flow created and nobody has
    /// filled in since. Unique, case-insensitively, among the rows that have one.
    /// </summary>
    public string? Username { get; private set; }

    /// <summary>Sign-in address, stored trimmed and lowercased. Null until given; unique among those that have one.</summary>
    public string? Email { get; private set; }

    /// <summary>
    /// The password, hashed by <c>PasswordHasher&lt;T&gt;</c>. Null for an account that signs in
    /// by code only - which is every account the code flow created until its owner sets one.
    /// </summary>
    public string? PasswordHash { get; private set; }

    /// <summary>What the branch calls this person on a booking. Optional for a code-only account.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Language for reminders and codes: <c>hy</c>, <c>ru</c> or <c>en</c>.</summary>
    public string LocaleCode { get; private set; } = null!;

    public bool IsActive { get; private set; }

    /// <summary>
    /// When the number was first proved by a one-time code. Null for a registered account whose
    /// owner has never passed one - the number they typed is unverified until then.
    /// </summary>
    public DateTime? PhoneVerifiedAtUtc { get; private set; }

    /// <summary>The profile picture, or null. A <see cref="Media.Photo"/> this account owns.</summary>
    public Guid? PhotoId { get; private set; }

    public Photo? Photo { get; private set; }

    /// <summary>Last successful sign-in, by code or by password. Null until the row has been signed in to once.</summary>
    public DateTime? LastSignInAtUtc { get; private set; }

    /// <summary>Whether a password sign-in is possible at all.</summary>
    public bool HasPassword => PasswordHash is not null;

    /// <summary>Whether the number has ever been proved by a code.</summary>
    public bool IsPhoneVerified => PhoneVerifiedAtUtc is not null;

    private DinerUser()
    {
    }

    /// <summary>The code flow's account: a number and a language, nothing more.</summary>
    public DinerUser(string phoneE164, string localeCode, string? displayName = null)
        : base(Guid.CreateVersion7())
    {
        PhoneE164 = Guard.NotBlank(phoneE164, nameof(phoneE164), FieldLengths.PhoneE164);
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);
        DisplayName = Guard.OptionalText(displayName, nameof(displayName), FieldLengths.DisplayName);
        IsActive = true;
    }

    /// <summary>
    /// An account created by the sign-up form: username, email, password and a name, with the
    /// number typed rather than proved.
    /// </summary>
    /// <remarks>
    /// The username and email are normalised on the way in - trimmed, lowercased, shape-checked by
    /// <see cref="DinerAccountRules"/> - and the refusals name the wire field. The password arrives
    /// already hashed; the domain never stores a plain one and this type never sees it.
    /// </remarks>
    /// <param name="phoneE164">The number, normalised by <see cref="PhoneNumber.Normalise"/>.</param>
    /// <param name="localeCode">Language for messages.</param>
    /// <param name="displayName">Required: the venue asks for it at the door.</param>
    /// <param name="username">As typed; normalised here.</param>
    /// <param name="email">As typed; normalised here.</param>
    /// <param name="passwordHash">The hash, never the password.</param>
    public static DinerUser Register(
        string phoneE164,
        string localeCode,
        string displayName,
        string username,
        string email,
        string passwordHash)
    {
        var diner = new DinerUser(phoneE164, localeCode)
        {
            DisplayName = Guard.NotBlank(displayName, nameof(displayName), FieldLengths.DisplayName),
        };

        diner.SetUsername(username);
        diner.SetEmail(email);
        diner.SetPassword(passwordHash);

        return diner;
    }

    /// <summary>Sets or clears the optional name. The code flow's accounts have none until asked.</summary>
    public void SetDisplayName(string? displayName) =>
        DisplayName = Guard.OptionalText(displayName, nameof(displayName), FieldLengths.DisplayName);

    /// <summary>
    /// Replaces the name, and refuses to blank it.
    /// </summary>
    /// <remarks>
    /// The profile edit sends only the fields that changed, so a blank here is a person clearing
    /// the one field the venue reads out at the door - refused, where <see cref="SetDisplayName"/>
    /// would quietly store null.
    /// </remarks>
    public void Rename(string displayName) =>
        DisplayName = Guard.NotBlank(displayName, nameof(displayName), FieldLengths.DisplayName);

    /// <summary>Sets the sign-in name, normalised and shape-checked by <see cref="DinerAccountRules"/>.</summary>
    public void SetUsername(string username) =>
        Username = DinerAccountRules.NormaliseUsername(username, nameof(username));

    /// <summary>Sets the sign-in address, normalised and shape-checked by <see cref="DinerAccountRules"/>.</summary>
    public void SetEmail(string email) =>
        Email = DinerAccountRules.NormaliseEmail(email, nameof(email));

    /// <summary>
    /// Sets or replaces the password hash. The plain password is checked by
    /// <see cref="DinerAccountRules.CheckPassword"/> before it is hashed; this only keeps the result.
    /// </summary>
    public void SetPassword(string passwordHash) =>
        PasswordHash = Guard.NotBlank(passwordHash, nameof(passwordHash), FieldLengths.PasswordHash);

    /// <summary>
    /// Records that a one-time code to this number was passed. The first proof is the one kept.
    /// </summary>
    /// <remarks>
    /// Idempotent on purpose: the code flow calls it on every sign-in, and "when was this number
    /// first proved" is the question no-show tracking asks, not "when most recently".
    /// </remarks>
    public void MarkPhoneVerified(DateTime atUtc) =>
        PhoneVerifiedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));

    /// <summary>Points the profile at a photo, or clears it. The old photo is left for the sweep.</summary>
    public void SetPhoto(Guid? photoId) =>
        PhotoId = photoId is { } id ? Guard.NotEmpty(id, nameof(photoId)) : null;

    public void SetLocale(string localeCode) =>
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);

    public void SetActive(bool isActive) => IsActive = isActive;

    public void RecordSignIn(DateTime atUtc) =>
        LastSignInAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
}
