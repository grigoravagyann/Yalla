using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Identity;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Tells a takeaway order's guest what happened to it - the push through the outbox and the feed entry beside it, in the
/// caller's unit of work, as every other diner notice is.
/// </summary>
/// <remarks>
/// <para>
/// Under the account's lock (<see cref="DinerAccountLock"/>), so the caller has a transaction open: a deletion that
/// comes after waits for this commit and removes the entry; an account already gone is told nothing.
/// </para>
/// <para>
/// One message per step, keyed <c>takeaway:{id}:accepted</c>, <c>:ready</c>, <c>:declined</c>: a step happens once, so
/// the key cannot collide. An auto-accepted order writes its feed entry and no push - the answer to the guest's own tap
/// already says it is accepted.
/// </para>
/// </remarks>
internal static class TakeawayNotices
{
    /// <summary>The step the guest is told about.</summary>
    public enum Step
    {
        Accepted,
        Ready,
        Declined,
    }

    public static async Task WriteAsync(
        YallaDbContext db,
        IOutbox outbox,
        TakeawayOrder order,
        Step step,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool push = true)
    {
        if (order.DinerUserId is not { } dinerUserId || !await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken))
        {
            return;
        }

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == order.BranchId)
            .Select(b => new { b.Name, VenueName = b.Venue.Name, b.TimeZoneId })
            .FirstAsync(cancellationToken);

        var zone = BranchZone.For(branch.TimeZoneId);
        TimeOnly? readyLocal = order.ReadyAtUtc is { } readyAt ? zone.LocalTimeAt(readyAt) : null;
        var noAnswer = order.DeclinedBecause == TakeawayDeclineCause.NoAnswer;

        var (type, kind, what) = step switch
        {
            Step.Accepted => (OutboxMessageTypes.TakeawayAccepted, DinerNotificationKinds.TakeawayAccepted, "accepted"),
            Step.Ready => (OutboxMessageTypes.TakeawayReady, DinerNotificationKinds.TakeawayReady, "ready"),
            _ => (OutboxMessageTypes.TakeawayDeclined, DinerNotificationKinds.TakeawayDeclined, "declined"),
        };

        var extra = new Dictionary<string, string>();

        if (step != Step.Declined && order.ReadyAtUtc is { } promised)
        {
            extra["readyTime"] = readyLocal!.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            extra["readyAtUtc"] = promised.ToString("O", CultureInfo.InvariantCulture);
        }

        if (step == Step.Declined)
        {
            extra["noAnswer"] = noAnswer ? "true" : "false";

            if (order.DeclineReason is { } reason)
            {
                extra["reason"] = reason;
            }
        }

        if (push)
        {
            outbox.Enqueue(
                type,
                new TakeawayNotice(
                    order.Id,
                    dinerUserId,
                    branch.VenueName,
                    branch.Name,
                    order.Code,
                    readyLocal,
                    order.DeclineReason,
                    noAnswer),
                nowUtc,
                OutboxMessageTypes.KeyFor("takeaway", order.Id, what));
        }

        DinerNotices.Takeaway(
            db, kind, dinerUserId, order.BranchId, order.Id, branch.VenueName, branch.Name, order.Code, nowUtc, extra);
    }
}
