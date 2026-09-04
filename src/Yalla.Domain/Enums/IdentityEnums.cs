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
