using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Messages;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// Sends one <c>staff.push</c>: works out whether it is still true, who at the branch wants it, and pushes each of
/// their devices in its own language.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who.</b> The branch's live devices with a push subscription whose switches include the kind. A phone also needs
/// its person still able to sign in there (active, of the venue, at the branch or venue-wide, not kitchen) and - when
/// their "only during my shift" is on, the default for a waiter - on a shift at the branch now. A tablet has no person
/// and no shift; it is pushed whenever it is subscribed and wants the kind.
/// </para>
/// <para>
/// <b>An order up</b> goes to the table's own waiter when there is one - who seated the party, else who placed the
/// order - on their phones, and to the branch's tablets that want it. With no such person, every phone on shift that
/// wants it.
/// </para>
/// <para>
/// <b>Delivery.</b> A 404 or 410 from the push service deletes the subscription: the browser unsubscribed or the app
/// was removed, and no retry will ever reach it. Any other failure is logged; the message is retried only when nothing
/// at all was delivered, so a retry never buzzes a phone twice.
/// </para>
/// </remarks>
internal sealed class StaffPushHandler(
    YallaDbContext db,
    StaffPushSender sender,
    VapidKeys keys,
    IClock clock,
    ILogger<StaffPushHandler> logger) : IOutboxHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string MessageType => OutboxMessageTypes.StaffPush;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = StaffPushSignals.Read(payloadJson);
        var nowUtc = clock.UtcNow;

        // A push about something long past is noise: the floor has seen it, or it no longer matters.
        if (nowUtc - notice.OccurredAtUtc > TimeSpan.FromSeconds(Math.Max(keys.Options.TtlSeconds, 60)))
        {
            logger.LogInformation("Staff push {Kind} for {SubjectId} is too old to send; dropped.", notice.Kind, notice.SubjectId);
            return;
        }

        var subject = await StaffPushSubjects.ResolveAsync(db, notice, nowUtc, cancellationToken);

        if (subject is null)
        {
            // Answered, accepted, read or gone meanwhile. Nothing to say.
            return;
        }

        var targets = await AudienceAsync(subject, notice.Kind, nowUtc, cancellationToken);

        if (targets.Count == 0)
        {
            return;
        }

        var kind = KindName(notice.Kind);

        // All at once rather than one after another: ten unreachable phones in a row at the client's ten-second timeout
        // would outlast the outbox lease, and a second dispatcher taking the message over would buzz every phone the
        // first had already reached. Each send is its own outcome - one subscription that cannot be encrypted to, or a
        // push service that throws, must not abort the loop after others were delivered and so get them buzzed again
        // on the retry.
        var outcomes = await Task.WhenAll(targets.Select(async target =>
        {
            var (title, body) = subject.Render(target.Locale);

            var payload = JsonSerializer.Serialize(
                new StaffPushPayload(title, body, subject.Url, subject.Tag, kind), Json);

            try
            {
                return (target, Outcome: await sender.SendAsync(target.Push, payload, kind, subject.Tag, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The type only: the message could quote the payload or the endpoint.
                logger.LogWarning(
                    "Staff push {Kind} to device {DeviceId} failed before it was sent: {Error}.",
                    kind, target.Push.DeviceId, ex.GetType().Name);

                return (target, Outcome: StaffPushOutcome.Failed);
            }
        }));

        var (delivered, failed) = (0, 0);

        foreach (var (target, outcome) in outcomes)
        {
            switch (outcome)
            {
                case StaffPushOutcome.Delivered:
                    delivered++;
                    break;

                case StaffPushOutcome.Gone:
                    try
                    {
                        await db.StaffPushSubscriptions
                            .Where(s => s.Id == target.Push.SubscriptionId)
                            .ExecuteDeleteAsync(cancellationToken);

                        logger.LogInformation(
                            "Push subscription of device {DeviceId} is gone at its push service; deleted.", target.Push.DeviceId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Tidying, not delivery: the next push to it is answered 410 again and tries once more.
                        logger.LogWarning(
                            "Push subscription of device {DeviceId} is gone but could not be deleted: {Error}.",
                            target.Push.DeviceId, ex.GetType().Name);
                    }

                    break;

                default:
                    failed++;
                    break;
            }
        }

        // Retried only when nothing at all went: a retry after a partial delivery would buzz the phones that got it.
        if (delivered == 0 && failed > 0)
        {
            throw new InvalidOperationException(
                $"Staff push {kind} reached none of {failed} device(s); the push service refused or was unreachable.");
        }
    }

    /// <summary>The <c>kind</c> on the wire - the preference's own name.</summary>
    internal static string KindName(StaffPushKinds kind) => kind switch
    {
        StaffPushKinds.Approvals => "approvals",
        StaffPushKinds.GuestMessages => "guestMessages",
        StaffPushKinds.TableCalls => "tableCalls",
        StaffPushKinds.Takeaway => "takeaway",
        StaffPushKinds.KitchenReady => "kitchenReady",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A staff push is one kind."),
    };

    private async Task<List<Target>> AudienceAsync(
        StaffPushSubject subject,
        StaffPushKinds kind,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var candidates = await db.StaffPushSubscriptions
            .AsNoTracking()
            .Where(s => s.StaffDevice.BranchId == subject.BranchId && s.StaffDevice.RevokedAtUtc == null)
            .Select(s => new
            {
                Push = new StaffPushTarget(s.Id, s.StaffDeviceId, s.Endpoint, s.P256dh, s.Auth),
                s.StaffDevice.Kind,
                s.StaffDevice.VenueId,
                s.StaffDevice.PushKinds,
                s.StaffDevice.PushOnlyDuringShift,
                s.StaffDevice.Locale,
                Person = s.StaffDevice.BoundStaffMember == null
                    ? null
                    : new
                    {
                        s.StaffDevice.BoundStaffMember.Id,
                        s.StaffDevice.BoundStaffMember.IsActive,
                        s.StaffDevice.BoundStaffMember.VenueId,
                        s.StaffDevice.BoundStaffMember.BranchId,
                        s.StaffDevice.BoundStaffMember.Role,
                    },
            })
            .ToListAsync(cancellationToken);

        var wanting = candidates.Where(c => ((c.PushKinds ?? StaffPushKinds.Defaults) & kind) == kind).ToList();

        if (wanting.Count == 0)
        {
            return [];
        }

        HashSet<Guid>? onShift = null;
        var targets = new List<Target>();

        foreach (var device in wanting)
        {
            if (device.Kind == StaffDeviceKind.Tablet)
            {
                targets.Add(new Target(device.Push, device.Locale));
                continue;
            }

            // A phone: its person must still be somebody who opens it here.
            if (device.Person is not { IsActive: true } person
                || person.VenueId != device.VenueId
                || (person.BranchId is not null && person.BranchId != subject.BranchId)
                || person.Role == StaffRole.Kitchen)
            {
                continue;
            }

            // An order up for somebody's table goes to that somebody.
            if (subject.OwnerStaffMemberId is { } owner && person.Id != owner)
            {
                continue;
            }

            // "Only during my shift" - and an order up with no waiter of its own goes to whoever is on shift.
            var needsShift = (device.PushOnlyDuringShift ?? StaffDevice.OnlyDuringShiftByDefault(person.Role))
                             || (kind == StaffPushKinds.KitchenReady && subject.OwnerStaffMemberId is null);

            if (needsShift)
            {
                onShift ??= await ShiftInstants.OnShiftAsync(db, subject.BranchId, subject.TimeZoneId, nowUtc, cancellationToken);

                if (!onShift.Contains(person.Id))
                {
                    continue;
                }
            }

            targets.Add(new Target(device.Push, device.Locale));
        }

        return targets;
    }

    private sealed record Target(StaffPushTarget Push, string? Locale);
}

/// <summary>
/// What the service worker receives - the whole of it. <c>url</c> is a path in the staff app to open on a tap,
/// <c>tag</c> the collapse key (a newer push with the same tag replaces the older on the lock screen), <c>kind</c> the
/// switch it came under.
/// </summary>
internal sealed record StaffPushPayload(string Title, string Body, string Url, string Tag, string Kind);

/// <summary>What a staff push is about, read at send time: still true, where, and in words.</summary>
/// <param name="BranchId">Whose devices are told.</param>
/// <param name="TimeZoneId">The branch's zone, for "on shift now".</param>
/// <param name="Url">The deep link into the staff app.</param>
/// <param name="Tag">The collapse key.</param>
/// <param name="OwnerStaffMemberId">For an order up: the table's waiter, when there is one.</param>
/// <param name="Render">The title and body in a device's language.</param>
internal sealed record StaffPushSubject(
    Guid BranchId,
    string TimeZoneId,
    string Url,
    string Tag,
    Guid? OwnerStaffMemberId,
    Func<string?, (string Title, string Body)> Render);

/// <summary>The five kinds of staff push, each read fresh - and dropped when no longer true.</summary>
internal static class StaffPushSubjects
{
    public static Task<StaffPushSubject?> ResolveAsync(
        YallaDbContext db,
        StaffPushNotice notice,
        DateTime nowUtc,
        CancellationToken cancellationToken) => notice.Kind switch
    {
        StaffPushKinds.Approvals => ApprovalAsync(db, notice.SubjectId, nowUtc, cancellationToken),
        StaffPushKinds.GuestMessages => GuestMessageAsync(db, notice.SubjectId, cancellationToken),
        StaffPushKinds.TableCalls => TableCallAsync(db, notice.SubjectId, cancellationToken),
        StaffPushKinds.Takeaway => TakeawayAsync(db, notice.SubjectId, cancellationToken),
        StaffPushKinds.KitchenReady => KitchenReadyAsync(db, notice.SubjectId, cancellationToken),
        _ => Task.FromResult<StaffPushSubject?>(null),
    };

    /// <summary>
    /// A booking the floor can still answer - the awaiting list's own rule: waiting, not started, inside its answer
    /// window, and not waiting on the guest before it to answer in the app.
    /// </summary>
    private static async Task<StaffPushSubject?> ApprovalAsync(
        YallaDbContext db, Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId
                        && r.Status == ReservationStatus.PendingApproval
                        && r.StartUtc > nowUtc
                        && (r.ApprovalExpiresAtUtc == null || r.ApprovalExpiresAtUtc > nowUtc)
                        && !db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                           && q.Status == TableShareRequestStatus.Pending
                                                           && q.AsksEarlierGuest))
            .Select(r => new
            {
                r.BranchId,
                r.Branch.TimeZoneId,
                Table = r.DiningTable.Label,
                r.LocalDate,
                r.LocalStartTime,
                r.PartySize,
                r.GuestName,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return booking is null
            ? null
            : new StaffPushSubject(
                booking.BranchId,
                booking.TimeZoneId,
                $"/staff/approve/{reservationId}",
                $"approval:{reservationId}",
                null,
                locale => StaffPushText.Approval(
                    locale ?? string.Empty, booking.Table, booking.LocalDate, booking.LocalStartTime, booking.PartySize, booking.GuestName));
    }

    /// <summary>A guest's words nobody at the venue has opened the thread on since.</summary>
    private static async Task<StaffPushSubject?> GuestMessageAsync(
        YallaDbContext db, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Join(db.Conversations, m => m.ConversationId, c => c.Id, (m, c) => new
            {
                m.ConversationId,
                m.Kind,
                m.Body,
                m.AnswerKind,
                m.LateMinutes,
                m.CreatedAtUtc,
                c.BranchId,
                c.ReservationId,
                c.TakeawayOrderId,
                c.VenueLastReadUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (message is null || (message.VenueLastReadUtc is { } read && read >= message.CreatedAtUtc))
        {
            return null;
        }

        var timeZoneId = await db.Branches
            .Where(b => b.Id == message.BranchId)
            .Select(b => b.TimeZoneId)
            .FirstAsync(cancellationToken);

        Func<string?, string> subject;

        if (message.ReservationId is { } reservationId)
        {
            var booking = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => new { r.GuestName, r.LocalStartTime, Table = r.DiningTable.Label })
                .FirstOrDefaultAsync(cancellationToken);

            if (booking is null)
            {
                return null;
            }

            subject = locale => StaffPushText.BookingSubject(locale ?? string.Empty, booking.Table, booking.GuestName, booking.LocalStartTime);
        }
        else
        {
            var order = await db.TakeawayOrders
                .AsNoTracking()
                .Where(o => o.Id == message.TakeawayOrderId)
                .Select(o => new { o.Code, o.GuestName })
                .FirstOrDefaultAsync(cancellationToken);

            if (order is null)
            {
                return null;
            }

            subject = _ => $"{order.Code} · {order.GuestName}";
        }

        return new StaffPushSubject(
            message.BranchId,
            timeZoneId,
            $"/staff/messages/{message.ConversationId}",
            $"message:{message.ConversationId}",
            null,
            locale => StaffPushText.GuestMessage(
                locale ?? string.Empty,
                subject(locale),
                message.Kind == MessageKind.Answer
                    ? StaffPushText.Answer(locale ?? string.Empty, (int)(message.AnswerKind ?? GuestAnswer.Coming), message.LateMinutes)
                    : message.Body ?? string.Empty));
    }

    /// <summary>A table's call nobody has said "I'm on it" to, and no newer one replaced.</summary>
    /// <remarks>
    /// "I'm on it" is the claim: a call somebody claimed in the seconds before this send buzzes nobody, though it stays
    /// open - on the floor's list, showing who has it - until it is acknowledged. The push asks for somebody to go;
    /// once somebody is going, the question is answered. Acknowledging claims a call nobody claimed, so an
    /// acknowledged call is claimed too. See <c>docs/notifications.md</c>.
    /// </remarks>
    private static async Task<StaffPushSubject?> TableCallAsync(
        YallaDbContext db, Guid requestId, CancellationToken cancellationToken)
    {
        var call = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.Id == requestId
                        && r.ClaimedAtUtc == null
                        && r.AcknowledgedAtUtc == null
                        && r.SupersededAtUtc == null)
            .Select(r => new
            {
                r.BranchId,
                r.Branch.TimeZoneId,
                r.DiningTableId,
                Table = r.DiningTable.Label,
                r.Preset,
                r.Note,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return call is null
            ? null
            : new StaffPushSubject(
                call.BranchId,
                call.TimeZoneId,
                $"/staff/now?call={requestId}",
                $"call:{call.DiningTableId}",
                null,
                locale => StaffPushText.TableCall(locale ?? string.Empty, call.Table, (int)call.Preset, call.Note));
    }

    /// <summary>A takeaway order still waiting for somebody to accept it.</summary>
    private static async Task<StaffPushSubject?> TakeawayAsync(
        YallaDbContext db, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId && o.Status == TakeawayStatus.Sent)
            .Select(o => new
            {
                o.BranchId,
                TimeZoneId = db.Branches.Where(b => b.Id == o.BranchId).Select(b => b.TimeZoneId).First(),
                o.Code,
                o.GuestName,
                o.PickupAsap,
                o.RequestedPickupLocalTime,
                Items = db.TakeawayOrderLines.Where(l => l.TakeawayOrderId == o.Id).Sum(l => (int?)l.Quantity) ?? 0,
                TotalAmd = db.TakeawayOrderLines.Where(l => l.TakeawayOrderId == o.Id).Sum(l => (long?)(l.UnitPriceAmdSnapshot * l.Quantity)) ?? 0,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return order is null
            ? null
            : new StaffPushSubject(
                order.BranchId,
                order.TimeZoneId,
                $"/staff/takeaway/{orderId}",
                $"takeaway:{orderId}",
                null,
                locale => StaffPushText.Takeaway(
                    locale ?? string.Empty,
                    order.Code,
                    order.GuestName,
                    order.Items,
                    order.TotalAmd,
                    order.PickupAsap ? null : order.RequestedPickupLocalTime));
    }

    /// <summary>An order still up - not yet served or voided - and whose table it is.</summary>
    private static async Task<StaffPushSubject?> KitchenReadyAsync(
        YallaDbContext db, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.TabOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId && o.Status == TabOrderStatus.Ready)
            .Select(o => new
            {
                o.Tab.BranchId,
                o.Tab.Branch.TimeZoneId,
                TableId = o.Tab.DiningTableId,
                Table = o.Tab.DiningTable.Label,
                SeatedBy = o.Tab.TableSession.SeatedByStaffId,
                o.PlacedByStaffId,
                Dishes = o.Lines.Sum(l => (int?)l.Quantity) ?? 0,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return order is null
            ? null
            : new StaffPushSubject(
                order.BranchId,
                order.TimeZoneId,
                $"/staff/floor?table={order.TableId}",
                $"ready:{orderId}",
                order.SeatedBy ?? order.PlacedByStaffId,
                locale => StaffPushText.KitchenReady(locale ?? string.Empty, order.Table, order.Dishes));
    }
}
