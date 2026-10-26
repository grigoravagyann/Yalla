namespace Yalla.Domain.Enums;

/// <summary>
/// What kind of caller a token represents.
/// </summary>
/// <remarks>
/// <para>
/// There are four identity types in this product and they are genuinely different, so the type is
/// carried explicitly in the token rather than inferred from which claims happen to be present.
/// An authorisation handler that has to guess "this looks like a staff token because it has a
/// role claim" is one renamed claim away from letting the wrong caller through.
/// </para>
/// <para>
/// Stored as an int, like every other enum in this system.
/// </para>
/// </remarks>
public enum PrincipalType
{
    /// <summary>
    /// Someone who scanned a table QR code or redeemed a join token. No account, no user row -
    /// only a <c>TabParticipant</c>. Valid for exactly one tab.
    /// </summary>
    TabParticipant = 1,

    /// <summary>A diner who verified a phone number. Has a <c>DinerUser</c>, never a password.</summary>
    Diner = 2,

    /// <summary>
    /// An enrolled staff tablet. Carries a branch but no person: it is the thing the PIN is typed
    /// into, not the identity that acts.
    /// </summary>
    StaffDevice = 3,

    /// <summary>A named staff member who tapped their PIN on an enrolled device.</summary>
    StaffSession = 4,

    /// <summary>An owner or manager signed in to the admin panel with email and password.</summary>
    VenueUser = 5,
}

/// <summary>Which kind of principal a rotating refresh token belongs to.</summary>
/// <remarks>
/// Staff sessions are deliberately absent: they expire on inactivity rather than rotating for
/// thirty days, so they have their own <c>StaffSession</c> record. See <c>docs/auth.md</c>.
/// </remarks>
public enum RefreshTokenSubject
{
    /// <summary>A <c>DinerUser</c>.</summary>
    Diner = 1,

    /// <summary>A <c>StaffMember</c> holding email-and-password credentials.</summary>
    VenueUser = 2,
}

/// <summary>
/// Where one Telegram phone-verification challenge is in its life. Stored as the integer; never
/// renumber.
/// </summary>
public enum TelegramVerificationStatus
{
    /// <summary>Issued; waiting for the bot to be handed the person's contact.</summary>
    Pending = 1,

    /// <summary>The bound Telegram user shared their own number. Claimable by the app.</summary>
    Verified = 2,

    /// <summary>Claimed: tokens were issued against it. Finished.</summary>
    Consumed = 3,

    /// <summary>Found past its deadline before it was consumed. Finished.</summary>
    Expired = 4,
}

/// <summary>
/// What an enrolled <c>StaffDevice</c> is. Stored as the integer; never renumber.
/// </summary>
/// <remarks>
/// A tablet is the branch's: anybody who works there taps their PIN on it. A phone is one person's: it
/// is paired to them by a code their manager shows, and only their own PIN opens it. Both are the same
/// row, the same device token and the same revocation, because a phone is a personal tablet and not a
/// second sign-in system. See <c>docs/auth.md</c>, "Personal phones".
/// </remarks>
public enum StaffDeviceKind
{
    /// <summary>A shared floor device, enrolled to a branch with a one-time code. No bound person.</summary>
    Tablet = 1,

    /// <summary>One staff member's own phone, paired to them and unlocked only with their PIN.</summary>
    Phone = 2,
}

/// <summary>
/// What a paired phone said it runs on, read by its own browser from its user agent. Stored as the integer on
/// <c>StaffDevice</c>; never renumber. The console words it in the reader's language - "iPhone · Safari" - rather than
/// the server or the phone writing an English label.
/// </summary>
public enum StaffDevicePlatform
{
    /// <summary>None the phone recognised: the console says "Phone" in the reader's language.</summary>
    Other = 1,

    IPhone = 2,

    /// <summary>An iPad, including one whose Safari asks for the desktop site and calls itself a Mac.</summary>
    IPad = 3,

    Android = 4,

    Windows = 5,

    Mac = 6,

    Linux = 7,
}

/// <summary>
/// What browser a paired phone said it is, read by the browser from its own user agent. Stored as the integer on
/// <c>StaffDevice</c>; never renumber. Absent when the phone did not recognise its browser.
/// </summary>
public enum StaffDeviceBrowser
{
    Safari = 1,

    Chrome = 2,

    Edge = 3,

    Firefox = 4,

    Opera = 5,

    SamsungInternet = 6,
}

/// <summary>
/// The kinds of staff push a device can ask for, as flags. Stored as the integer on <c>StaffDevice</c>; never
/// renumber a bit.
/// </summary>
/// <remarks>
/// Flags rather than five columns, because a device's preference is one choice the person makes on one
/// screen, and because a new kind is a new bit rather than a new column on a busy table. A device that has
/// never chosen stores nothing and reads <see cref="Defaults"/>.
/// </remarks>
[Flags]
public enum StaffPushKinds
{
    /// <summary>Nothing at all - every switch off.</summary>
    None = 0,

    /// <summary>A booking now waits for the floor's yes or no.</summary>
    Approvals = 1,

    /// <summary>A guest wrote in a booking's or takeaway order's thread, or answered "are you coming?".</summary>
    GuestMessages = 2,

    /// <summary>A table called a waiter or asked for the bill.</summary>
    TableCalls = 4,

    /// <summary>A new takeaway order waits to be accepted.</summary>
    Takeaway = 8,

    /// <summary>The kitchen marked an order ready for the person's table.</summary>
    KitchenReady = 16,

    /// <summary>What a device that never chose gets: everything but the kitchen.</summary>
    Defaults = Approvals | GuestMessages | TableCalls | Takeaway,

    /// <summary>Every kind there is.</summary>
    All = Defaults | KitchenReady,
}
