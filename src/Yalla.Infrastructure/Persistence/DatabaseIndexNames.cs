namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Names of the indexes the application reasons about by name.
/// </summary>
/// <remarks>
/// Two of these are load-bearing: the service catches their unique violations and turns them into
/// meaningful answers rather than a 500. Keeping the names here means the configuration that
/// creates them and the handler that catches them cannot drift.
/// </remarks>
internal static class DatabaseIndexNames
{
    /// <summary>
    /// Unique, filtered on open sessions: at most one party sitting at a table. A violation means
    /// somebody else seated this table first.
    /// </summary>
    public const string OpenSessionPerTable = "UX_TableSessions_OpenPerTable";

    /// <summary>
    /// Unique on the caller's command id. A violation means the same offline command is being
    /// replayed concurrently with its original.
    /// </summary>
    public const string TableStateChangeClientCommand = "IX_TableStateChanges_ClientCommandId";

    /// <summary>
    /// Unique on the booking caller's command id. A violation means a diner's retry raced its own
    /// original, and the loser answers with the booking that won rather than taking a second table.
    /// </summary>
    public const string ReservationClientCommand = "IX_Reservations_ClientCommandId";

    /// <summary>
    /// Unique on the short code the diner quotes at the door. A violation is a genuine random
    /// collision, and the only sane response is to roll another code.
    /// </summary>
    public const string ReservationCode = "IX_Reservations_Code";
}
