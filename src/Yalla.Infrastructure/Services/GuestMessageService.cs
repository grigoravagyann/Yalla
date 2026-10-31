using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Messages;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Guest messages: a booking's or a takeaway order's thread as the guest and the venue read and write it, the tablet's
/// inbox, and the console's settings. See <c>docs/messages.md</c>.
/// </summary>
internal sealed class GuestMessageService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAuthorizationQueries authorization,
    IStaffBranchGuard branchGuard,
    IActivityLog activity,
    ILogger<GuestMessageService> logger) : IGuestMessageService
{
    // ---------------------------------------------------------------- the guest

    public async Task<DinerConversationView> GetForGuestAsync(
        ConversationSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var dinerUserId = RequireDiner("Read a conversation");
        var nowUtc = clock.UtcNow;
        var facts = await GuestSubjectAsync(subject, dinerUserId, nowUtc, cancellationToken);
        var (thread, messages) = await ThreadAsync(subject, cancellationToken);

        return new DinerConversationView(
            thread?.Id,
            facts.View(),
            facts.IsOpenAt(nowUtc),
            facts.ClosesAtUtc,
            facts.GuestsCanWrite,
            facts.BranchPhone,
            PendingAsk(facts, messages, nowUtc),
            thread?.VenueLastReadUtc,
            messages.Count(m => m.Author == MessageAuthor.Venue && After(m, thread!.GuestLastReadUtc)),
            [.. messages.Select(DinerMessage)]);
    }

    /// <summary>
    /// The guest writes. In this order, each refusal before anything is written: the text (422 <c>validation-failed</c>
    /// naming <c>text</c>); their own booking or order (404); a replay (answered as the message stands); the platform
    /// allowing guest messages (409 <c>feature-not-enabled</c>, feature <c>GuestMessages</c>); the thread open
    /// (409 <c>conversation-closed</c>); the venue taking typed messages (403 <c>guests-cannot-write</c>); the pace (429
    /// <c>messages-too-fast</c>).
    /// </summary>
    /// <remarks>
    /// Under the account's lock and then the subject's, as every guest write is: a deletion that comes after waits, a
    /// retry racing its own first try finds the message the first made, and two first messages make one thread. The
    /// floor's <c>guestMessages</c> push hangs on the message's <see cref="GuestMessageWritten"/> event.
    /// </remarks>
    public async Task<DinerMessageResult> WriteAsGuestAsync(
        ConversationSubject subject, string? text, Guid clientCommandId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var dinerUserId = RequireDiner("Write to the venue");
        var body = ConversationRules.RequireText(text);
        var facts = await GuestSubjectAsync(subject, dinerUserId, clock.UtcNow, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);
        await GuestThreads.LockAsync(db, subject, cancellationToken);

        var existing = await GuestThreads.FindAsync(db, subject, cancellationToken);

        if (existing is not null
            && await ReplayAsync(existing.Id, MessageAuthor.Guest, null, body, clientCommandId, cancellationToken) is { } replay)
        {
            return GuestResult(replay, wasReplay: true);
        }

        var nowUtc = clock.UtcNow;

        // The platform's switch first (docs/branch-features.md): the thread stays readable, nobody writes in it.
        RequireMessagesAllowed(facts);
        RequireOpen(facts, nowUtc);

        if (!facts.GuestsCanWrite)
        {
            throw new GuestsCannotWriteException(facts.BranchId);
        }

        if (existing is not null)
        {
            var sent = await db.ConversationMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == existing.Id && m.Author == MessageAuthor.Guest && m.Kind == MessageKind.Text)
                .Select(m => m.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var next = ConversationRules.NextGuestMessageAtUtc(sent, nowUtc);

            if (next is null || next > nowUtc)
            {
                throw new MessagesTooFastException(existing.Id, next, nowUtc);
            }
        }

        var thread = existing
                     ?? await GuestThreads.FindOrStartAsync(db, subject, facts.BranchId, nowUtc, cancellationToken);
        var message = thread.WriteAsGuest(body, clientCommandId, nowUtc);
        db.ConversationMessages.Add(message);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Guest message {MessageId} in thread {ConversationId} ({Kind} {SubjectId}).",
            message.Id, thread.Id, subject.Kind, subject.Id);

        return GuestResult(message, wasReplay: false);
    }

    public async Task MarkReadByGuestAsync(
        ConversationSubject subject, DateTime? upToUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var dinerUserId = RequireDiner("Read a conversation");
        var nowUtc = clock.UtcNow;
        await GuestSubjectAsync(subject, dinerUserId, nowUtc, cancellationToken);

        var mark = ConversationRules.ReadMarkAt(nowUtc, upToUtc is { } upTo ? AsUtc(upTo) : null);

        // One conditional statement, so the mark only ever moves forward - two phones marking at once included.
        await ThreadOf(subject)
            .Where(c => c.GuestLastReadUtc == null || c.GuestLastReadUtc < mark)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.GuestLastReadUtc, (DateTime?)mark), cancellationToken);
    }

    public async Task<DinerUnreadMessages> GetUnreadForGuestAsync(CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Read my messages");
        var nowUtc = clock.UtcNow;

        var lines = await GuestThreads.LinesAsync(db.Conversations.Where(c => c.DinerUserId == dinerUserId), cancellationToken);

        var bookings = await GuestThreads.ReservationSubjectsAsync(
            db, [.. lines.Where(l => l.ReservationId is not null).Select(l => l.ReservationId!.Value)], cancellationToken);
        var orders = await GuestThreads.TakeawaySubjectsAsync(
            db, [.. lines.Where(l => l.TakeawayOrderId is not null).Select(l => l.TakeawayOrderId!.Value)], cancellationToken);

        var items = new List<DinerUnreadThread>();

        foreach (var line in lines.OrderByDescending(l => l.LastMessageUtc))
        {
            var facts = line.ReservationId is { } reservationId
                ? bookings.GetValueOrDefault(reservationId)
                : orders.GetValueOrDefault(line.TakeawayOrderId!.Value);

            if (facts is null || line.LastAuthor is null || !ConversationRules.GuestMayRead(facts.ClosesAtUtc, nowUtc))
            {
                continue;
            }

            var open = facts.IsOpenAt(nowUtc);

            if (!open && line.UnreadForGuest == 0)
            {
                continue;
            }

            items.Add(new DinerUnreadThread(
                line.ConversationId,
                GuestThreads.SubjectKindName(facts.Kind),
                facts.Id,
                facts.VenueName,
                line.UnreadForGuest,
                line.LastBody is { } body ? ConversationRules.Preview(body) : null,
                GuestThreads.KindName(line.LastKind!.Value),
                GuestThreads.AuthorName(line.LastAuthor.Value),
                line.LastMessageUtc,
                open));
        }

        return new DinerUnreadMessages(items.Sum(i => i.Unread), items);
    }

    // ---------------------------------------------------------------- the venue

    public async Task<BranchConversations> ListForBranchAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, branchId, "Read the guest messages", cancellationToken);

        var settings = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.MessageRemindAfterMinutes, b.MessageTemplatesJson, b.WaitersCanMessage, b.AllowedFeatureBits })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;

        var rows = await GuestThreads.RelevantAt(db, branchId, nowUtc)
            .AsNoTracking()
            .Select(c => new InboxRow
            {
                ConversationId = c.Id,
                ReservationId = c.ReservationId,
                TakeawayOrderId = c.TakeawayOrderId,
                LastMessageUtc = c.LastMessageUtc,
                UnreadFromGuest = c.Messages.Count(m => m.Author == MessageAuthor.Guest
                                                        && (c.VenueLastReadUtc == null || m.CreatedAtUtc > c.VenueLastReadUtc)),
                Last = c.Messages
                    .Where(m => m.Kind != MessageKind.Status)
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Sequence)
                    .FirstOrDefault(),
                LatestStatus = c.Messages
                    .Where(m => m.Kind == MessageKind.Status)
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Sequence)
                    .FirstOrDefault(),
                // Only the guest's words wait for a reply: an answer to "are you coming?" asks nothing back. It is
                // still unread above until somebody at the venue opens the thread.
                OldestUnansweredUtc = c.Messages
                    .Where(m => m.Author == MessageAuthor.Guest
                                && m.Kind == MessageKind.Text
                                && !c.Messages.Any(v => v.Author == MessageAuthor.Venue
                                                        && (v.CreatedAtUtc > m.CreatedAtUtc
                                                            || (v.CreatedAtUtc == m.CreatedAtUtc && v.Sequence > m.Sequence))))
                    .Min(m => (DateTime?)m.CreatedAtUtc),
            })
            .ToListAsync(cancellationToken);

        var bookings = await GuestThreads.ReservationSubjectsAsync(
            db, [.. rows.Where(r => r.ReservationId is not null).Select(r => r.ReservationId!.Value)], cancellationToken);
        var orders = await GuestThreads.TakeawaySubjectsAsync(
            db, [.. rows.Where(r => r.TakeawayOrderId is not null).Select(r => r.TakeawayOrderId!.Value)], cancellationToken);

        var items = new List<InboxItem>();

        foreach (var row in rows)
        {
            var facts = row.ReservationId is { } reservationId
                ? bookings.GetValueOrDefault(reservationId)
                : orders.GetValueOrDefault(row.TakeawayOrderId!.Value);

            if (facts is null || row.Last is null)
            {
                continue;
            }

            var open = facts.IsOpenAt(nowUtc);

            // The query's window is a superset - it cannot see a completed booking's handover time; this is the rule.
            if (!open && row.LastMessageUtc < nowUtc.AddHours(-ConversationRules.InboxRecentHours))
            {
                continue;
            }

            items.Add(new InboxItem(
                row.ConversationId,
                InboxSubjectOf(facts),
                StaffMessage(row.Last),
                row.LatestStatus is { } status ? StatusOf(status) : null,
                row.UnreadFromGuest,
                row.Last is { Author: MessageAuthor.Venue, Kind: MessageKind.AskComing },

                // A closed thread cannot be answered: nothing in it waits, and nothing rings.
                open ? row.OldestUnansweredUtc : null,
                open));
        }

        // A guest waiting for an answer first - the longest waiting first - then the rest, newest first.
        var ordered = items
            .OrderBy(i => i.OldestUnansweredGuestMessageUtc is null)
            .ThenBy(i => i.OldestUnansweredGuestMessageUtc)
            .ThenByDescending(i => i.LastMessage.CreatedAtUtc)
            .ToList();

        return new BranchConversations(
            ordered,
            ordered.Sum(i => i.UnreadFromGuest),
            settings.MessageRemindAfterMinutes,
            Domain.Venues.Branch.TemplatesFrom(settings.MessageTemplatesJson),

            // The effective answer (docs/branch-features.md): the venue's setting inside what the platform allows.
            settings.WaitersCanMessage && BranchFeatures.Has(settings.AllowedFeatureBits, BranchFeature.GuestMessages),
            nowUtc);
    }

    /// <summary>
    /// The console's Messages page. In this order: manager or above at the branch, from the stored row (403); the branch
    /// (404); the query (422, every bad field named). One statement for the page - the branch's threads by
    /// <c>IX_Conversations_BranchId_LastMessageUtc</c>, the window and the cursor its range - then one per kind of subject
    /// and one for the staff names, whatever the page holds. Reads only: no read mark moves.
    /// </summary>
    public async Task<MessageHistoryPage> ListHistoryAsync(
        Guid branchId, MessageHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await branchGuard.RequireAtBranchAsync(branchId, "Read the message history", cancellationToken);

        var timeZoneId = await db.Branches
                             .AsNoTracking()
                             .Where(b => b.Id == branchId)
                             .Select(b => b.TimeZoneId)
                             .FirstOrDefaultAsync(cancellationToken)
                         ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;
        var zone = BranchZone.For(timeZoneId);
        var filter = MessageHistoryRules.Check(query, zone.LocalDateAt(nowUtc));

        // The branch's own days: from its midnight to the midnight after the last day, exclusive.
        var fromUtc = zone.ToUtcLenient(filter.From, TimeOnly.MinValue);
        var toUtcExclusive = zone.ToUtcLenient(filter.To.AddDays(1), TimeOnly.MinValue);

        var threads = db.Conversations
            .AsNoTracking()
            .Where(c => c.BranchId == branchId && c.LastMessageUtc >= fromUtc && c.LastMessageUtc < toUtcExclusive);

        threads = filter.Kind switch
        {
            MessageHistoryKind.Reservation => threads.Where(c => c.ReservationId != null),
            MessageHistoryKind.Takeaway => threads.Where(c => c.TakeawayOrderId != null),
            _ => threads,
        };

        if (filter.Search is { } search)
        {
            // Lowered on both sides rather than trusting the column's collation to ignore case - Armenian and
            // Cyrillic names included. The page is already one branch's window, so nothing here needs an index.
            var lowered = search.ToLowerInvariant();
            var code = filter.SearchAsTakeawayCode;

            threads = threads.Where(c =>
                (c.ReservationId != null
                 && db.Reservations.Any(r => r.Id == c.ReservationId
                                             && (r.GuestName.ToLower().Contains(lowered)
                                                 || r.DiningTable!.Label.ToLower() == lowered)))
                || (c.TakeawayOrderId != null
                    && db.TakeawayOrders.Any(o => o.Id == c.TakeawayOrderId
                                                  && (o.GuestName.ToLower().Contains(lowered) || o.Code == code))));
        }

        if (filter.UnansweredOnly)
        {
            threads = threads.Where(c => c.Messages.Any(m => m.Author == MessageAuthor.Guest
                                                             && m.Kind == MessageKind.Text
                                                             && !c.Messages.Any(v => v.Author == MessageAuthor.Venue
                                                                                     && (v.CreatedAtUtc > m.CreatedAtUtc
                                                                                         || (v.CreatedAtUtc == m.CreatedAtUtc
                                                                                             && v.Sequence > m.Sequence)))));
        }

        if (filter.Cursor is { } cursor)
        {
            var atUtc = cursor.LastMessageUtc;
            var afterId = cursor.ConversationId;

            // Strictly after the last row shown, in the list's own order - the id breaks a tie of instants, compared as
            // the database orders it.
            threads = threads.Where(c => c.LastMessageUtc < atUtc
                                         || (c.LastMessageUtc == atUtc && c.Id.CompareTo(afterId) < 0));
        }

        var rows = await threads
            .OrderByDescending(c => c.LastMessageUtc)
            .ThenByDescending(c => c.Id)
            .Take(filter.Limit + 1)
            .Select(c => new HistoryRow
            {
                ConversationId = c.Id,
                ReservationId = c.ReservationId,
                TakeawayOrderId = c.TakeawayOrderId,
                LastMessageUtc = c.LastMessageUtc,
                CreatedAtUtc = c.CreatedAtUtc,
                MessageCount = c.Messages.Count(m => m.Author != MessageAuthor.System),
                FirstAtUtc = c.Messages.Min(m => (DateTime?)m.CreatedAtUtc),
                Unanswered = c.Messages.Any(m => m.Author == MessageAuthor.Guest
                                                 && m.Kind == MessageKind.Text
                                                 && !c.Messages.Any(v => v.Author == MessageAuthor.Venue
                                                                         && (v.CreatedAtUtc > m.CreatedAtUtc
                                                                             || (v.CreatedAtUtc == m.CreatedAtUtc
                                                                                 && v.Sequence > m.Sequence)))),
            })
            .ToListAsync(cancellationToken);

        var shown = rows.Take(filter.Limit).ToList();

        var bookings = await GuestThreads.ReservationSubjectsAsync(
            db, [.. shown.Where(r => r.ReservationId is not null).Select(r => r.ReservationId!.Value)], cancellationToken);
        var orders = await GuestThreads.TakeawaySubjectsAsync(
            db, [.. shown.Where(r => r.TakeawayOrderId is not null).Select(r => r.TakeawayOrderId!.Value)], cancellationToken);
        var staffNames = await StaffNamesAsync([.. shown.Select(r => r.ConversationId)], cancellationToken);

        var items = new List<MessageHistoryItem>(shown.Count);

        foreach (var row in shown)
        {
            var facts = row.ReservationId is { } reservationId
                ? bookings.GetValueOrDefault(reservationId)
                : orders.GetValueOrDefault(row.TakeawayOrderId!.Value);

            // A thread's subject is never deleted while the thread stands (the foreign keys restrict it).
            if (facts is null)
            {
                continue;
            }

            items.Add(new MessageHistoryItem(
                row.ConversationId,
                HistorySubjectOf(facts, zone),
                row.MessageCount,
                row.FirstAtUtc ?? row.CreatedAtUtc,
                row.LastMessageUtc,
                zone.ToLocal(row.LastMessageUtc).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                staffNames.GetValueOrDefault(row.ConversationId) ?? [],
                row.Unanswered,
                facts.IsOpenAt(nowUtc)));
        }

        var next = rows.Count > filter.Limit
            ? new MessageHistoryCursor(shown[^1].LastMessageUtc, shown[^1].ConversationId).Encode()
            : null;

        return new MessageHistoryPage(items, next, filter.From, filter.To);
    }

    /// <summary>
    /// Who at the venue wrote in each thread, as they were named then - each name once, in the order they first wrote. One
    /// statement for the page.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> StaffNamesAsync(
        List<Guid> conversationIds, CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return [];
        }

        var firsts = await db.ConversationMessages
            .AsNoTracking()
            .Where(m => conversationIds.Contains(m.ConversationId)
                        && m.Author == MessageAuthor.Venue
                        && m.StaffName != null
                        && m.StaffName != string.Empty)
            .GroupBy(m => new { m.ConversationId, m.StaffName })
            .Select(g => new
            {
                g.Key.ConversationId,
                g.Key.StaffName,
                FirstAtUtc = g.Min(m => m.CreatedAtUtc),
                FirstSequence = g.Min(m => m.Sequence),
            })
            .ToListAsync(cancellationToken);

        return firsts
            .GroupBy(f => f.ConversationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.OrderBy(f => f.FirstAtUtc).ThenBy(f => f.FirstSequence).Select(f => f.StaffName!)]);
    }

    private static MessageHistorySubject HistorySubjectOf(GuestThreads.Subject facts, BranchZone zone)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        if (facts.Kind == ConversationSubjectKind.Reservation)
        {
            return new MessageHistorySubject(
                ConversationRules.SubjectKinds.Reservation,
                facts.Id,
                facts.TableLabel ?? string.Empty,
                facts.GuestName,
                facts.LocalDate!.Value.ToString("yyyy-MM-dd", invariant),
                GuestThreads.Hhmm(facts.LocalStartTime!.Value),
                facts.PartySize,
                null,
                null,
                facts.Outcome);
        }

        // An order's day: the day it was ready, or asked for, on the branch's clock.
        var readyDate = facts.ReadyAtUtc is { } readyAt ? zone.LocalDateAt(readyAt) : (DateOnly?)null;

        return new MessageHistorySubject(
            ConversationRules.SubjectKinds.Takeaway,
            facts.Id,
            facts.TakeawayCode ?? string.Empty,
            facts.GuestName,
            readyDate?.ToString("yyyy-MM-dd", invariant) ?? string.Empty,
            facts.ReadyAtLocal is { } ready ? GuestThreads.Hhmm(ready) : string.Empty,
            null,
            facts.ItemCount,
            facts.TotalAmd,
            facts.Outcome);
    }

    public async Task<StaffConversationView> GetForStaffAsync(
        ConversationSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var facts = await AnySubjectAsync(subject, cancellationToken);
        var staffId = await FloorStaffGuard.RequireAtBranchAsync(
            db, actor, authorization, facts.BranchId, "Read a guest conversation", cancellationToken);

        var role = await StoredRoleAsync(staffId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var (thread, messages) = await ThreadAsync(subject, cancellationToken);
        var open = facts.IsOpenAt(nowUtc);

        return new StaffConversationView(
            thread?.Id,
            facts.View(withOutcome: true),
            facts.GuestName,

            // The guests' numbers are the venue's to call, not the platform's to read.
            role == StaffRole.PlatformAdmin ? string.Empty : facts.GuestPhone,
            open,
            facts.ClosesAtUtc,
            facts.GuestsCanWrite,
            open && MayWrite(role, facts),
            facts.BranchPhone,
            PendingAsk(facts, messages, nowUtc),
            thread?.VenueLastReadUtc,
            thread?.GuestLastReadUtc,
            messages.Count(m => m.Author == MessageAuthor.Guest && After(m, thread!.VenueLastReadUtc)),
            [.. messages.Select(StaffMessage)]);
    }

    /// <summary>
    /// Somebody at the venue writes, and the guest is told (push <c>message.to-guest</c>, feed <c>venue-message</c>) and the
    /// History says who answered - by the message's <see cref="VenueMessageWritten"/> handlers, in this save. In this
    /// order: the text (422); the subject (404); floor staff at its branch (403); a platform admin reads only and the
    /// kitchen never writes (403 <c>forbidden</c>); the platform allowing guest messages (409 <c>feature-not-enabled</c>,
    /// feature <c>GuestMessages</c>); a waiter where the venue keeps messages to managers (403
    /// <c>waiters-cannot-write</c>); a replay (answered as the message stands, nobody told twice); the thread open (409
    /// <c>conversation-closed</c>).
    /// </summary>
    public async Task<StaffMessageResult> WriteAsStaffAsync(
        ConversationSubject subject, string? text, Guid clientCommandId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        const string Operation = "Write to a guest";

        var body = ConversationRules.RequireText(text);
        var facts = await AnySubjectAsync(subject, cancellationToken);
        var staffId = await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, facts.BranchId, Operation, cancellationToken);
        var role = await StoredRoleAsync(staffId, cancellationToken);

        if (role is StaffRole.PlatformAdmin or StaffRole.Kitchen)
        {
            throw new StaffPermissionException(Operation, role, StaffRole.Waiter);
        }

        // The platform's switch before the venue's own (docs/branch-features.md): managers included.
        RequireMessagesAllowed(facts);

        if (role == StaffRole.Waiter && !facts.WaitersCanWrite)
        {
            throw new WaitersCannotWriteException(facts.BranchId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The account's lock before the subject's - the order every guest write takes them in.
        if (facts.DinerUserId is { } account)
        {
            await DinerAccountLock.IsLiveAsync(db, account, cancellationToken);
        }

        await GuestThreads.LockAsync(db, subject, cancellationToken);

        var existing = await GuestThreads.FindAsync(db, subject, cancellationToken);

        if (existing is not null
            && await ReplayAsync(existing.Id, MessageAuthor.Venue, staffId, body, clientCommandId, cancellationToken) is { } replay)
        {
            return StaffResult(replay, wasReplay: true);
        }

        var nowUtc = clock.UtcNow;
        RequireOpen(facts, nowUtc);

        // A new thread takes the subject's account as it stands now, under the lock - not facts.DinerUserId, read before
        // the locks: an account deleted in between has had its link cut, and must not be linked again (FindOrStartAsync).
        var staffName = await GuestThreads.StaffNameAsync(db, staffId, cancellationToken);
        var thread = existing
                     ?? await GuestThreads.FindOrStartAsync(db, subject, facts.BranchId, nowUtc, cancellationToken);

        // The History's line (with how long the guest waited) and the guest told hang on the message's event.
        var message = thread.WriteAsVenue(body, staffId, staffName, clientCommandId, nowUtc);
        db.ConversationMessages.Add(message);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Venue message {MessageId} in thread {ConversationId} by staff {StaffId}.", message.Id, thread.Id, staffId);

        return StaffResult(message, wasReplay: false);
    }

    /// <summary>
    /// Any member of staff reading counts for the venue - except a platform admin, who only looks. Up to
    /// <paramref name="upToUtc"/> when the tablet says what it showed, never past now, never backwards.
    /// </summary>
    public async Task MarkReadByVenueAsync(
        ConversationSubject subject, DateTime? upToUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var facts = await AnySubjectAsync(subject, cancellationToken);
        var staffId = await FloorStaffGuard.RequireAtBranchAsync(
            db, actor, authorization, facts.BranchId, "Read a guest conversation", cancellationToken);

        if (await StoredRoleAsync(staffId, cancellationToken) == StaffRole.PlatformAdmin)
        {
            return;
        }

        var mark = ConversationRules.ReadMarkAt(clock.UtcNow, upToUtc is { } upTo ? AsUtc(upTo) : null);

        // One conditional statement, so the mark only ever moves forward - two tablets marking at once included.
        await ThreadOf(subject)
            .Where(c => c.VenueLastReadUtc == null || c.VenueLastReadUtc < mark)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.VenueLastReadUtc, (DateTime?)mark), cancellationToken);
    }

    /// <summary>Refuses a new message at a branch the platform has not allowed guest messages at.</summary>
    /// <exception cref="FeatureNotEnabledException">409 <c>feature-not-enabled</c>, feature <c>GuestMessages</c>.</exception>
    private static void RequireMessagesAllowed(GuestThreads.Subject facts)
    {
        if (!facts.MessagesAllowed)
        {
            throw new FeatureNotEnabledException(BranchFeature.GuestMessages, facts.BranchId);
        }
    }

    /// <summary>The subject's thread, as a query - none while nobody has written.</summary>
    private IQueryable<Conversation> ThreadOf(ConversationSubject subject) =>
        subject.Kind == ConversationSubjectKind.Reservation
            ? db.Conversations.Where(c => c.ReservationId == subject.Id)
            : db.Conversations.Where(c => c.TakeawayOrderId == subject.Id);

    /// <summary>An instant off the wire: one with an offset is converted, one without is taken as UTC.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the console

    public async Task<MessageSettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the message settings", cancellationToken);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    public async Task<MessageSettingsView> SaveSettingsAsync(
        Guid branchId, MessageSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await branchGuard.RequireAtBranchAsync(branchId, "Change the message settings", cancellationToken);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var before = new
        {
            guestsCanWrite = branch.GuestsCanMessage,
            waitersCanWrite = branch.WaitersCanMessage,
            remindAfterMinutes = branch.MessageRemindAfterMinutes,
            templates = Domain.Venues.Branch.TemplatesFrom(branch.MessageTemplatesJson),
        };

        branch.SetMessages(settings.GuestsCanWrite, settings.WaitersCanWrite, settings.Templates, settings.RemindAfterMinutes);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Settings, ActivityActions.SettingsMessages, ActivitySubjects.Branch, branchId)
            {
                BranchId = branchId,
                VenueId = branch.VenueId,
                Before = before,
                After = new
                {
                    guestsCanWrite = branch.GuestsCanMessage,
                    waitersCanWrite = branch.WaitersCanMessage,
                    remindAfterMinutes = branch.MessageRemindAfterMinutes,
                    templates = Domain.Venues.Branch.TemplatesFrom(branch.MessageTemplatesJson),
                },
                Summary = new { settings.GuestsCanWrite, settings.WaitersCanWrite, settings.RemindAfterMinutes },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} messages: guests {Guests}, waiters {Waiters}, {Templates} quick replies, remind after {Remind} min.",
            branchId,
            settings.GuestsCanWrite ? "write" : "answer only",
            settings.WaitersCanWrite ? "write" : "read only",
            settings.Templates?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default",
            settings.RemindAfterMinutes);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    private async Task<MessageSettingsView> SettingsViewAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.GuestsCanMessage, b.WaitersCanMessage, b.MessageTemplatesJson, b.MessageRemindAfterMinutes })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        return new MessageSettingsView(
            branch.GuestsCanMessage,
            branch.WaitersCanMessage,
            Domain.Venues.Branch.TemplatesFrom(branch.MessageTemplatesJson),
            branch.MessageRemindAfterMinutes,
            Bounds);
    }

    internal static readonly MessageSettingsBounds Bounds = new(
        ConversationRules.MaxTemplates,
        ConversationRules.MaxTemplateLength,
        ConversationRules.MinRemindAfterMinutes,
        ConversationRules.MaxRemindAfterMinutes,
        ConversationRules.MaxTextLength);

    // ---------------------------------------------------------------- who, and what

    private Guid RequireDiner(string operation) =>
        actor.Type == ActorType.Diner && actor.DinerUserId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException($"{operation} requires a diner account.");

    /// <summary>
    /// The caller's own booking or order, while its thread may still be read: anybody else's - or one closed over 30 days
    /// ago - is not found, word for word as no subject at all.
    /// </summary>
    private async Task<GuestThreads.Subject> GuestSubjectAsync(
        ConversationSubject subject, Guid dinerUserId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var facts = await GuestThreads.SubjectAsync(db, subject, cancellationToken);

        return facts is not null && facts.DinerUserId == dinerUserId && ConversationRules.GuestMayRead(facts.ClosesAtUtc, nowUtc)
            ? facts
            : throw NotFound(subject);
    }

    private async Task<GuestThreads.Subject> AnySubjectAsync(ConversationSubject subject, CancellationToken cancellationToken) =>
        await GuestThreads.SubjectAsync(db, subject, cancellationToken) ?? throw NotFound(subject);

    private static KeyNotFoundException NotFound(ConversationSubject subject) =>
        new(subject.Kind == ConversationSubjectKind.Reservation
            ? $"Booking {subject.Id} was not found."
            : $"Takeaway order {subject.Id} was not found.");

    private async Task<StaffRole> StoredRoleAsync(Guid staffId, CancellationToken cancellationToken) =>
        await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => s.Role)
            .FirstAsync(cancellationToken);

    /// <summary>
    /// Nobody where the platform has blocked guest messages (docs/branch-features.md); otherwise managers, the general
    /// manager and the owner always, and a waiter while the venue lets waiters write.
    /// </summary>
    private static bool MayWrite(StaffRole role, GuestThreads.Subject facts) =>
        facts.MessagesAllowed
        && (role is StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner
            || (role == StaffRole.Waiter && facts.WaitersCanWrite));

    private static void RequireOpen(GuestThreads.Subject facts, DateTime nowUtc)
    {
        if (!facts.IsOpenAt(nowUtc))
        {
            throw new ConversationClosedException(
                GuestThreads.SubjectKindName(facts.Kind), facts.Id, facts.ClosesAtUtc!.Value);
        }
    }

    /// <summary>
    /// The message this author's command already wrote in the thread, or null. A retry is the same words from the same
    /// writer; the id reused for other words - or by another member of staff - is a client bug, and answering it with the
    /// first message would drop the new one without a word: 409 <c>client-command-id-in-use</c>.
    /// </summary>
    /// <exception cref="ClientCommandIdAlreadyUsedException">The id wrote a different message.</exception>
    private async Task<ConversationMessage?> ReplayAsync(
        Guid conversationId,
        MessageAuthor author,
        Guid? staffMemberId,
        string body,
        Guid clientCommandId,
        CancellationToken cancellationToken)
    {
        var written = await db.ConversationMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.ConversationId == conversationId && m.Author == author && m.ClientCommandId == clientCommandId,
                cancellationToken);

        if (written is not null
            && (!string.Equals(written.Body, body, StringComparison.Ordinal) || written.StaffMemberId != staffMemberId))
        {
            throw new ClientCommandIdAlreadyUsedException(
                clientCommandId,
                "That clientCommandId already wrote another message. Generate a fresh one per message, and reuse it only "
                + "when retrying that same message.");
        }

        return written;
    }

    /// <summary>The subject's thread and its messages in order (written, then the database's tie-break), untracked - or nothing while there is none.</summary>
    private async Task<(Conversation? Thread, IReadOnlyList<ConversationMessage> Messages)> ThreadAsync(
        ConversationSubject subject, CancellationToken cancellationToken)
    {
        var thread = subject.Kind == ConversationSubjectKind.Reservation
            ? await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.ReservationId == subject.Id, cancellationToken)
            : await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.TakeawayOrderId == subject.Id, cancellationToken);

        if (thread is null)
        {
            return (null, []);
        }

        var messages = await db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == thread.Id)
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        return (thread, messages);
    }

    /// <summary>
    /// The venue asked "are you coming?", the guest has not answered since, and the answer is still taken - the answer
    /// bar.
    /// </summary>
    private static bool PendingAsk(GuestThreads.Subject facts, IReadOnlyList<ConversationMessage> messages, DateTime nowUtc)
    {
        var lastAsk = messages.ToList().FindLastIndex(m => m.Kind == MessageKind.AskComing);

        return lastAsk >= 0
               && !messages.Skip(lastAsk + 1).Any(m => m.Kind == MessageKind.Answer)
               && facts.AnswerAcceptedAt(nowUtc);
    }

    private static bool After(ConversationMessage message, DateTime? readUtc) => readUtc is null || message.CreatedAtUtc > readUtc;

    private static InboxSubject InboxSubjectOf(GuestThreads.Subject facts) =>
        facts.Kind == ConversationSubjectKind.Reservation
            ? new InboxSubject(
                ConversationRules.SubjectKinds.Reservation,
                facts.Id,
                facts.TableLabel ?? string.Empty,
                facts.GuestName,
                GuestThreads.Hhmm(facts.LocalStartTime!.Value),
                facts.LocalDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                facts.PartySize,
                null,
                null)
            : new InboxSubject(
                ConversationRules.SubjectKinds.Takeaway,
                facts.Id,
                facts.TakeawayCode ?? string.Empty,
                facts.GuestName,
                GuestThreads.Hhmm(facts.ReadyAtLocal!.Value),
                null,
                null,
                facts.ItemCount,
                facts.TotalAmd);

    // ---------------------------------------------------------------- views

    private static MessageAnswerView? AnswerOf(ConversationMessage m) =>
        m.Kind == MessageKind.Answer && m.AnswerKind is { } answer
            ? new MessageAnswerView(GuestThreads.AnswerName(answer), m.LateMinutes)
            : null;

    private static MessageStatusView? StatusOf(ConversationMessage m) =>
        m.Kind == MessageKind.Status && m.AnswerKind is { } answer
            ? new MessageStatusView(
                GuestThreads.AnswerName(answer),
                m.HeldUntilLocal is { } held ? GuestThreads.Hhmm(held) : null,
                answer == GuestAnswer.NotComing)
            : null;

    private static DinerConversationMessage DinerMessage(ConversationMessage m) => new(
        m.Id,
        GuestThreads.AuthorName(m.Author),
        GuestThreads.KindName(m.Kind),
        m.Body,
        AnswerOf(m),
        StatusOf(m),
        m.CreatedAtUtc);

    private static StaffConversationMessage StaffMessage(ConversationMessage m) => new(
        m.Id,
        GuestThreads.AuthorName(m.Author),
        GuestThreads.KindName(m.Kind),
        m.Body,
        AnswerOf(m),
        StatusOf(m),
        m.CreatedAtUtc,
        m.Author == MessageAuthor.Venue ? m.StaffName : null);

    private static DinerMessageResult GuestResult(ConversationMessage m, bool wasReplay) => new(
        m.ConversationId,
        m.Id,
        GuestThreads.AuthorName(m.Author),
        GuestThreads.KindName(m.Kind),
        m.Body,
        m.CreatedAtUtc,
        wasReplay);

    private static StaffMessageResult StaffResult(ConversationMessage m, bool wasReplay) => new(
        m.ConversationId,
        m.Id,
        GuestThreads.AuthorName(m.Author),
        GuestThreads.KindName(m.Kind),
        m.Body,
        m.CreatedAtUtc,
        m.StaffName,
        wasReplay);

    /// <summary>A thread as the history page reads it, in one statement for the page.</summary>
    private sealed class HistoryRow
    {
        public required Guid ConversationId { get; init; }

        public Guid? ReservationId { get; init; }

        public Guid? TakeawayOrderId { get; init; }

        public required DateTime LastMessageUtc { get; init; }

        public required DateTime CreatedAtUtc { get; init; }

        public required int MessageCount { get; init; }

        public DateTime? FirstAtUtc { get; init; }

        public required bool Unanswered { get; init; }
    }

    /// <summary>A thread as the inbox reads it, in one statement for every thread.</summary>
    private sealed class InboxRow
    {
        public required Guid ConversationId { get; init; }

        public Guid? ReservationId { get; init; }

        public Guid? TakeawayOrderId { get; init; }

        public required DateTime LastMessageUtc { get; init; }

        public required int UnreadFromGuest { get; init; }

        public ConversationMessage? Last { get; init; }

        public ConversationMessage? LatestStatus { get; init; }

        public DateTime? OldestUnansweredUtc { get; init; }
    }
}
