using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// What the outbox carries for each message type.
/// </summary>
/// <remarks>
/// <b>Self-contained on purpose.</b> A payload of ids alone would have to re-read the world at send
/// time, and the world has moved on: the booking may have been cancelled, the venue renamed, the
/// diner's name changed. What was true when the message was written is what the message should say.
/// The one thing read at send time is the <i>state</i> that decides whether to send at all.
/// </remarks>
internal sealed record ReservationNotice(
    Guid ReservationId,
    Guid DinerUserId,
    string VenueName,
    string BranchName,
    string TableLabel,
    TimeOnly LocalStartTime,
    string ReservationCode,
    int GraceExtensionMinutes = 0,
    bool Approved = false,
    bool Expired = false,
    bool WasChange = false,
    bool OriginalKept = false,
    DeclineReason? DeclinedBecause = null,
    DateOnly? LocalDate = null,
    int PartySize = 0,
    ReservationStatus? Status = null,
    bool StaffAsked = false);

/// <summary>A later guest's request for a booking's table, as the earlier guest is told about it.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="ReservationId">The earlier guest's booking - where a tap lands.</param>
/// <param name="DinerUserId">The earlier guest.</param>
/// <param name="VenueName">The venue, as it was named when the request was made.</param>
/// <param name="TableLabel">The table.</param>
/// <param name="RequestedLocalTime">When the later guest would sit down, on the branch's wall clock.</param>
/// <param name="FreeByLocalTime">The time to have left by, on the branch's wall clock.</param>
/// <param name="FreeByUtc">The same instant, for the app's own countdown and text.</param>
internal sealed record TableRequestNotice(
    Guid RequestId,
    Guid ReservationId,
    Guid DinerUserId,
    string VenueName,
    string TableLabel,
    TimeOnly RequestedLocalTime,
    TimeOnly FreeByLocalTime,
    DateTime FreeByUtc);

/// <param name="TabId">The tab, which is where a tap lands.</param>
/// <param name="ParticipantId">Who it is about: the approved joiner, whoever ordered, or the one waiting.</param>
/// <param name="DinerUserId">Who it is sent to.</param>
/// <param name="TableLabel">The table as it was labelled when the message was written.</param>
/// <param name="GuestName">The name a waiting joiner gave, for the host. Null when they gave none.</param>
internal sealed record TabNotice(
    Guid TabId,
    Guid ParticipantId,
    Guid DinerUserId,
    string TableLabel,
    string? GuestName = null);

/// <summary>
/// Sends the reminder, if the booking is still worth reminding anybody about.
/// </summary>
/// <remarks>
/// <para>
/// The state check happens here rather than at write time, because hours pass in between. Cancelling
/// a booking deletes its unsent reminder, so this is the belt to that braces - a booking seated early
/// or cancelled by staff through some path that forgot still produces no push.
/// </para>
/// <para>
/// A booking still waiting for the venue is reminded - a large party next week the venue has not got
/// to yet is still the diner's plan - <b>except one waiting on a short-notice answer</b>. That one
/// has a deadline minutes away, and "your table is at 20:00" about a table nobody has promised is the
/// wrong thing to say; the answer itself says the right thing. Its reminder is not even written when
/// it would fall before the deadline, so this is the braces for a sweep running behind.
/// </para>
/// </remarks>
internal sealed class ReservationReminderHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationReminderHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationReminder;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => new { r.Status, AwaitsShortNoticeAnswer = r.ApprovalExpiresAtUtc != null })
            .FirstOrDefaultAsync(cancellationToken);

        var status = booking?.Status;

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            logger.LogInformation(
                "Skipping the reminder for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        if (status == ReservationStatus.PendingApproval && booking!.AwaitsShortNoticeAnswer)
        {
            logger.LogInformation(
                "Skipping the reminder for booking {ReservationId}: it is still waiting on the venue's short-notice answer.",
                notice.ReservationId);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.Reminder(
            locale, notice.VenueName, notice.BranchName, notice.TableLabel, notice.LocalStartTime);

        // The action lives in the payload, so the diner cancels from the lock screen. That is the
        // whole feature: cancelling has to be easier than not showing up.
        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-reminder",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["code"] = notice.ReservationCode,
                    ["action"] = "cancel",
                    ["branch"] = notice.BranchName,
                },
                CategoryId: "reservation-reminder"),
            cancellationToken);
    }
}

/// <summary>"Still coming?", with the one-tap hold extension behind it.</summary>
internal sealed class ReservationLateNudgeHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationLateNudgeHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationLateNudge;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        // Seated is the case this exists to avoid. Nudging somebody who is already at the table,
        // eating, is the notification that makes a diner turn them all off.
        if (status != ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "Skipping the late nudge for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.LateNudge(
            locale, notice.VenueName, notice.TableLabel, notice.GraceExtensionMinutes);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-late-nudge",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["action"] = "extend-hold",
                    ["extensionMinutes"] = notice.GraceExtensionMinutes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                },
                CategoryId: "late-nudge"),
            cancellationToken);
    }
}

/// <summary>
/// "Are you on your way?", 45 minutes before a booking with a pre-order, with "Yes, on my way" and "Can't come" behind
/// it - so the waiter knows whether to send the pre-order to the kitchen without phoning.
/// </summary>
/// <remarks>
/// <para>
/// Re-reads the state that decides whether to ask at all, as every handler here does. Asked of a <b>confirmed</b> booking
/// only - the answer refuses anything else, and a booking still waiting for the venue's yes is not expected yet. Every
/// booking is asked (2026-10-21); not once its pre-order was sent or the party marked not coming - the floor has decided
/// - or when the guest already said they are coming or running late, or the venue already asked. The wording says
/// whether a pre-order is waiting for the kitchen.
/// </para>
/// <para>
/// The venue's "Ask: are you coming?" is <b>not</b> sent through here: since guest messages (2026-10-23) it is a message
/// in the booking's thread, pushed as <c>message.to-guest</c> by <see cref="VenueMessageHandler"/>. <c>staffAsked</c> on
/// the notice is the earlier path, kept only so a message of that kind still queued when this shipped is delivered as
/// it was written - sent at once, whatever the floor decided. Nothing enqueues one any more.
/// </para>
/// </remarks>
internal sealed class ReservationOnYourWayHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationOnYourWayHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationOnYourWay;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => new
            {
                r.Status,
                Answered = r.GuestConfirmedOnWayAtUtc != null || r.GuestRunningLateAtUtc != null,
                StaffAsked = r.StaffAskedOnMyWayAtUtc != null,
                PreOrderWaiting = db.PreOrders.Any(p => p.ReservationId == r.Id
                                                        && p.SentAtUtc == null
                                                        && p.NotComingAtUtc == null
                                                        && p.Lines.Any()),
                FloorDecided = db.PreOrders.Any(p => p.ReservationId == r.Id
                                                     && (p.SentAtUtc != null || p.NotComingAtUtc != null)),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var expected = booking is { Status: ReservationStatus.Confirmed };

        // A venue's "ask now" queued before guest messages shipped goes whatever the floor decided about a pre-order -
        // somebody tapped it. The automatic question is not asked on top of the venue's: the guest has been asked already.
        if (!expected
            || booking!.Answered
            || (!notice.StaffAsked && (booking.FloorDecided || booking.StaffAsked)))
        {
            logger.LogInformation(
                "Not asking booking {ReservationId} whether they are on their way: it is {Status}, answered {Answered}, "
                + "decided by the floor {Decided}.",
                notice.ReservationId, booking?.Status, booking?.Answered, booking?.FloorDecided);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = booking.PreOrderWaiting
            ? NotificationText.OnYourWay(locale, notice.VenueName, notice.LocalStartTime)
            : NotificationText.OnYourWayWithoutPreOrder(locale, notice.VenueName, notice.LocalStartTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-on-your-way",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["code"] = notice.ReservationCode,
                    ["actions"] = "on-my-way,cancel",
                },
                CategoryId: "on-your-way"),
            cancellationToken);
    }
}

/// <summary>
/// Staff approved or declined a booking that was waiting - or nobody answered a short-notice one in
/// time and it was declined for them.
/// </summary>
/// <remarks>
/// <para>
/// <c>expired</c> in the data tells the app which decline it is, so it can apologise rather than say
/// the venue turned the diner down. A message written before the flag existed reads as not expired.
/// </para>
/// <para>
/// <c>wasChange</c> and <c>originalKept</c> say what a declined <i>change</i> of a confirmed booking
/// came to: the booking stands at the slot it had - the body names its time - or that slot was gone
/// too and the booking with it.
/// </para>
/// </remarks>
internal sealed class ReservationDecidedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationDecided;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = notice switch
        {
            { Approved: true } => NotificationText.ReservationApproved(locale, notice.VenueName, notice.LocalStartTime),
            { WasChange: true, OriginalKept: true } =>
                NotificationText.ReservationChangeDeclined(locale, notice.VenueName, notice.LocalStartTime, notice.Expired),
            { WasChange: true } => NotificationText.ReservationChangeDeclinedOriginalGone(locale, notice.VenueName, notice.Expired),
            { DeclinedBecause: DeclineReason.EarlierGuestDeclined } =>
                NotificationText.ReservationEarlierGuestDeclined(locale, notice.VenueName, notice.LocalStartTime),
            { DeclinedBecause: DeclineReason.EarlierGuestNoAnswer } =>
                NotificationText.ReservationEarlierGuestNoAnswer(locale, notice.VenueName, notice.LocalStartTime),
            { Expired: true } => NotificationText.ReservationExpired(locale, notice.VenueName),
            _ => NotificationText.ReservationRejected(locale, notice.VenueName),
        };

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-decided",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["approved"] = notice.Approved ? "true" : "false",
                    ["expired"] = notice.Expired ? "true" : "false",
                    ["wasChange"] = notice.WasChange ? "true" : "false",
                    ["originalKept"] = notice.OriginalKept ? "true" : "false",

                    // Why a decline happened, as the DeclineReason number - "3" the guest before said no,
                    // "4" they did not answer - absent for an approval and for a decline written before.
                    ["declinedBecause"] = notice.DeclinedBecause is { } because
                        ? ((int)because).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty,
                }),
            cancellationToken);
    }
}

/// <summary>
/// To the booking's account: somebody holding the booking's manage link changed it, or cancelled it.
/// </summary>
/// <remarks>
/// <para>
/// The link goes round group chats, so whoever acted may not be the diner - and a booking moved without
/// their knowing becomes their no-show. So the account holder is told, whatever the booking has done since:
/// a later change from the link replaces this message while it is unsent, and a cancel replaces it too.
/// </para>
/// <para>
/// One class for both types: the words differ, the delivery does not. The data carries <c>kind</c>
/// (<c>reservation-changed-by-link</c> or <c>reservation-cancelled-by-link</c>) and <c>reservationId</c>, which
/// is where a tap lands.
/// </para>
/// </remarks>
internal abstract class ManageLinkActionHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public abstract string MessageType { get; }

    protected abstract string Kind { get; }

    protected abstract (string Title, string Body) Text(string locale, ReservationNotice notice);

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = Text(locale, notice);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = Kind,
                    ["reservationId"] = notice.ReservationId.ToString(),
                }),
            cancellationToken);
    }

    /// <summary>The day the notice was written about; a payload always carries it.</summary>
    protected static DateOnly DateOf(ReservationNotice notice) =>
        notice.LocalDate ?? throw new InvalidOperationException("A manage-link notice carries the booking's date.");
}

/// <summary>"Your booking was changed" - see <see cref="ManageLinkActionHandler"/>.</summary>
internal sealed class ReservationChangedByLinkHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationChangedByLink;

    protected override string Kind => "reservation-changed-by-link";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.ReservationChangedByLink(
            locale,
            notice.VenueName,
            DateOf(notice),
            notice.LocalStartTime,
            notice.PartySize,
            waiting: notice.Status == ReservationStatus.PendingApproval);
}

/// <summary>"Your booking was cancelled" - see <see cref="ManageLinkActionHandler"/>.</summary>
internal sealed class ReservationCancelledByLinkHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationCancelledByLink;

    protected override string Kind => "reservation-cancelled-by-link";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.ReservationCancelledByLink(locale, notice.VenueName, DateOf(notice), notice.LocalStartTime);
}

/// <summary>"Booking paused for a week" - to a guest just blocked for three no-shows in three days.</summary>
internal sealed class DinerBookingsBlockedHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.DinerBookingsBlocked;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<BookingsBlockedNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        // The day on the venue's wall clock, not UTC's (review N3); a payload from before it falls back to UTC.
        var (title, body) = NotificationText.BookingsBlocked(
            locale, notice.BlockedUntilLocalDate ?? DateOnly.FromDateTime(notice.BlockedUntilUtc), notice.CancelledCount);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "bookings-blocked-no-shows",
                    ["blockedUntilUtc"] = notice.BlockedUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }

    /// <summary>The push's payload.</summary>
    private sealed record BookingsBlockedNotice(
        Guid DinerUserId, DateTime BlockedUntilUtc, int CancelledCount, DateOnly? BlockedUntilLocalDate = null);
}

/// <summary>"You can book again" - to a guest whose no-show block was lifted early.</summary>
internal sealed class DinerBookingsUnblockedHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.DinerBookingsUnblocked;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<UnblockedNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = NotificationText.BookingsUnblocked(locale);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "bookings-unblocked",
                    ["liftedBecause"] = ((int?)notice.LiftedBecause ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }

    private sealed record UnblockedNotice(Guid DinerUserId, Domain.Occupancy.BookingBlockLiftCause? LiftedBecause = null);
}

/// <summary>"Marked as a no-show at {venue}" - to the booking's account, every time (review B1d).</summary>
internal sealed class ReservationNoShowHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationNoShow;

    protected override string Kind => "booking-no-show";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.NoShowMarked(locale, notice.VenueName, DateOf(notice), notice.LocalStartTime);
}

/// <summary>
/// "Someone would like your table at 19:45. Will you have left by 19:40?" - to the guest with the table
/// first, with Yes and No on the notification (the <c>table-request</c> category).
/// </summary>
/// <remarks>
/// Sent only while the question is still open: answered from the booking's card, withdrawn because one
/// of the two bookings moved or ended, or past its deadline, the push would ask about something already
/// settled - and teach the guest to ignore the next one.
/// </remarks>
internal sealed class TableRequestHandler(
    YallaDbContext db,
    IClock clock,
    INotificationChannel channel,
    ILogger<TableRequestHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationTableRequest;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TableRequestNotice>(payloadJson);
        var nowUtc = clock.UtcNow;

        var open = await db.TableShareRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == notice.RequestId
                     && r.Status == TableShareRequestStatus.Pending
                     && r.ExpiresAtUtc > nowUtc,
                cancellationToken);

        if (!open)
        {
            logger.LogInformation(
                "Skipping the request for booking {ReservationId}'s table: request {RequestId} is no longer open.",
                notice.ReservationId, notice.RequestId);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.TableRequested(
            locale, notice.VenueName, notice.RequestedLocalTime, notice.FreeByLocalTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "table-request",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["requestId"] = notice.RequestId.ToString(),
                    ["requestedTime"] = notice.RequestedLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByTime"] = notice.FreeByLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByUtc"] = notice.FreeByUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                },
                CategoryId: "table-request"),
            cancellationToken);
    }
}

/// <summary>
/// To the guest who said yes - or for whom the floor said it after calling: "please free the table by
/// 19:40". Sent while the booking still holds the table.
/// </summary>
internal sealed class TableRequestDecidedHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<TableRequestDecidedHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationTableRequestDecided;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TableRequestNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval or ReservationStatus.Seated))
        {
            logger.LogInformation(
                "Skipping the free-by reminder for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.TableRequestAccepted(locale, notice.VenueName, notice.FreeByLocalTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "table-request-decided",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["requestId"] = notice.RequestId.ToString(),
                    ["accepted"] = "true",
                    ["freeByTime"] = notice.FreeByLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByUtc"] = notice.FreeByUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }
}

/// <summary>The host let a pending joiner onto the tab.</summary>
internal sealed class ParticipantApprovedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ParticipantApproved;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.ParticipantApproved(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "participant-approved",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}

/// <summary>Somebody scanned the table and is waiting for the host to let them on.</summary>
/// <remarks>
/// Sent to the host only while the joiner is still waiting. The host may have seen them on the
/// People screen first and let them on, or turned them away; "Anna wants to join" after Anna is
/// already on the tab is the push that teaches a host to ignore the next one.
/// </remarks>
internal sealed class JoinRequestedHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<JoinRequestedHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.JoinRequested;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);

        // Still a question this person can answer: the joiner still waiting, the tab still taking
        // people - closing leaves a pending joiner pending, and nobody new is let on a tab being
        // settled - and the one it is addressed to still its host, which staff can change.
        var standing = await db.TabParticipants
            .AsNoTracking()
            .Where(p => p.Id == notice.ParticipantId && p.TabId == notice.TabId)
            .Select(p => new
            {
                p.Status,
                TabStatus = p.Tab.Status,
                HostUserId = p.Tab.Participants
                    .Where(h => h.Id == p.Tab.HostParticipantId)
                    .Select(h => h.UserId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (standing is not { Status: ParticipantStatus.PendingApproval, TabStatus: TabStatus.Open }
            || standing.HostUserId != notice.DinerUserId)
        {
            logger.LogInformation(
                "Skipping the join request for participant {ParticipantId} on tab {TabId}: they are {Status}, "
                + "the tab is {TabStatus}, and the host it was written for is {Host} its host.",
                notice.ParticipantId,
                notice.TabId,
                standing?.Status,
                standing?.TabStatus,
                standing?.HostUserId == notice.DinerUserId ? "still" : "no longer");

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.JoinRequested(locale, notice.TableLabel, notice.GuestName);

        // The tab, so a tap opens the bill the request is waiting on; the participant too, so a
        // client can go straight to them. The kind is camelCase, unlike the older kebab-case ones,
        // because it is the exact string the diner app routes this tap on.
        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "tabJoinRequested",
                    ["tabId"] = notice.TabId.ToString(),
                    ["participantId"] = notice.ParticipantId.ToString(),
                }),
            cancellationToken);
    }
}

/// <summary>Food is up. Per-branch, and off by default - noisy in a cafe, useful in a canteen.</summary>
internal sealed class OrderReadyHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.OrderReady;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.OrderReady(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "order-ready",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}

/// <summary>A takeaway order's news, as its guest is told it.</summary>
/// <param name="OrderId">The order - where a tap lands.</param>
/// <param name="DinerUserId">Who is told.</param>
/// <param name="VenueName">The venue, as named when the message was written.</param>
/// <param name="BranchName">The branch.</param>
/// <param name="Code">"T-482".</param>
/// <param name="ReadyAtLocalTime">When it will be ready, on the branch's wall clock. Accepted only.</param>
/// <param name="Reason">What the venue said, declining. Null when it said nothing.</param>
/// <param name="NoAnswer">Declined because nobody at the venue answered in time.</param>
internal sealed record TakeawayNotice(
    Guid OrderId,
    Guid DinerUserId,
    string VenueName,
    string BranchName,
    string Code,
    TimeOnly? ReadyAtLocalTime = null,
    string? Reason = null,
    bool NoAnswer = false);

/// <summary>
/// A takeaway order moved on: accepted, ready or declined. Sent only while the order is still where the message says it
/// is - an "accepted" push for an order the guest has since collected says nothing true.
/// </summary>
internal abstract class TakeawayHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public abstract string MessageType { get; }

    /// <summary>The <c>kind</c> in the data - the feed entry's kind too.</summary>
    protected abstract string Kind { get; }

    /// <summary>The statuses in which the message is still true.</summary>
    protected abstract IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile { get; }

    protected abstract (string Title, string Body) Text(string locale, TakeawayNotice notice);

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TakeawayNotice>(payloadJson);

        var status = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.Id == notice.OrderId)
            .Select(o => (Domain.Takeaway.TakeawayStatus?)o.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not { } current || !SendWhile.Contains(current))
        {
            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = Text(locale, notice);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = Kind,
                    ["orderId"] = notice.OrderId.ToString(),
                    ["code"] = notice.Code,
                }),
            cancellationToken);
    }
}

/// <summary>"Order accepted - ready at 19:10."</summary>
internal sealed class TakeawayAcceptedHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayAccepted;

    protected override string Kind => "takeaway-accepted";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Accepted];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayAccepted(locale, notice.VenueName, notice.Code, notice.ReadyAtLocalTime ?? default);
}

/// <summary>"Your order is ready - come and pick it up."</summary>
internal sealed class TakeawayReadyHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayReady;

    protected override string Kind => "takeaway-ready";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Ready];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayReady(locale, notice.VenueName, notice.Code);
}

/// <summary>"Order declined" - with the venue's reason, or the apology for nobody answering.</summary>
internal sealed class TakeawayDeclinedHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayDeclined;

    protected override string Kind => "takeaway-declined";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Declined];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayDeclined(locale, notice.VenueName, notice.Code, notice.Reason, notice.NoAnswer);
}

/// <summary>A venue message, as its thread's guest is told about it.</summary>
/// <param name="ConversationId">The thread - where a tap lands.</param>
/// <param name="MessageId">The message.</param>
/// <param name="DinerUserId">Who is told.</param>
/// <param name="VenueName">The venue, as named when the message was written - the title.</param>
/// <param name="ReservationId">The booking, for a booking's thread.</param>
/// <param name="TakeawayOrderId">The order, for a takeaway order's thread.</param>
/// <param name="Ask">The venue's "are you coming?" rather than words.</param>
/// <param name="Preview">The words, at most 140 characters. Null for an ask.</param>
/// <param name="LocalStartTime">The booking's start on the branch's wall clock, for the ask's "at 19:30".</param>
internal sealed record VenueMessageNotice(
    Guid ConversationId,
    Guid MessageId,
    Guid DinerUserId,
    string VenueName,
    Guid? ReservationId,
    Guid? TakeawayOrderId,
    bool Ask,
    string? Preview,
    TimeOnly? LocalStartTime);

/// <summary>
/// "Lava restaurant: Of course, without onion." - somebody at the venue wrote in a booking's or an order's thread, or
/// asked "are you coming?".
/// </summary>
/// <remarks>
/// <para>
/// Sent while the thread is still the account's it was written for (a deletion cuts the link), and an ask only while it
/// is still a question: the booking confirmed and the guest not yet answered - "are you coming?" after "I'm on my way"
/// is the push that teaches a guest to switch them off.
/// </para>
/// <para>
/// <b>The category</b> - the actions the app hangs under the push - follows the branch's <c>guestsCanWrite</c> when it
/// is sent: a Reply the venue would refuse with 403 <c>guests-cannot-write</c> is not offered. Guests may write:
/// <c>venue-ask</c> ("I'm coming", "Late 15 min", a text reply) and <c>venue-message</c> (a text reply). They may not:
/// <c>venue-ask-buttons</c> (the two answers, no reply) and <c>venue-message-readonly</c> (no actions).
/// </para>
/// </remarks>
internal sealed class VenueMessageHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<VenueMessageHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.MessageToGuest;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<VenueMessageNotice>(payloadJson);

        var thread = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == notice.ConversationId && c.DinerUserId == notice.DinerUserId)
            .Select(c => new
            {
                GuestsCanWrite = db.Branches.Where(b => b.Id == c.BranchId).Select(b => b.GuestsCanMessage).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (thread is null)
        {
            logger.LogInformation(
                "Not telling anybody about message {MessageId}: its thread no longer belongs to the account it was written for.",
                notice.MessageId);

            return;
        }

        if (notice.Ask && notice.ReservationId is { } reservationId)
        {
            var booking = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => new
                {
                    r.Status,
                    Answered = r.GuestConfirmedOnWayAtUtc != null || r.GuestRunningLateAtUtc != null,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (booking is not { Status: ReservationStatus.Confirmed, Answered: false })
            {
                logger.LogInformation(
                    "Not asking booking {ReservationId} whether they are coming: it is {Status}, answered {Answered}.",
                    reservationId, booking?.Status, booking?.Answered);

                return;
            }
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = NotificationText.VenueMessage(locale, notice.VenueName, notice.Preview, notice.Ask, notice.LocalStartTime);

        var data = new Dictionary<string, string>
        {
            ["kind"] = "venue-message",
            ["conversationId"] = notice.ConversationId.ToString(),
            ["ask"] = notice.Ask ? "true" : "false",
        };

        if (notice.ReservationId is { } booked)
        {
            data["reservationId"] = booked.ToString();
        }

        if (notice.TakeawayOrderId is { } ordered)
        {
            data["takeawayOrderId"] = ordered.ToString();
        }

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                data,
                CategoryId: VenueMessageCategories.For(notice.Ask, thread.GuestsCanWrite)),
            cancellationToken);
    }
}

/// <summary>
/// The push categories of a venue message (<c>message.to-guest</c>). The app registers all four; the server picks by the
/// message's kind and whether the branch lets guests type.
/// </summary>
internal static class VenueMessageCategories
{
    /// <summary>An ask, guests may type: "I'm coming", "Late 15 min" and Reply (a text input).</summary>
    public const string Ask = "venue-ask";

    /// <summary>An ask, guests may not type: "I'm coming" and "Late 15 min", no Reply.</summary>
    public const string AskButtons = "venue-ask-buttons";

    /// <summary>A text, guests may type: Reply (a text input).</summary>
    public const string Message = "venue-message";

    /// <summary>A text, guests may not type: no actions - a tap opens the thread.</summary>
    public const string MessageReadOnly = "venue-message-readonly";

    public static string For(bool ask, bool guestsCanWrite) => (ask, guestsCanWrite) switch
    {
        (true, true) => Ask,
        (true, false) => AskButtons,
        (false, true) => Message,
        (false, false) => MessageReadOnly,
    };
}

/// <summary>Shared bits every handler needs.</summary>
internal static class Payload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T Read<T>(string payloadJson) =>
        JsonSerializer.Deserialize<T>(payloadJson, Json)
        ?? throw new InvalidOperationException($"An outbox payload did not deserialise to {typeof(T).Name}.");

    /// <summary>
    /// The language to write in: the diner's most recently used device's.
    /// </summary>
    /// <remarks>
    /// <b>Never the branch's.</b> A Russian-speaking regular at an Armenian venue gets Russian.
    /// Falls back to Armenian when the diner has no device on file, which is also the case where
    /// nothing will be delivered anyway - the fallback is there so the message is still renderable
    /// and loggable rather than throwing.
    /// </remarks>
    public static async Task<string> LocaleForAsync(
        YallaDbContext db,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        var locale = await db.DinerDevices
            .AsNoTracking()
            .Where(d => d.DinerUserId == dinerUserId && d.RevokedAtUtc == null)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .Select(d => d.Locale)
            .FirstOrDefaultAsync(cancellationToken);

        return NotificationText.Normalise(locale);
    }
}
