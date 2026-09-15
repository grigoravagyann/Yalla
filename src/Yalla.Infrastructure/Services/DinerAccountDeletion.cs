using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Media;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What deleting a diner account does to every table, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of row, two treatments.</b> Rows that are the <i>person's</i> - their reviews, their
/// phones, their pictures - are deleted. Rows that are the <i>venue's</i> record of something that
/// happened - a booking, a place at a table, an order - are kept, with the link to the person cut:
/// the venue still needs to know table 7 was booked for 19:00 and what was served, and it does not
/// need to know it was this account. The booking keeps the guest name and number the diner typed
/// into it, because that is what the venue was given for that booking.
/// </para>
/// <para>
/// <b>Adding a table.</b> A new table with a <c>DinerUserId</c> gets one line in
/// <see cref="RemoveRowsOwnedByAsync"/> (the person's) or in <see cref="DetachVenueRecordsAsync"/>
/// (the venue's). Nothing else changes: the counts go into the audit row by name. A table added
/// without a line here keeps a deleted person's data for ever, and a foreign key to
/// <c>DinerUsers</c> will not catch it, because the account row is a tombstone rather than gone.
/// </para>
/// </remarks>
internal static class DinerAccountDeletion
{
    /// <summary>The audit action written for every deletion.</summary>
    public const string AuditAction = "diner.delete";

    /// <summary>What a tab place is called once its person is gone - what an anonymous scan is called.</summary>
    public const string GuestName = "Guest";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deletes the account, in one transaction. The caller has already proved the request is the
    /// account holder's.
    /// </summary>
    /// <remarks>
    /// Picture files are deleted inside the transaction, before the commit, for the sweep's reason:
    /// if the commit then fails, the account survives with a picture whose links answer 404, rather
    /// than a committed deletion leaving files nothing will ever find again.
    /// </remarks>
    public static async Task EraseAsync(
        YallaDbContext db,
        IPhotoService photos,
        RefreshTokenStore refreshTokens,
        DinerUser diner,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var dinerUserId = diner.Id;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The account row's lock, first and held to the commit. Every write that keeps something of this
        // person's takes the same lock (DinerAccountLock) - a favourite, a review, a report, a profile
        // edit, a picture, a booking and its reminder, an order-ready entry: one that got it first
        // commits before anything below reads what to remove, and one that comes after finds the
        // tombstone - so no row of theirs can be written behind the deletes and outlive the account.
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        // The row as it is now, not as the caller loaded it before the lock. A profile edit that
        // committed in between is on the row, and the tombstone's save sends only the columns it sees
        // change: a username written after this entity was read would otherwise stay on it.
        await db.Entry(diner).ReloadAsync(cancellationToken);

        // Read before the tombstone clears it: the one-time codes sent to the number are keyed by it.
        var phoneE164 = diner.PhoneE164;

        // Every picture the person owns, not only the current one: a replaced picture waiting for
        // the sweep is still a picture of them. Under the lock, so one uploaded a moment ago counts.
        var photoIds = await db.Photos
            .Where(p => p.DinerUserId == dinerUserId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        // The tombstone first: it clears DinerUsers.PhotoId, which the photo deletes below need,
        // and bumps the session generation that ends every access token.
        diner.MarkDeleted(nowUtc);
        await refreshTokens.RevokeAllForSubjectAsync(
            RefreshTokenSubject.Diner, dinerUserId, "account-deleted", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var removed = await RemoveRowsOwnedByAsync(db, dinerUserId, phoneE164, cancellationToken);
        var detached = await DetachVenueRecordsAsync(db, dinerUserId, cancellationToken);

        foreach (var photoId in photoIds)
        {
            await photos.DeleteAsync(photoId, cancellationToken);
        }

        // Written by the diner, about the diner, and carrying nothing that identifies them: the id
        // is already meaningless once the row is a tombstone. The actor column holds the diner's id
        // because there is no staff member behind this action; ChangesJson says so.
        db.PlatformAuditLogs.Add(new PlatformAuditLog(
            dinerUserId,
            AuditAction,
            nameof(DinerUser),
            dinerUserId,
            JsonSerializer.Serialize(
                new
                {
                    actorType = "diner",
                    removed,
                    photos = photoIds.Count,
                    detached,
                },
                Json),
            nowUtc));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The person's own rows, deleted. One line per table, counted by name.</summary>
    /// <remarks>
    /// <para>Rows that point at another row in this list go above it.</para>
    /// </remarks>
    private static async Task<Dictionary<string, int>> RemoveRowsOwnedByAsync(
        YallaDbContext db,
        Guid dinerUserId,
        string? phoneE164,
        CancellationToken cancellationToken) =>
        new()
        {
            ["devices"] = await db.DinerDevices
                .Where(d => d.DinerUserId == dinerUserId)
                .ExecuteDeleteAsync(cancellationToken),

            // The one-time codes sent to the account's number - the one that proved this deletion
            // included. Keyed by the number, not the account, so the tombstone clearing PhoneE164 does
            // not reach them, and nothing else ever deletes them.
            ["phoneCodes"] = phoneE164 is null
                ? 0
                : await db.PhoneVerificationCodes
                    .Where(c => c.PhoneE164 == phoneE164)
                    .ExecuteDeleteAsync(cancellationToken),

            // K11: the places the person hearted.
            ["favorites"] = await db.DinerFavorites
                .Where(f => f.DinerUserId == dinerUserId)
                .ExecuteDeleteAsync(cancellationToken),

            // K12: the feed - what happened to their bookings, orders and reviews, including a reminder
            // that has not appeared yet.
            ["notifications"] = await db.DinerNotifications
                .Where(n => n.DinerUserId == dinerUserId)
                .ExecuteDeleteAsync(cancellationToken),

            // Above the reviews (K8): the reports this diner filed, and every report about one of this
            // diner's reviews - those reviews go on the next line, and a report is about its review.
            ["reviewReports"] = await db.BranchReviewReports
                .Where(r => r.DinerUserId == dinerUserId
                            || db.BranchReviews.Any(v => v.Id == r.ReviewId && v.DinerUserId == dinerUserId))
                .ExecuteDeleteAsync(cancellationToken),

            // The aggregates are computed from this table on read, so rating and count move with it.
            ["reviews"] = await db.BranchReviews
                .Where(r => r.DinerUserId == dinerUserId)
                .ExecuteDeleteAsync(cancellationToken),
        };

    /// <summary>The venue's records of what the person did, kept with the link to them cut.</summary>
    private static async Task<Dictionary<string, int>> DetachVenueRecordsAsync(
        YallaDbContext db,
        Guid dinerUserId,
        CancellationToken cancellationToken) =>
        new()
        {
            // A place at a table: its orders and shares point at it, so it stays - as a guest, which
            // is exactly what somebody who scanned the QR with no account looks like.
            ["tabParticipants"] = await db.TabParticipants
                .Where(p => p.UserId == dinerUserId)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(p => p.UserId, (Guid?)null)
                        .SetProperty(p => p.DisplayName, GuestName),
                    cancellationToken),

            // The booking is the venue's; the guest name and number on it are what the venue was
            // given for it and stay. Orders hang off tabs and participants and need nothing.
            ["reservations"] = await db.Reservations
                .Where(r => r.DinerUserId == dinerUserId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.DinerUserId, (Guid?)null), cancellationToken),
        };
}
