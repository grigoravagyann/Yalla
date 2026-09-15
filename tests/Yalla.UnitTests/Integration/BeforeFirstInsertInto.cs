using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Runs something once, just before the first statement that inserts into one table executes.
/// </summary>
/// <remarks>
/// How a race test makes the race happen every run rather than on the unlucky evening: the action
/// commits a competing write in full between the service's read and its insert. Assert
/// <see cref="Fired"/>, or the test can pass without the race ever occurring.
/// </remarks>
internal sealed class BeforeFirstInsertInto(string table, Func<Task> action) : DbCommandInterceptor
{
    private int _fired;

    public bool Fired => _fired == 1;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfInsertAsync(command);

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfInsertAsync(command);

        return result;
    }

    private async Task RunOnceIfInsertAsync(DbCommand command)
    {
        var inserts = command.CommandText.Contains($"INSERT INTO [{table}]", StringComparison.OrdinalIgnoreCase)
                      || command.CommandText.Contains($"MERGE [{table}]", StringComparison.OrdinalIgnoreCase);

        if (inserts && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            await action();
        }
    }
}
