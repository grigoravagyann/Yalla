using Microsoft.EntityFrameworkCore;
using Yalla.Application.Auth;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Serialises a diner's own writes with the deletion of their account, and with each other.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline's session check is cached for five seconds and evicted only on the process that
/// changed the account, so a request from another phone can be past it when the account is deleted.
/// Deletion removes the person's rows with set-based deletes, which lock only rows that already exist:
/// a favourite or a report inserted a moment later would be kept on the tombstone for ever, and no
/// foreign key catches it, because the account row stays.
/// </para>
/// <para>
/// So a write that keeps a row keyed to the person takes an update lock on the account's row inside
/// its transaction and reads the row there, and deletion takes the same lock first. Whichever comes
/// second waits for the other's commit: a writer that was first has committed before deletion reads
/// what to remove, and a writer that was second finds the tombstone and is refused. Two writers of one
/// account queue on it as well, which is what turns a count-then-insert limit into a real limit.
/// </para>
/// </remarks>
internal static class DinerAccountLock
{
    /// <summary>
    /// Takes the update lock on the account row, held to the end of the open transaction, and refuses
    /// an account that is deleted, deactivated or not there.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open, so there is nothing to hold the lock.</exception>
    /// <exception cref="AuthenticationFailedException"><c>session-revoked</c>: the account is not live.</exception>
    public static async Task RequireLiveAsync(YallaDbContext db, Guid dinerUserId, CancellationToken cancellationToken)
    {
        if (!await IsLiveAsync(db, dinerUserId, cancellationToken))
        {
            throw new AuthenticationFailedException(
                TokenRevoked.SessionRevoked, "This session has ended. Sign in again.");
        }
    }

    /// <summary>
    /// Takes the same lock, held to the end of the open transaction, and answers whether the account is
    /// live - for a write that is not the account's own request and so has nobody to refuse: a booking's
    /// reminder written after the booking committed, or a kitchen marking an order ready. It leaves the
    /// person's row out instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open, so there is nothing to hold the lock.</exception>
    public static async Task<bool> IsLiveAsync(YallaDbContext db, Guid dinerUserId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The diner account lock is held by a transaction. Begin one before taking it.");
        }

        var live = await db.Database
            .SqlQuery<bool>(
                $"""
                 SELECT CAST(CASE WHEN IsActive = 1 AND DeletedAtUtc IS NULL THEN 1 ELSE 0 END AS bit) AS [Value]
                 FROM DinerUsers WITH (UPDLOCK, HOLDLOCK)
                 WHERE Id = {dinerUserId}
                 """)
            .ToListAsync(cancellationToken);

        return live is [true];
    }
}
