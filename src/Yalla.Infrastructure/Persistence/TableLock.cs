using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Yalla.Application.Reservations;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// The per-table write lock, and the only place its protocol is written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lock at all.</b> Two waiters seating the same table both update the same
/// <c>DiningTables</c> row, so <c>RowVersion</c> catches that race for free. Two diners booking the
/// same table insert <i>different</i> <c>Reservations</c> rows: nothing they touch collides,
/// optimistic concurrency sees no conflict, and both commit. The table is then double-booked and
/// nothing in the system knows it.
/// </para>
/// <para>
/// Forcing a version clash by touching the parent row would fix that and break something worse:
/// two bookings for the same table at <i>different</i> times would collide too, so the most popular
/// tables in the room would start refusing perfectly good bookings at the busiest moment.
/// </para>
/// <para>
/// So writers are serialised per table. <c>UPDLOCK</c> serialises them against each other without
/// blocking readers - the floor screen keeps refreshing throughout - and <c>HOLDLOCK</c> keeps the
/// lock to the end of the transaction instead of releasing it the moment the statement finishes,
/// which is what makes a re-check taken inside the lock mean anything.
/// </para>
/// <para>
/// <b>Seating takes this lock too, and must.</b> Booking's re-check reads <c>TableSessions</c> to
/// refuse a table somebody is already sitting at. A seating that did not take the lock could commit
/// its session in the window between that read and the booking's commit, and both would succeed -
/// the exact double-booking the occupancy check was added to prevent. Walk-ins are most of a cafe's
/// traffic, so this is the common case, not the exotic one.
/// </para>
/// <para>
/// The locked span is kept tiny on purpose: indexed reads and one insert. No availability snapshot,
/// no notification, no email happens inside it. Conflict payloads are assembled after the
/// transaction has already ended.
/// </para>
/// </remarks>
internal sealed class TableLock(
    YallaDbContext db,
    BookingLockOptions options,
    ILogger<TableLock> logger)
{
    /// <summary>SQL Server: "Lock request time out period exceeded."</summary>
    private const int LockTimeoutErrorNumber = 1222;

    /// <summary>
    /// The bounded wait, in milliseconds. Clamped rather than validated, because a misconfigured
    /// value should slow the venue down, not take its bookings offline.
    /// </summary>
    public int TimeoutMilliseconds => Math.Clamp(options.LockTimeoutMilliseconds, 100, 60_000);

    /// <summary>
    /// True when this exception is SQL Server refusing to wait any longer, at whatever depth.
    /// </summary>
    /// <remarks>
    /// Error 1222 can surface bare from an explicit statement or wrapped in a
    /// <see cref="DbUpdateException"/> from <c>SaveChanges</c>, and callers must not have to know
    /// which. Anything else is a real failure and is left alone.
    /// </remarks>
    public static bool IsTimeout(Exception exception) =>
        exception switch
        {
            SqlException { Number: LockTimeoutErrorNumber } => true,
            DbUpdateException { InnerException: SqlException { Number: LockTimeoutErrorNumber } } => true,
            _ => false,
        };

    /// <summary>
    /// Opens a transaction and takes the table's write lock, waiting no longer than
    /// <see cref="TimeoutMilliseconds"/>.
    /// </summary>
    /// <remarks>
    /// Everything the caller re-checks must be read <b>inside</b> the returned scope and never
    /// before it: a check taken before the lock proves only that the table was free at some earlier
    /// moment, which is precisely the window the lock exists to close.
    /// </remarks>
    /// <exception cref="TableLockTimeoutException">
    /// Another writer held the lock for longer than the wait allows. Nothing was changed.
    /// </exception>
    /// <param name="tableLabel">
    /// The table's label if the caller already has it. Omit it when the caller reads the table
    /// <i>inside</i> the lock, as it should: the label is only ever used to word the timeout, and
    /// looking it up on that rare path is better than a round trip on every seating.
    /// </param>
    public async Task<TableLockScope> AcquireAsync(
        Guid tableId,
        string? tableLabel,
        CancellationToken cancellationToken)
    {
        var timeoutMs = TimeoutMilliseconds;

        var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var scope = new TableLockScope(this, transaction, tableId, tableLabel ?? string.Empty, timeoutMs);

        try
        {
            // SET LOCK_TIMEOUT takes a literal and will not accept a parameter, so this is the one
            // place a value is formatted into a statement. It is an int, clamped just above, and
            // never touches user input - there is nothing here to inject.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection.
            await db.Database.ExecuteSqlRawAsync($"SET LOCK_TIMEOUT {timeoutMs};", cancellationToken);
#pragma warning restore EF1002

            await db.Database.ExecuteSqlRawAsync(
                "SELECT Id FROM DiningTables WITH (UPDLOCK, HOLDLOCK) WHERE Id = @tableId",
                [new SqlParameter("@tableId", tableId)],
                cancellationToken);

            return scope;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            // Dispose first: the label lookup below needs a connection with no doomed transaction
            // on it, and disposal is also what puts LOCK_TIMEOUT back.
            await scope.DisposeAsync();

            var label = tableLabel ?? await LabelOfAsync(tableId, cancellationToken);

            logger.LogWarning(
                "Timed out after {TimeoutMs}ms waiting for the write lock on table {TableLabel}.",
                timeoutMs, label);

            throw new TableLockTimeoutException(tableId, label, timeoutMs);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The table's label, for the timeout message, when the caller never got far enough to read it.
    /// </summary>
    /// <remarks>
    /// Best effort. A waiter reading "Table 7 is busy" can act on it; failing the whole request
    /// because the label could not be fetched would replace a retryable answer with a broken one.
    /// </remarks>
    private async Task<string> LabelOfAsync(Guid tableId, CancellationToken cancellationToken)
    {
        try
        {
            return await db.DiningTables
                       .AsNoTracking()
                       .Where(t => t.Id == tableId)
                       .Select(t => t.Label)
                       .FirstOrDefaultAsync(cancellationToken)
                   ?? "this table";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not read the label of table {TableId} for a timeout message.", tableId);
            return "this table";
        }
    }

    /// <summary>
    /// Puts the connection's lock timeout back.
    /// </summary>
    /// <remarks>
    /// <c>SET LOCK_TIMEOUT</c> is a property of the connection, and connections are pooled. The
    /// pool's reset would clear it eventually, but the same context goes on to serve the rest of
    /// this request first - and a stray five-second timeout on an unrelated query is the kind of
    /// bug that only shows up under load.
    /// </remarks>
    internal async Task ResetTimeoutAsync()
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT -1;");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ObjectDisposedException)
        {
            // The connection is already gone or the transaction is doomed. Nothing to restore, and
            // throwing here would replace the real failure with a meaningless one.
            logger.LogDebug(ex, "Could not restore the connection's lock timeout.");
        }
    }
}

/// <summary>
/// A held table lock. Commit it to keep the work; disposing without committing rolls it back.
/// </summary>
/// <remarks>
/// Disposal always restores the connection's lock timeout, on every path, which is why this is a
/// scope rather than a pair of calls the caller has to remember to balance.
/// </remarks>
internal sealed class TableLockScope(
    TableLock owner,
    IDbContextTransaction transaction,
    Guid tableId,
    string tableLabel,
    int timeoutMilliseconds) : IAsyncDisposable
{
    private bool committed;

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public int TimeoutMilliseconds { get; } = timeoutMilliseconds;

    /// <summary>Keeps the work and releases the lock.</summary>
    /// <exception cref="TableLockTimeoutException">
    /// The commit itself waited too long on a lock. Nothing was changed.
    /// </exception>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            throw new TableLockTimeoutException(TableId, TableLabel, TimeoutMilliseconds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!committed)
            {
                await transaction.RollbackAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already rolled back by the provider, or the connection went. Either way there is
            // nothing left to undo.
            _ = ex;
        }
        finally
        {
            await transaction.DisposeAsync();
            await owner.ResetTimeoutAsync();
        }
    }
}
