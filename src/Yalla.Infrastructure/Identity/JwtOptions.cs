namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Token signing and lifetimes, from the <c>Jwt</c> configuration section.
/// </summary>
/// <remarks>
/// <see cref="SigningKey"/> is a secret and is never in <c>appsettings.json</c>. In development it
/// comes from user secrets (<c>dotnet user-secrets set "Jwt:SigningKey" "..."</c>); everywhere
/// else it arrives as the <c>Jwt__SigningKey</c> environment variable. Startup fails loudly if it
/// is missing or too short, rather than quietly signing with something guessable.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// The HMAC key. At least 32 bytes - HS256 with a shorter key is weaker than it looks and the
    /// library will not tell you.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "yalla";

    public string Audience { get; set; } = "yalla-clients";

    /// <summary>Access token lifetime, in minutes. Short by design: refresh tokens carry the session.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Device token lifetime, in days. Long on purpose - a tablet enrolled in March must still
    /// work in November without anyone typing anything.
    /// </summary>
    public int DeviceTokenDays { get; set; } = 365;

    /// <summary>
    /// Staff session token lifetime, in minutes. Matches the inactivity window, so a tablet left
    /// alone stops working at the same moment its session becomes unrenewable.
    /// </summary>
    public int StaffSessionMinutes { get; set; } = 30;

    /// <summary>
    /// Hard cap on a tab participant token, in hours. The real expiry is the tab closing; this
    /// only stops a token from a tab nobody ever closed living forever.
    /// </summary>
    public int ParticipantTokenMaxHours { get; set; } = 12;

    /// <summary>
    /// How long after a tab closes its participants can still use their token, in minutes. Long
    /// enough to look at the receipt, short enough not to be a session.
    /// </summary>
    public int ParticipantReceiptGraceMinutes { get; set; } = 120;

    /// <summary>
    /// Tolerance for clock differences between this server and whatever issued the token, in
    /// seconds. The framework default is five minutes, which is far too generous for a
    /// fifteen-minute token.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
