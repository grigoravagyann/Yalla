using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Messaging;

namespace Yalla.Infrastructure.Persistence;

/// <summary>What a <c>staff.push</c> outbox message carries: the kind, the thing it is about, and when.</summary>
/// <param name="Kind">One of the six <see cref="StaffPushKinds"/>.</param>
/// <param name="SubjectId">
/// The booking (approvals, bookingToCall), the guest's message (guestMessages), the service request (tableCalls), the
/// takeaway order (takeaway) or the tab order (kitchenReady).
/// </param>
/// <param name="OccurredAtUtc">
/// When it happened - or, for a push scheduled ahead (<see cref="StaffPushSignals.StageOne(YallaDbContext, StaffPushKinds, Guid, string, DateTime, DateTime)"/>),
/// when it is due. Older than <c>WebPush:TtlSeconds</c> at send time, it is dropped.
/// </param>
internal sealed record StaffPushNotice(StaffPushKinds Kind, Guid SubjectId, DateTime OccurredAtUtc);

/// <summary>
/// Writes a <c>staff.push</c> outbox message into the same save as each event the floor is pushed about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Told, not detected.</b> Every kind is staged by the domain-event handler of the event the floor is told about,
/// through <see cref="StageOne(YallaDbContext, StaffPushKinds, Guid, string, DateTime)"/>, in the same save and transaction
/// as its cause or not at all - or, for one due later, its overload with the due time. Until 2026-10-25
/// <b>approvals</b> was still detected here from the change tracker on every save (<c>Stage</c>); it is the bookings'
/// <c>ApprovalStaffPush</c> now, and nothing is detected.
/// </para>
/// <para>
/// <b>Only the kind and an id.</b> Who is told, in which language, and whether it is still true are decided when the
/// message is sent, seconds later, by <c>StaffPushHandler</c>: a call answered meanwhile buzzes nobody.
/// </para>
/// <para>
/// The events, as the five switches name them:
/// </para>
/// <list type="bullet">
/// <item><b>approvals</b> - a booking made waiting for approval, one moved into it, a waiting one moved to another
/// time, or a waiting one now waiting for another reason (the guest before it stepped aside and the floor decides now).
/// Staged by the <c>ReservationMade</c>, <c>ReservationRescheduled</c> and <c>ReservationWaitingInstead</c> domain-event
/// handler (<c>ApprovalStaffPush</c>), key <c>staff-push:approval:{id}:{n}</c>.</item>
/// <item><b>guestMessages</b> - a guest's text or answer ("I'm coming", "late", "not coming") added to a thread, from the
/// app, the manage link or the answer buttons. Staged by the <c>GuestMessageWritten</c> and <c>GuestAnswered</c>
/// domain-event handler (<c>GuestMessageStaffPush</c>).</item>
/// <item><b>tableCalls</b> - a service request raised: a waiter called, water, napkins, the bill. Staged by the
/// <c>ServiceRequestRaised</c> domain-event handler (<c>TableCallStaffPush</c>).</item>
/// <item><b>takeaway</b> - a takeaway order placed and waiting to be accepted (one a branch accepts automatically
/// needs nobody, and is not pushed). Staged by the <c>TakeawayPlaced</c> domain-event handler
/// (<c>TakeawayStaffPush</c>).</item>
/// <item><b>kitchenReady</b> - a tab order moved to <c>Ready</c>. Staged by the <c>TabOrderMoved</c> domain-event
/// handler (<c>KitchenReadyStaffPush</c>): see <c>docs/domain-events.md</c>.</item>
/// <item><b>bookingToCall</b> - a booking a member of staff took by hand starts in thirty minutes: call the guest. Staged
/// by the <c>ReservationMade</c> domain-event handler (<c>BookingToCallStaffPush</c>) when the booking is made, for
/// thirty minutes before its start (the <c>StageOne</c> overload with a due time), key <c>staff-push:to-call:{id}</c>.</item>
/// </list>
/// </remarks>
internal static class StaffPushSignals
{
    /// <summary>The key prefix of every staff push, so nothing else's cancellation can reach one.</summary>
    public const string KeyPrefix = "staff-push:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stages one staff push in the caller's unit of work - for a domain-event handler, which knows the event rather than
    /// detecting it. The key carries a fresh id after the subject when the event can happen again (<see cref="Again"/>).
    /// </summary>
    public static void StageOne(YallaDbContext db, StaffPushKinds kind, Guid subjectId, string key, DateTime nowUtc) =>
        StageAll(db, [(kind, subjectId, key)], nowUtc, nowUtc);

    /// <summary>
    /// Stages one staff push due at <paramref name="scheduledForUtc"/> rather than now - the outbox holds it until then,
    /// and the handler judges whether it is still true when its turn comes. The notice is stamped with the due time, so
    /// the handler's "too old to send" rule reads the wait as the outbox's, not as a push that went stale.
    /// </summary>
    public static void StageOne(
        YallaDbContext db, StaffPushKinds kind, Guid subjectId, string key, DateTime nowUtc, DateTime scheduledForUtc) =>
        StageAll(db, [(kind, subjectId, key)], nowUtc, scheduledForUtc);

    /// <summary>
    /// A fresh id for the key of an event that can happen to the same thing more than once - a booking sent back for
    /// approval again, an order up again: an instant would collide under a clock that stands still, and a collision on
    /// the unique key would fail the save that caused the event.
    /// </summary>
    public static string Again() => Guid.CreateVersion7().ToString("N");

    private static void StageAll(
        YallaDbContext db,
        List<(StaffPushKinds Kind, Guid SubjectId, string Key)> events,
        DateTime nowUtc,
        DateTime scheduledForUtc)
    {
        // Already staged in this save - a save that is retried runs this again, and the key is unique.
        var staged = db.ChangeTracker.Entries<OutboxMessage>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == OutboxMessageTypes.StaffPush)
            .Select(e => PayloadSubject(e.Entity.PayloadJson))
            .ToHashSet();

        foreach (var (kind, subjectId, key) in events)
        {
            if (!staged.Add((kind, subjectId)))
            {
                continue;
            }

            db.OutboxMessages.Add(new OutboxMessage(
                OutboxMessageTypes.StaffPush,
                JsonSerializer.Serialize(new StaffPushNotice(kind, subjectId, scheduledForUtc), Json),
                scheduledForUtc,
                key,
                nowUtc));
        }
    }

    /// <summary>Reads a staff push payload back.</summary>
    public static StaffPushNotice Read(string payloadJson) =>
        JsonSerializer.Deserialize<StaffPushNotice>(payloadJson, Json)
        ?? throw new InvalidOperationException("A staff push payload did not deserialise.");

    private static (StaffPushKinds, Guid) PayloadSubject(string payloadJson)
    {
        var notice = Read(payloadJson);

        return (notice.Kind, notice.SubjectId);
    }
}
