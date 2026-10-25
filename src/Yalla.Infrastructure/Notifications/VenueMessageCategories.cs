namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// The push categories of a venue message (<c>message.to-guest</c>). The app registers all four; the server picks by the
/// message's kind and whether the branch lets guests type.
/// </summary>
internal static class VenueMessageCategories
{
    /// <summary>An ask, guests may type: "I'm coming", "Late 15 min" and Reply (a text input).</summary>
    public const string Ask = "venue-ask";

    /// <summary>An ask, guests may not type: "I'm coming" and "Late 15 min", no Reply.</summary>
    public const string AskButtons = "venue-ask-buttons";

    /// <summary>A text, guests may type: Reply (a text input).</summary>
    public const string Message = "venue-message";

    /// <summary>A text, guests may not type: no actions - a tap opens the thread.</summary>
    public const string MessageReadOnly = "venue-message-readonly";

    public static string For(bool ask, bool guestsCanWrite) => (ask, guestsCanWrite) switch
    {
        (true, true) => Ask,
        (true, false) => AskButtons,
        (false, true) => Message,
        (false, false) => MessageReadOnly,
    };
}
