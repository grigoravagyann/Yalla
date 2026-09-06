namespace Yalla.Infrastructure.Services;

/// <summary>
/// Where the public web pages live, from the <c>PublicWeb</c> configuration section. No secrets.
/// </summary>
public sealed class PublicWebOptions
{
    public const string SectionName = "PublicWeb";

    /// <summary>
    /// Where a manage-booking link points. <c>{token}</c> is replaced with the booking's manage
    /// token.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="TabOptions.JoinUrlTemplate"/>, and for the same reason: the
    /// server mints the capability and only the deployment knows what host it should be reached
    /// at, so the template is configuration and the token is not.
    /// </remarks>
    public string ManageBookingUrlTemplate { get; set; } = "http://localhost:5173/booking/{token}";
}
