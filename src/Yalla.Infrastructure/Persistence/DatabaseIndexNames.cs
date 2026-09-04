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
}
