using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A held update lock on one branch row, inside a transaction: the writers that replace a branch's
/// configuration whole take it, so two saves never interleave. Commit to keep the work; disposing without
/// committing rolls it back.
/// </summary>
/// <remarks>
/// <para>
/// <c>UPDLOCK</c> serialises those writers against each other without blocking readers, and
/// <c>HOLDLOCK</c> keeps it to the end of the transaction, so what each reads inside the lock is
/// still true when it commits. The same protocol as <see cref="TableLock"/>, one level up.
/// </para>
/// <para>
/// Taken by the floor plan's writers (the plan replace, the room add, rename and delete, the table delete),
/// the special opening days' replace and the menu's reorders.
/// </para>
/// </remarks>
internal sealed class BranchWriteLock : IAsyncDisposable
{
    /// <summary>How long a save waits for another one on the same branch.</summary>
    /// <remarks>
    /// A plan replace is a handful of statements; five seconds is many of them. Longer than that and
    /// somebody's save is stuck, which the editor should hear about as "try again" rather than hang.
    /// </remarks>
    private const string SetLockTimeout = "SET LOCK_TIMEOUT 5000;";

    private readonly YallaDbContext db;
    private readonly IDbContextTransaction transaction;
    private bool committed;

    private BranchWriteLock(YallaDbContext db, IDbContextTransaction transaction)
    {
        this.db = db;
        this.transaction = transaction;
    }

    /// <summary>Opens a transaction and takes the update lock on the branch row.</summary>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Another save held the lock for longer than the wait allows. Answers 409
    /// <c>concurrent-update</c>: reload and try again. Nothing was changed.
    /// </exception>
    public static async Task<BranchWriteLock> AcquireAsync(
        YallaDbContext db,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new BranchWriteLock(db, transaction);

        try
        {
            await db.Database.ExecuteSqlRawAsync(SetLockTimeout, cancellationToken);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT Id FROM Branches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @branchId",
                [new SqlParameter("@branchId", branchId)],
                cancellationToken);

            return scope;
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            await scope.DisposeAsync();
            throw Busy(branchId);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>Saves and commits inside the lock, answering a lock wait as a concurrent update.</summary>
    public async Task SaveAsync(Guid branchId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            // A table row held by a booking or a seating for longer than the wait. Nothing was
            // decided, so the honest answer is the retryable one.
            db.ChangeTracker.Clear();
            throw Busy(branchId);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        committed = true;
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
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SqlException)
        {
            // Already rolled back by the provider, or the connection went. Nothing left to undo.
            _ = ex;
        }
        finally
        {
            await transaction.DisposeAsync();

            // LOCK_TIMEOUT belongs to the pooled connection; put it back for the rest of the request.
            try
            {
                await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT -1;");
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ObjectDisposedException)
            {
                _ = ex;
            }
        }
    }

    private static DbUpdateConcurrencyException Busy(Guid branchId) =>
        new($"Branch {branchId} is being saved by somebody else right now. Reload and try again.");
}
