namespace Yalla.Infrastructure.Services;

/// <summary>
/// Tab settings, from the <c>Tabs</c> configuration section. Contains no secrets.
/// </summary>
public sealed class TabOptions
{
    public const string SectionName = "Tabs";

    /// <summary>
    /// Where a join link points. <c>{token}</c> is replaced with the invitation token. The same
    /// token backs the QR on the host's screen; this is only the second way of handing it over.
    /// </summary>
    /// <remarks>
    /// <b>A path on the diner link domain</b>, which is what the app's <c>/join/[token]</c> route and
    /// its Android intent filter match. It was a query string on the web console's local address:
    /// the app's link parser dropped the query and read the invitation as the word "join", and no
    /// phone would have opened it in the app. There is no join page on the web.
    /// </remarks>
    public string JoinUrlTemplate { get; set; } = "https://yalla.am/join/{token}";
}
