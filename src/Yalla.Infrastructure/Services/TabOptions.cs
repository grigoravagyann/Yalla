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
    public string JoinUrlTemplate { get; set; } = "http://localhost:5173/join?token={token}";
}
