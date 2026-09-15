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
    /// <summary>
    /// The number, in E.164. Unique across the live accounts and their natural key. Null only on a
    /// deleted account, so the number can make a new one - see <see cref="MarkDeleted"/>.
    /// </summary>
    public string? PhoneE164 { get; private set; }

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

    /// <summary>
    /// Which generation of sessions is still good. Every access token carries the value it was
    /// minted under (the <c>sgen</c> claim), and a token whose value differs is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refresh token can be revoked in the database; an access token cannot - it is a signed
    /// statement that stays valid until it expires. Before this column a squatter evicted by the
    /// number's owner kept a working access token for up to fifteen minutes, and with it could set a
    /// password on the account the owner had just taken back. Bumping this number ends every access
    /// token the account has, at once.
    /// </para>
    /// <para>
    /// Bumped by exactly four things: the number's owner proving it and displacing a registrant
    /// (<see cref="ProveNumberByCode"/>), setting or changing the password
    /// (<see cref="SetPassword"/>), deactivation (<see cref="SetActive"/> with false) and deletion
    /// (<see cref="MarkDeleted"/>). It only ever goes up.
    /// </para>
    /// </remarks>
    public int SessionGeneration { get; private set; }

    /// <summary>
    /// When the diner deleted the account. The row stays as a tombstone - bookings and orders the
    /// venue keeps still point at an id - but everything that identified the person is gone.
    /// </summary>
    public DateTime? DeletedAtUtc { get; private set; }

    /// <summary>Whether the account has been deleted.</summary>
    public bool IsDeleted => DeletedAtUtc is not null;

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

        // Assigned rather than set through SetPassword: a brand-new account has no sessions to end,
        // and its first token is minted under generation zero.
        diner.PasswordHash = Guard.NotBlank(passwordHash, nameof(passwordHash), FieldLengths.PasswordHash);

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
    /// Sets or replaces the password hash, and ends every access token the account holds. The plain
    /// password is checked by <see cref="DinerAccountRules.CheckPassword"/> before it is hashed; this
    /// only keeps the result.
    /// </summary>
    /// <remarks>
    /// The token the change was made with ends too. The service revokes every other sign-in's refresh
    /// tokens alongside and keeps the caller's, so the app that made the change refreshes and carries
    /// on; see <c>docs/auth.md</c>.
    /// </remarks>
    public void SetPassword(string passwordHash)
    {
        PasswordHash = Guard.NotBlank(passwordHash, nameof(passwordHash), FieldLengths.PasswordHash);
        SessionGeneration++;
    }

    /// <summary>
    /// Replaces the stored hash of the <b>same</b> password with a stronger one, at sign-in. Not a
    /// password change, so no session ends.
    /// </summary>
    public void RehashPassword(string passwordHash) =>
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

    /// <summary>
    /// A one-time code to this number came back: whoever is holding the phone owns this account.
    /// Marks the number verified and, when that proof is the first, the account has a password and
    /// the verifier is not already signed in as this account, clears the password. Returns whether it
    /// did, so the caller revokes every session too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration takes the number as typed, and anybody can type anybody's number. Until a code
    /// comes back the password on the row belongs to whoever filled in the form - not necessarily
    /// the person the number belongs to. The first code to come back settles it: the verifier proves
    /// the number, and if the verifier is somebody else the registrant's way in goes. Leaving the
    /// password would hand the number's real owner an account a stranger can still sign in to,
    /// reading their bookings and changing their profile.
    /// </para>
    /// <para>
    /// <paramref name="verifierIsAccountHolder"/> is the honest registrant's case, and the normal
    /// sign-up: register, then verify from the same app while holding the token register issued. The
    /// person with the password and the person with the phone are then shown to be one person - the
    /// token proves the first, the code the second - so nothing is cleared and no session is revoked.
    /// It is safe because each half is out of the other party's reach: a squatter holding the token
    /// never receives the code, and the number's owner, who receives it, never holds the squatter's
    /// token. Anonymous or under a different account, it is false, and the password goes.
    /// </para>
    /// <para>
    /// An account whose number was already proved keeps its password either way - the code proves
    /// the same person again, and a password set after that proof is theirs. The username and email
    /// stay too: they are not a way in without the password, and the verifier can change both.
    /// </para>
    /// </remarks>
    /// <param name="atUtc">When the code came back.</param>
    /// <param name="verifierIsAccountHolder">
    /// True only when the verify request carried a valid diner token for this very account. Defaults
    /// to false, the displacing answer, so a caller that does not know cannot keep a squatter in.
    /// </param>
    /// <returns>True when a password was cleared and the account's sessions must be revoked.</returns>
    public bool ProveNumberByCode(DateTime atUtc, bool verifierIsAccountHolder = false)
    {
        var verifiedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        var displacesRegistrant = PhoneVerifiedAtUtc is null && PasswordHash is not null && !verifierIsAccountHolder;

        if (displacesRegistrant)
        {
            // The registrant's access tokens end with the password, not fifteen minutes later.
            PasswordHash = null;
            SessionGeneration++;
        }

        MarkPhoneVerified(verifiedAtUtc);

        return displacesRegistrant;
    }

    /// <summary>
    /// Points the profile at a photo, or clears it. The caller deletes a picture that is being
    /// removed; one that is replaced is left for the orphan sweep.
    /// </summary>
    public void SetPhoto(Guid? photoId) =>
        PhotoId = photoId is { } id ? Guard.NotEmpty(id, nameof(photoId)) : null;

    public void SetLocale(string localeCode) =>
        LocaleCode = Guard.NotBlank(localeCode, nameof(localeCode), FieldLengths.LocaleCode);

    /// <summary>Switches the account on or off. Switching it off ends every access token it holds.</summary>
    public void SetActive(bool isActive)
    {
        if (IsActive && !isActive)
        {
            SessionGeneration++;
        }

        IsActive = isActive;
    }

    /// <summary>
    /// Deletes the account: a tombstone with nothing left that identifies the person, and every
    /// session ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row stays because the venue's records point at it until the caller detaches them, and
    /// because an id that was deleted must never be handed to somebody new. What goes is everything
    /// a person could be recognised by: the number, the username, the email, the name, the password
    /// and the stamp that said the number was real. Clearing the three sign-in keys is also what lets
    /// the same number, username and email make a new account afterwards - the unique indexes only
    /// count rows that have one.
    /// </para>
    /// <para>
    /// This is the domain half. Reviews, the picture, devices, tokens, tab and booking links are
    /// other tables, and the account-deletion service removes or detaches them in the same
    /// transaction. Deleting twice is a no-op.
    /// </para>
    /// </remarks>
    /// <param name="atUtc">When the deletion was asked for.</param>
    public void MarkDeleted(DateTime atUtc)
    {
        if (IsDeleted)
        {
            return;
        }

        DeletedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        PhoneE164 = null;
        Username = null;
        Email = null;
        DisplayName = null;
        PasswordHash = null;
        PhoneVerifiedAtUtc = null;
        PhotoId = null;
        Photo = null;

        IsActive = false;
        SessionGeneration++;
    }

    public void RecordSignIn(DateTime atUtc) =>
        LastSignInAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
}
