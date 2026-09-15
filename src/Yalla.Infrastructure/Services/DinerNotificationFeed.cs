using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reading and marking the diner's notifications feed (K12).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is visible is what has appeared:</b> an entry whose <c>CreatedAtUtc</c> is not in the future.
/// A booking reminder is written with its due time and shows from then; the count and the list agree
/// because both read the same rule.
/// </para>
/// <para>
/// <b>The cursor</b> is the last entry's instant and database sequence, encoded. Newest first by
/// <c>(CreatedAtUtc, Sequence)</c> is a total order, so a page boundary between two entries written in
/// the same instant neither repeats nor skips one, and an entry deleted by the sweep between two pages
/// does not break the next one.
/// </para>
/// </remarks>
internal sealed class DinerNotificationFeed(YallaDbContext db, IClock clock, ICurrentActor actor) : IDinerNotificationFeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DinerNotificationPage> ListAsync(
        string? before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();

        if (limit is < 1 or > DinerNotificationFeedLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, $"limit is 1 to {DinerNotificationFeedLimits.MaxPageSize}.");
        }

        var cursor = string.IsNullOrWhiteSpace(before) ? null : FeedCursor.Decode(before);
        var nowUtc = clock.UtcNow;
        var page = Visible(dinerUserId, nowUtc).AsNoTracking();

        if (cursor is not null)
        {
            var atUtc = cursor.AtUtc;
            var sequence = cursor.Sequence;

            page = page.Where(n => n.CreatedAtUtc < atUtc || (n.CreatedAtUtc == atUtc && n.Sequence < sequence));
        }

        var rows = await page
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Sequence)
            .Take(limit + 1)
            .Select(n => new
            {
                n.Id,
                n.Kind,
                n.ParamsJson,
                n.BranchId,
                BranchName = db.Branches.Where(b => b.Id == n.BranchId).Select(b => b.Name).FirstOrDefault(),
                n.ReservationId,
                n.TabId,
                n.OrderId,
                n.CreatedAtUtc,
                n.Sequence,
                n.ReadAtUtc,
            })
            .ToListAsync(cancellationToken);

        var unread = await Visible(dinerUserId, nowUtc)
            .AsNoTracking()
            .CountAsync(n => n.ReadAtUtc == null, cancellationToken);

        var shown = rows.Take(limit).ToList();

        var items = shown
            .Select(r =>
            {
                var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParamsJson, Json) ?? [];

                return new DinerNotificationView(
                    r.Id,
                    r.Kind,
                    parameters,
                    r.BranchId,
                    r.BranchName ?? parameters.GetValueOrDefault("branchName"),
                    r.ReservationId,
                    r.TabId,
                    r.OrderId,
                    r.CreatedAtUtc,
                    r.ReadAtUtc is not null);
            })
            .ToList();

        var nextCursor = rows.Count > limit
            ? new FeedCursor(shown[^1].CreatedAtUtc, shown[^1].Sequence).Encode()
            : null;

        return new DinerNotificationPage(items, nextCursor, unread);
    }

    public async Task MarkReadAsync(MarkNotificationsReadCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner();

        if (command.Ids is { Count: > DinerNotificationFeedLimits.MaxIdsPerRead })
        {
            throw new FieldValidationException(
            [
                new FieldViolation(
                    "ids",
                    $"At most {DinerNotificationFeedLimits.MaxIdsPerRead} ids at once.",
                    FieldBounds.Max,
                    Max: DinerNotificationFeedLimits.MaxIdsPerRead,
                    Value: command.Ids.Count),
            ]);
        }

        var nowUtc = clock.UtcNow;
        var unread = Visible(dinerUserId, nowUtc).Where(n => n.ReadAtUtc == null);

        // Neither named: the whole feed, which is what "mark all read" sends.
        if (command.UpTo is null && command.Ids is null)
        {
            await MarkAsync(unread, nowUtc, cancellationToken);

            return;
        }

        if (command.Ids is { Count: > 0 })
        {
            var ids = command.Ids.Distinct().ToList();

            // Scoped to the caller's own feed by the query, so somebody else's id marks nothing.
            await MarkAsync(unread.Where(n => ids.Contains(n.Id)), nowUtc, cancellationToken);
        }

        if (command.UpTo is { } upTo)
        {
            var anchor = await Visible(dinerUserId, nowUtc)
                .AsNoTracking()
                .Where(n => n.Id == upTo)
                .Select(n => new { n.CreatedAtUtc, n.Sequence })
                .FirstOrDefaultAsync(cancellationToken);

            // Unknown, somebody else's, or already swept: nothing to mark up to.
            if (anchor is not null)
            {
                await MarkAsync(
                    unread.Where(n => n.CreatedAtUtc < anchor.CreatedAtUtc
                                      || (n.CreatedAtUtc == anchor.CreatedAtUtc && n.Sequence <= anchor.Sequence)),
                    nowUtc,
                    cancellationToken);
            }
        }
    }

    private IQueryable<DinerNotification> Visible(Guid dinerUserId, DateTime nowUtc) =>
        db.DinerNotifications.Where(n => n.DinerUserId == dinerUserId && n.CreatedAtUtc <= nowUtc);

    private static Task<int> MarkAsync(IQueryable<DinerNotification> entries, DateTime nowUtc, CancellationToken cancellationToken) =>
        entries.ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAtUtc, (DateTime?)nowUtc), cancellationToken);

    private Guid RequireDiner() =>
        actor.DinerUserId ?? throw new UnauthorizedAccessException("Only a signed-in diner has a notifications feed.");

    /// <summary>Where a page ended: the last entry's instant and sequence, as an opaque string.</summary>
    private sealed record FeedCursor(DateTime AtUtc, long Sequence)
    {
        public string Encode() =>
            Convert.ToBase64String(Encoding.ASCII.GetBytes(
                    string.Create(CultureInfo.InvariantCulture, $"{AtUtc.Ticks}.{Sequence}")))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        public static FeedCursor Decode(string value)
        {
            try
            {
                var base64 = value.Trim().Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

                var parts = Encoding.ASCII.GetString(Convert.FromBase64String(base64)).Split('.');

                if (parts.Length == 2
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    && ticks <= DateTime.MaxValue.Ticks
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                {
                    return new FeedCursor(new DateTime(ticks, DateTimeKind.Utc), sequence);
                }
            }
            catch (FormatException)
            {
                // Falls through to the refusal below.
            }

            throw new ArgumentException("That cursor was not issued by this feed. Start again without before.", "before");
        }
    }
}
