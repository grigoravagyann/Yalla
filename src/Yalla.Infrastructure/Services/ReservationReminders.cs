using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A booking's reminder, late nudge and "are you on your way?" - written beside the booking, in the same
/// unit of work, and replaced when the booking moves.
/// </summary>
internal sealed class ReservationReminders(
    YallaDbContext db,
    IClock clock,
    IOutbox outbox,
    IOptions<PublicWebOptions> publicWeb)
{
    // ---------------------------------------------------------------- what the diner gets told

    /// <summary>
    /// Queues the reminder and the late nudge alongside the booking that causes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are written in the caller's transaction and saved with it - <c>IOutbox</c> has no save of
    /// its own, precisely so this cannot drift into "book, commit, then enqueue" and leave a window
    /// where the booking exists and the reminder does not.
    /// </para>
    /// <para>
    /// The payload carries the venue and branch names, the table and the time as they are now. At
    /// send time the world has moved - the venue may have been renamed - and what the message should
    /// say is what was true when the booking was made.
    /// </para>
    /// <para>
    /// A reminder whose moment has already passed is not written at all. A booking made an hour
    /// before it starts has no three-hours-before, and queueing one in the past would only be
    /// discarded by the staleness rule with a log line implying something went wrong.
    /// </para>
    /// </remarks>
    internal async Task ScheduleRemindersAsync(
        Reservation reservation,
        Branch branch,
        string tableLabel,
        string manageToken,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        // The manage link, for a booking whose diner has no app to be pushed to.
        //
        // Written now, before any channel exists that could send it. The app reminder already
        // carries a one-tap cancel; a web booking's reminder has to carry a URL instead, and
        // the token is knowable only here - it is never stored in plaintext and never returned
        // again. A dispatcher added later that had to go and mint one would find it cannot.
        //
        // Only for Web. A capability that opens somebody's booking should be written into as
        // few places as possible, and an app booking has a better cancel route already.
        var manageUrl = reservation.Channel == ReservationChannel.Web
            ? publicWeb.Value.ManageBookingUrlTemplate.Replace(
                "{token}", Uri.EscapeDataString(manageToken), StringComparison.Ordinal)
            : null;

        // The booking committed a moment ago, and the account can have been deleted since - from another
        // phone, past the token cache - which cut the booking loose and emptied the feed. The feed entry
        // is the person's, so it is written under the account's lock and only for a live account:
        // a deletion that comes after waits for this commit and removes it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The reminder's moment may be gone already (a booking made an hour ahead) while the question's is not, so the
        // account is checked whenever there is a diner; the reminder itself is only written while it is still ahead.
        var writeFeed = reservation.DinerUserId is { } dinerUserId
                        && await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken);

        // A new booking has no pre-order yet: asked at 30 minutes, and saving one moves the question to 45.
        EnqueueReminders(
            db, outbox, reservation, branch, tableLabel, manageUrl, keyVersion: null, writeFeed, NothingDelivered, nowUtc,
            OnYourWayAsk.Plain);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces a booking's unsent reminder, late nudge and waiting feed entry with ones for the slot
    /// it now has, in the caller's unit of work - after a move, and after a declined change sent it
    /// back to the slot it had.
    /// </summary>
    /// <remarks>
    /// The new keys carry the start they are for (<c>reminder:202610171930</c>), because a message
    /// already sent keeps its row and its key; one whose key was sent already is not written again. A
    /// web booking's new messages carry the manage link its old ones did - the token's plaintext exists
    /// only in the create response and the messages written then. The caller is responsible for the
    /// diner's account being live (its lock, or a booking whose account is gone has no diner and so no
    /// feed to write).
    /// </remarks>
    internal static async Task ReplaceRemindersAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        string tableLabel,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservation.Id);

        var messages = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix))
            .Select(m => new { m.Type, m.IdempotencyKey, m.PayloadJson, Sent = m.SentAtUtc != null })
            .ToListAsync(cancellationToken);

        var manageUrl = reservation.Channel == ReservationChannel.Web
            ? messages
                .Where(m => m.Type is OutboxMessageTypes.ReservationReminder or OutboxMessageTypes.ReservationLateNudge)
                .Select(m => ManageUrlIn(m.PayloadJson))
                .FirstOrDefault(url => url is not null)
            : null;

        var delivered = messages.Where(m => m.Sent).Select(m => m.IdempotencyKey).ToHashSet(StringComparer.Ordinal);

        // Every booking is asked "are you on your way?" and the question moves with it: at 45 minutes while a pre-order
        // waits for the kitchen, at 30 otherwise. Not once the kitchen has the pre-order or the waiter marked the party
        // not coming - the floor has decided.
        var preOrder = await db.PreOrders
            .Where(p => p.ReservationId == reservation.Id)
            .Select(p => new { Decided = p.SentAtUtc != null || p.NotComingAtUtc != null })
            .FirstOrDefaultAsync(cancellationToken);

        OnYourWayAsk askOnYourWay = preOrder is null
            ? OnYourWayAsk.Plain
            : preOrder.Decided ? OnYourWayAsk.None : OnYourWayAsk.WithPreOrder;

        await outbox.CancelAsync(prefix, cancellationToken);
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        EnqueueReminders(
            db,
            outbox,
            reservation,
            branch,
            tableLabel,
            manageUrl,
            keyVersion: reservation.StartUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
            writeFeed: reservation.DinerUserId is not null,
            delivered,
            nowUtc,
            askOnYourWay);
    }

    /// <summary>The manage link a reminder was written with, or null.</summary>
    private static string? ManageUrlIn(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);

        return payload.RootElement.TryGetProperty("manageUrl", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;
    }

    /// <summary>A new booking has sent nothing yet.</summary>
    internal static readonly IReadOnlySet<string> NothingDelivered = new HashSet<string>();

    /// <summary>When the booking's reminder is due: the branch's reminder hours before its start.</summary>
    private static DateTime ReminderDueAt(Reservation reservation, ReservationPolicy policy) =>
        reservation.StartUtc.AddHours(-policy.ReminderHoursBefore);

    /// <summary>
    /// Writes the reminder - with its feed entry - and the late nudge for the booking as it now stands,
    /// into the caller's unit of work.
    /// </summary>
    /// <param name="db">The caller's unit of work, for the feed entry.</param>
    /// <param name="outbox">The caller's outbox, for the messages.</param>
    /// <param name="reservation">The booking, at its current start.</param>
    /// <param name="branch">Its branch, with the venue, for the names the messages carry.</param>
    /// <param name="tableLabel">The table, as the messages name it.</param>
    /// <param name="manageUrl">The manage link for a web booking, or null.</param>
    /// <param name="keyVersion">
    /// Null for a new booking, whose keys are <c>reservation:{id}:reminder</c> and
    /// <c>reservation:{id}:late-nudge</c>. A moved booking's carry the start they are for, because the
    /// messages for an earlier start keep their keys once sent.
    /// </param>
    /// <param name="writeFeed">Whether the account is live, under its lock, so the feed entry may be written.</param>
    /// <param name="delivered">Keys already sent: a message under one of them went out and is not written again.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="askOnYourWay">
    /// The booking has a pre-order not yet sent: ask "are you on your way?" 45 minutes before - see
    /// <see cref="PreOrderQuestions"/>.
    /// </param>
    internal static void EnqueueReminders(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        string tableLabel,
        string? manageUrl,
        string? keyVersion,
        bool writeFeed,
        IReadOnlySet<string> delivered,
        DateTime nowUtc,
        OnYourWayAsk askOnYourWay)
    {
        var policy = branch.ReservationPolicy;

        var notice = new
        {
            reservationId = reservation.Id,
            dinerUserId = reservation.DinerUserId,
            venueName = branch.Venue?.Name ?? branch.Name,
            branchName = branch.Name,
            tableLabel,
            localStartTime = reservation.LocalStartTime,
            reservationCode = reservation.Code,
            graceExtensionMinutes = policy.GraceExtensionMinutes,
            approved = false,
            manageUrl,
        };

        string KeyFor(string what) =>
            OutboxMessageTypes.KeyFor("reservation", reservation.Id, keyVersion is null ? what : $"{what}:{keyVersion}");

        var remindAt = ReminderDueAt(reservation, policy);
        var reminderKey = KeyFor("reminder");

        // A reminder whose moment has already passed is not written at all: a booking made an hour
        // before it starts has no three-hours-before, and queueing one in the past would only be
        // discarded by the staleness rule with a log line implying something went wrong.
        //
        // Nor one due while a short-notice booking is still waiting for the floor's answer. "Your table
        // is at 20:00" about a table nobody has promised yet is the wrong thing to say, and the answer
        // itself - "confirmed, they expect you at 20:00" - says the right one minutes later.
        var dueBeforeTheAnswer = reservation.ApprovalExpiresAtUtc is { } answerBy && remindAt <= answerBy;

        if (remindAt > nowUtc && !dueBeforeTheAnswer && !delivered.Contains(reminderKey))
        {
            outbox.Enqueue(OutboxMessageTypes.ReservationReminder, notice, remindAt, reminderKey);

            // Its feed entry, written now beside the push and appearing when the push is due (K12).
            // Cancelling the booking before then deletes both.
            if (writeFeed)
            {
                DinerNotices.BookingReminder(db, reservation, branch.Venue?.Name ?? branch.Name, branch.Name, remindAt);
            }
        }

        // The nudge is always in the future when the booking is made or moved, because neither can
        // start in the past, so it has no "already passed" rule. Only one already sent under the same
        // key - a move back to a start whose nudge went out - is not written twice.
        var nudgeKey = KeyFor("late-nudge");

        if (!delivered.Contains(nudgeKey))
        {
            outbox.Enqueue(
                OutboxMessageTypes.ReservationLateNudge,
                notice,
                reservation.StartUtc.AddMinutes(policy.LateNudgeAfterMinutes),
                nudgeKey);
        }

        // Pushed to an account only. A booking made on the web has no app to ask; its manage link asks instead, from the
        // same moment (PublicBookingService, OnYourWayAsked).
        if (askOnYourWay != OnYourWayAsk.None && reservation.DinerUserId is not null)
        {
            PreOrderQuestions.Enqueue(
                db, outbox, reservation, notice, branch.Venue?.Name ?? branch.Name, branch.Name, writeFeed, delivered,
                preOrderWaiting: askOnYourWay == OnYourWayAsk.WithPreOrder, nowUtc);
        }
    }

    /// <summary>Whether, and when, a booking is asked "are you on your way?" - see <see cref="PreOrderQuestions"/>.</summary>
    internal enum OnYourWayAsk
    {
        /// <summary>The floor has decided: the kitchen has the pre-order, or the party was marked not coming.</summary>
        None,

        /// <summary>At 30 minutes, as every booking.</summary>
        Plain,

        /// <summary>At 45 minutes, while a pre-order waits for the kitchen.</summary>
        WithPreOrder,
    }
}
