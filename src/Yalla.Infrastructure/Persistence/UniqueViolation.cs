using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Recognises which unique index a failed write violated.
/// </summary>
/// <remarks>
/// Matching on the index name is unlovely but it is the only thing SQL Server actually tells us,
/// and the alternative - a check-then-insert in the service - loses the very race these indexes
/// exist to win.
/// </remarks>
internal static class UniqueViolation
{
    /// <summary>Cannot insert duplicate key row in object ... with unique index.</summary>
    private const int DuplicateKeyInIndex = 2601;

    /// <summary>Violation of UNIQUE KEY constraint.</summary>
    private const int DuplicateKeyConstraint = 2627;

    public static bool IsOn(DbUpdateException exception, string indexName) =>
        exception.InnerException is SqlException sql
        && sql.Number is DuplicateKeyInIndex or DuplicateKeyConstraint
        && sql.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is any unique violation, whichever index it came from.</summary>
    public static bool IsAny(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && sql.Number is DuplicateKeyInIndex or DuplicateKeyConstraint;
}
