using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Runs something once, just before the first statement whose SQL matches a predicate executes.
/// </summary>
/// <remarks>
/// <see cref="BeforeFirstInsertInto"/> for any statement: a race test picks the moment - before a
/// read, before an update, before a delete - and commits a competing write in full there, every run.
/// When the competing write is expected to <i>wait</i> for this one (a lock is the fix under test),
/// start it without awaiting it to the end, or the two wait on each other. Assert <see cref="Fired"/>,
/// or the test can pass without the race ever occurring.
/// </remarks>
internal sealed class BeforeFirstCommandMatching(Func<string, bool> matches, Func<Task> action) : DbCommandInterceptor
{
    private int _fired;

    public bool Fired => _fired == 1;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfMatchingAsync(command);

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfMatchingAsync(command);

        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfMatchingAsync(command);

        return result;
    }

    private async Task RunOnceIfMatchingAsync(DbCommand command)
    {
        if (matches(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            await action();
        }
    }
}
