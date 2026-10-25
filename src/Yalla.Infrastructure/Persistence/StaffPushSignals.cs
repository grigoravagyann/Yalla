using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Messaging;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence;

/// <summary>What a <c>staff.push</c> outbox message carries: the kind, the thing it is about, and when.</summary>
/// <param name="Kind">One of the five <see cref="StaffPushKinds"/>.</param>
/// <param name="SubjectId">
/// The booking (approvals), the guest's message (guestMessages), the service request (tableCalls), the takeaway order
/// (takeaway) or the tab order (kitchenReady).
/// </param>
/// <param name="OccurredAtUtc">When it happened. Older than <c>WebPush:TtlSeconds</c> at send time, it is dropped.</param>
internal sealed record StaffPushNotice(StaffPushKinds Kind, Guid SubjectId, DateTime OccurredAtUtc);

/// <summary>
/// Writes a <c>staff.push</c> outbox message into the same save as each event the floor is pushed about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here, beside the change-stream signals, for their reason.</b> A booking lands in "waiting for approval" from the
/// app, the public page, a reschedule, a change sent back for approval and the guest before it stepping aside; a guest
/// message from the app, the manage link and the answer buttons; a table call from a scan and from the bill sheet. A
/// rule each of those paths had to remember is a rule one of them forgets, and the symptom - a phone that stays quiet
/// - looks exactly like a quiet evening. So the save itself notices, from the change tracker, and the message goes in
/// the same transaction as its cause or not at all.
/// </para>
/// <para>
/// <b>Only the kind and an id.</b> Who is told, in which language, and whether it is still true are decided when the
/// message is sent, seconds later, by <c>StaffPushHandler</c>: a call answered meanwhile buzzes nobody.
/// </para>
/// <para>
/// The events, as the five switches name them:
/// </para>
/// <list type="bullet">
/// <item><b>approvals</b> - a booking added as <c>PendingApproval</c>, one moved into it, a waiting one moved to
/// another time, or a waiting one now waiting for another reason (the guest before it stepped aside and the floor
/// decides now).</item>
/// <item><b>guestMessages</b> - a guest's text or answer ("I'm coming", "late", "not coming") added to a thread.</item>
/// <item><b>tableCalls</b> - a service request raised: a waiter called, water, napkins, the bill. Staged by the
/// <c>ServiceRequestRaised</c> domain-event handler (<c>TableCallStaffPush</c>) rather than detected here.</item>
/// <item><b>takeaway</b> - a takeaway order placed and waiting to be accepted (one a branch accepts automatically
/// needs nobody, and is not pushed). Staged by the <c>TakeawayPlaced</c> domain-event handler
/// (<c>TakeawayStaffPush</c>) rather than detected here.</item>
/// <item><b>kitchenReady</b> - a tab order moved to <c>Ready</c>. Staged by the <c>TabOrderMoved</c> domain-event
/// handler (<c>KitchenReadyStaffPush</c>) rather than detected here: see <c>docs/domain-events.md</c>.</item>
/// </list>
/// </remarks>
internal static class StaffPushSignals
{
    /// <summary>The key prefix of every staff push, so nothing else's cancellation can reach one.</summary>
    public const string KeyPrefix = "staff-push:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Stage(YallaDbContext db, DateTime nowUtc)
    {
        // One scan of the tracker for the six reads below, rather than one each: every save passes through here.
        var detecting = db.ChangeTracker.AutoDetectChangesEnabled;

        if (detecting)
        {
            db.ChangeTracker.DetectChanges();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
        }

        try
        {
            StageDetected(db, nowUtc);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = detecting;
        }
    }

    private static void StageDetected(YallaDbContext db, DateTime nowUtc)
    {
        var events = new List<(StaffPushKinds Kind, Guid SubjectId, string Key)>();

        foreach (var entry in db.ChangeTracker.Entries<Reservation>())
        {
            if (entry.Entity.Status != ReservationStatus.PendingApproval)
            {
                continue;
            }

            var entered = entry.State == EntityState.Added
                          || (entry.State == EntityState.Modified
                              && ((entry.Property(r => r.Status).IsModified
                                   && entry.Property(r => r.Status).OriginalValue != ReservationStatus.PendingApproval)
                                  || entry.Property(r => r.StartUtc).IsModified
                                  || entry.Property(r => r.ApprovalTrigger).IsModified));

            if (entered)
            {
                events.Add((StaffPushKinds.Approvals, entry.Entity.Id, $"{KeyPrefix}approval:{entry.Entity.Id}:{Again()}"));
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<ConversationMessage>())
        {
            if (entry.State == EntityState.Added
                && entry.Entity.Author == MessageAuthor.Guest
                && entry.Entity.Kind is MessageKind.Text or MessageKind.Answer)
            {
                events.Add((StaffPushKinds.GuestMessages, entry.Entity.Id, $"{KeyPrefix}message:{entry.Entity.Id}"));
            }
        }

        // kitchenReady is not detected here any more: it is the TabOrderMoved handler's (KitchenReadyStaffPush), the
        // first kind to move onto domain events (docs/domain-events.md). tableCalls and takeaway are the
        // ServiceRequestRaised and TakeawayPlaced handlers' (TableCallStaffPush, TakeawayStaffPush). The other two
        // follow their verticals.
        if (events.Count == 0)
        {
            return;
        }

        StageAll(db, events, nowUtc);
    }

    /// <summary>
    /// Stages one staff push in the caller's unit of work - for a domain-event handler, which knows the event rather than
    /// detecting it. The key carries a fresh id after the subject when the event can happen again (<see cref="Again"/>).
    /// </summary>
    public static void StageOne(YallaDbContext db, StaffPushKinds kind, Guid subjectId, string key, DateTime nowUtc) =>
        StageAll(db, [(kind, subjectId, key)], nowUtc);

    /// <summary>
    /// A fresh id for the key of an event that can happen to the same thing more than once - a booking sent back for
    /// approval again, an order up again: an instant would collide under a clock that stands still, and a collision on
    /// the unique key would fail the save that caused the event.
    /// </summary>
    public static string Again() => Guid.CreateVersion7().ToString("N");

    private static void StageAll(YallaDbContext db, List<(StaffPushKinds Kind, Guid SubjectId, string Key)> events, DateTime nowUtc)
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
                JsonSerializer.Serialize(new StaffPushNotice(kind, subjectId, nowUtc), Json),
                nowUtc,
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
