using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messages;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Guest-message threads as the rest of the API touches them: the subject's facts, the subject's lock, a thread found or
/// made, the venue's ask and the guest's answers appended, and the one-line summaries on bookings and orders. See
/// <c>docs/messages.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A static helper over the context, like <see cref="DinerNotices"/> and <see cref="PreOrderReads"/>: the booking and
/// pre-order services append to a thread inside their own unit of work, and need no new constructor argument for it.
/// What a message tells the rest - the guest's push and feed entry, the History's line, the floor's push - is written by
/// the handlers of the event the thread raises (<c>Infrastructure/DomainEvents/Messages/</c>), in the same save.
/// </para>
/// <para>
/// <b>Every write takes the subject's row lock first</b> (<see cref="LockAsync"/>) - after the account's, when it takes
/// that too - and only then looks for the thread. Two first messages at once queue on the lock, and the second finds the
/// thread the first made; the filtered unique index is the backstop.
/// </para>
/// </remarks>
internal static class GuestThreads
{
    // ---------------------------------------------------------------- wire names

    public static string AuthorName(MessageAuthor author) => author switch
    {
        MessageAuthor.Guest => "guest",
        MessageAuthor.Venue => "venue",
        _ => "system",
    };

    public static string KindName(MessageKind kind) => kind switch
    {
        MessageKind.Text => "text",
        MessageKind.AskComing => "ask-coming",
        MessageKind.Answer => "answer",
        _ => "status",
    };

    public static string AnswerName(GuestAnswer answer) => answer switch
    {
        GuestAnswer.Coming => "coming",
        GuestAnswer.Late => "late",
        _ => "not-coming",
    };

    public static string SubjectKindName(ConversationSubjectKind kind) =>
        kind == ConversationSubjectKind.Reservation ? ConversationRules.SubjectKinds.Reservation : ConversationRules.SubjectKinds.Takeaway;

    public static string Hhmm(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- the subject

    /// <summary>What a thread needs to know about its booking or order, read in one statement per kind.</summary>
    internal sealed record Subject
    {
        public required ConversationSubjectKind Kind { get; init; }

        public required Guid Id { get; init; }

        public required Guid BranchId { get; init; }

        public required string VenueName { get; init; }

        public required string BranchName { get; init; }

        public string? BranchPhone { get; init; }

        public required string TimeZoneId { get; init; }

        public Guid? DinerUserId { get; init; }

        public required string GuestName { get; init; }

        /// <summary>The number the booking or order holds - the venue's to call.</summary>
        public required string GuestPhone { get; init; }

        public required bool GuestsCanWrite { get; init; }

        public required bool WaitersCanWrite { get; init; }

        /// <summary>When the thread closes, or closed; null while a takeaway order is still moving.</summary>
        public DateTime? ClosesAtUtc { get; init; }

        public ReservationStatus? ReservationStatus { get; init; }

        /// <summary>A booking the venue declined - or nobody answered in time - rather than cancelled.</summary>
        public bool ReservationDeclined { get; init; }

        public DateTime? StartUtc { get; init; }

        public DateTime? EndUtc { get; init; }

        public DateOnly? LocalDate { get; init; }

        public TimeOnly? LocalStartTime { get; init; }

        public int? PartySize { get; init; }

        public string? TableLabel { get; init; }

        public TakeawayStatus? TakeawayStatus { get; init; }

        public string? TakeawayCode { get; init; }

        public DateTime? ReadyAtUtc { get; init; }

        public TimeOnly? ReadyAtLocal { get; init; }

        public int? ItemCount { get; init; }

        public long? TotalAmd { get; init; }

        public bool IsOpenAt(DateTime nowUtc) => ConversationRules.IsOpen(ClosesAtUtc, nowUtc);

        /// <summary>What became of the booking or order - <see cref="MessageHistoryRules.BookingOutcome"/>, <see cref="MessageHistoryRules.TakeawayOutcome"/>.</summary>
        public string Outcome => Kind == ConversationSubjectKind.Reservation
            ? MessageHistoryRules.BookingOutcome(ReservationStatus!.Value, ReservationDeclined)
            : MessageHistoryRules.TakeawayOutcome(TakeawayStatus!.Value);

        /// <summary>"Are you coming?" may still be answered - the answer bar shows.</summary>
        public bool AnswerAcceptedAt(DateTime nowUtc) =>
            ReservationStatus is { } status
            && ConversationRules.AnswerAccepted(status, StartUtc!.Value, EndUtc!.Value, nowUtc);

        /// <summary>The header. <paramref name="withOutcome"/> for staff: the guest's view carries no outcome.</summary>
        public ConversationSubjectView View(bool withOutcome = false) => new(
            SubjectKindName(Kind),
            Id,
            VenueName,
            BranchId,
            StartUtc,
            LocalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LocalStartTime is { } start ? Hhmm(start) : null,
            PartySize,
            TableLabel,
            TakeawayCode,
            ReadyAtLocal is { } ready ? Hhmm(ready) : null,
            Kind == ConversationSubjectKind.Takeaway ? ReadyAtUtc : null,
            withOutcome ? Outcome : null);
    }

    /// <summary>One booking's or order's facts, or null when there is no such subject.</summary>
    public static async Task<Subject?> SubjectAsync(
        YallaDbContext db, ConversationSubject subject, CancellationToken cancellationToken) =>
        subject.Kind == ConversationSubjectKind.Reservation
            ? (await ReservationSubjectsAsync(db, [subject.Id], cancellationToken)).GetValueOrDefault(subject.Id)
            : (await TakeawaySubjectsAsync(db, [subject.Id], cancellationToken)).GetValueOrDefault(subject.Id);

    /// <summary>Bookings' facts, by id - one statement.</summary>
    public static async Task<Dictionary<Guid, Subject>> ReservationSubjectsAsync(
        YallaDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.BranchId,
                VenueName = r.Branch.Venue.Name,
                BranchName = r.Branch.Name,
                r.Branch.PhoneE164,
                r.Branch.TimeZoneId,
                r.Branch.GuestsCanMessage,
                r.Branch.WaitersCanMessage,
                r.DinerUserId,
                r.GuestName,
                r.GuestPhone,
                r.Status,
                r.StartUtc,
                r.EndUtc,
                r.LocalDate,
                r.LocalStartTime,
                r.PartySize,
                TableLabel = r.DiningTable.Label,
                r.CancelledAtUtc,
                Declined = r.DeclineReason != null,

                // A completed booking ended when its table was given back.
                CompletedAtUtc = db.TableSessions
                    .Where(s => s.ReservationId == r.Id)
                    .Max(s => s.ClosedAtUtc),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            r => r.Id,
            r => new Subject
            {
                Kind = ConversationSubjectKind.Reservation,
                Id = r.Id,
                BranchId = r.BranchId,
                VenueName = r.VenueName,
                BranchName = r.BranchName,
                BranchPhone = r.PhoneE164,
                TimeZoneId = r.TimeZoneId,
                DinerUserId = r.DinerUserId,
                GuestName = r.GuestName,
                GuestPhone = r.GuestPhone,
                GuestsCanWrite = r.GuestsCanMessage,
                WaitersCanWrite = r.WaitersCanMessage,
                ClosesAtUtc = ConversationRules.ReservationClosesAtUtc(
                    r.Status, r.EndUtc, r.Status == ReservationStatus.Completed ? r.CompletedAtUtc ?? r.CancelledAtUtc : r.CancelledAtUtc),
                ReservationStatus = r.Status,
                ReservationDeclined = r.Declined,
                StartUtc = r.StartUtc,
                EndUtc = r.EndUtc,
                LocalDate = r.LocalDate,
                LocalStartTime = r.LocalStartTime,
                PartySize = r.PartySize,
                TableLabel = r.TableLabel,
            });
    }

    /// <summary>Takeaway orders' facts, by id - one statement.</summary>
    public static async Task<Dictionary<Guid, Subject>> TakeawaySubjectsAsync(
        YallaDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new
            {
                o.Id,
                o.BranchId,
                Branch = db.Branches
                    .Where(b => b.Id == o.BranchId)
                    .Select(b => new
                    {
                        VenueName = b.Venue.Name,
                        b.Name,
                        b.PhoneE164,
                        b.TimeZoneId,
                        b.GuestsCanMessage,
                        b.WaitersCanMessage,
                    })
                    .First(),
                o.DinerUserId,
                o.GuestName,
                o.GuestPhone,
                o.Status,
                o.Code,
                o.ReadyAtUtc,
                o.RequestedPickupUtc,
                o.RequestedPickupLocalTime,
                EndedAtUtc = o.CollectedAtUtc ?? o.NoShowAtUtc ?? o.DeclinedAtUtc ?? o.CancelledAtUtc,
                ItemCount = o.Lines.Sum(l => (int?)l.Quantity) ?? 0,
                TotalAmd = o.Lines.Sum(l => (long?)(l.UnitPriceAmdSnapshot * l.Quantity)) ?? 0L,
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            o => o.Id,
            o => new Subject
            {
                Kind = ConversationSubjectKind.Takeaway,
                Id = o.Id,
                BranchId = o.BranchId,
                VenueName = o.Branch.VenueName,
                BranchName = o.Branch.Name,
                BranchPhone = o.Branch.PhoneE164,
                TimeZoneId = o.Branch.TimeZoneId,
                DinerUserId = o.DinerUserId,
                GuestName = o.GuestName,
                GuestPhone = o.GuestPhone,
                GuestsCanWrite = o.Branch.GuestsCanMessage,
                WaitersCanWrite = o.Branch.WaitersCanMessage,
                ClosesAtUtc = ConversationRules.TakeawayClosesAtUtc(o.Status, o.EndedAtUtc),
                TakeawayStatus = o.Status,
                TakeawayCode = o.Code,
                ReadyAtUtc = o.ReadyAtUtc ?? o.RequestedPickupUtc,
                ReadyAtLocal = o.ReadyAtUtc is { } readyAt
                    ? BranchZone.For(o.Branch.TimeZoneId).LocalTimeAt(readyAt)
                    : o.RequestedPickupLocalTime,
                ItemCount = o.ItemCount,
                TotalAmd = o.TotalAmd,
            });
    }

    // ---------------------------------------------------------------- the lock and the thread

    /// <summary>
    /// The subject's row lock, held to the end of the open transaction: the thread of one booking or order is made and
    /// written one writer at a time.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    public static async Task LockAsync(YallaDbContext db, ConversationSubject subject, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A thread's subject is locked by a transaction. Begin one before taking it.");
        }

        if (subject.Kind == ConversationSubjectKind.Reservation)
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT Id FROM Reservations WITH (UPDLOCK, HOLDLOCK) WHERE Id = {subject.Id}", cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT Id FROM TakeawayOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {subject.Id}", cancellationToken);
        }
    }

    /// <summary>The subject's thread, tracked, or null while there is none. Under the subject's lock for a write.</summary>
    public static Task<Conversation?> FindAsync(
        YallaDbContext db, ConversationSubject subject, CancellationToken cancellationToken) =>
        subject.Kind == ConversationSubjectKind.Reservation
            ? db.Conversations.FirstOrDefaultAsync(c => c.ReservationId == subject.Id, cancellationToken)
            : db.Conversations.FirstOrDefaultAsync(c => c.TakeawayOrderId == subject.Id, cancellationToken);

    /// <summary>The subject's thread, tracked - made and added to the context when there is none. Under the subject's lock.</summary>
    /// <remarks>
    /// A new thread is linked to the account the subject holds <b>now</b>, read under the lock - never to one a caller
    /// read before taking it. An account deleted in between has had its link cut from the booking or order (the
    /// deletion holds the account's lock, then the subject's row), and a thread made from the stale read would hand the
    /// venue's record back to a tombstone.
    /// </remarks>
    public static async Task<Conversation> FindOrStartAsync(
        YallaDbContext db,
        ConversationSubject subject,
        Guid branchId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(db, subject, cancellationToken) is { } existing)
        {
            return existing;
        }

        var dinerUserId = subject.Kind == ConversationSubjectKind.Reservation
            ? await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == subject.Id)
                .Select(r => r.DinerUserId)
                .FirstOrDefaultAsync(cancellationToken)
            : await db.TakeawayOrders
                .AsNoTracking()
                .Where(o => o.Id == subject.Id)
                .Select(o => o.DinerUserId)
                .FirstOrDefaultAsync(cancellationToken);

        var started = subject.Kind == ConversationSubjectKind.Reservation
            ? Conversation.ForReservation(branchId, subject.Id, dinerUserId, nowUtc)
            : Conversation.ForTakeaway(branchId, subject.Id, dinerUserId, nowUtc);

        db.Conversations.Add(started);

        return started;
    }

    // ---------------------------------------------------------------- the ask and the answers

    /// <summary>
    /// The venue's "are you coming?" as a message in the booking's thread, with who asked - in the caller's unit of work,
    /// under the booking's lock (after the account's, which the caller already holds). The guest is told - push
    /// <c>message.to-guest</c> (<c>categoryId</c> <c>venue-ask</c>) and feed <c>venue-message</c> - and the History says
    /// who asked, by the ask's <see cref="VenueAskedComing"/> handlers in the caller's save.
    /// </summary>
    /// <param name="db">The caller's context, with a transaction open.</param>
    /// <param name="reservation">The booking, its ask already recorded.</param>
    /// <param name="staffMemberId">Who asked.</param>
    /// <param name="accountLive">The caller took the booking's account lock and found the account live.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the guest is told - false when the booking has no live account behind it.</returns>
    public static async Task<bool> AskAsync(
        YallaDbContext db,
        Reservation reservation,
        Guid staffMemberId,
        bool accountLive,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var subject = ConversationSubject.Reservation(reservation.Id);
        await LockAsync(db, subject, cancellationToken);

        var staffName = await StaffNameAsync(db, staffMemberId, cancellationToken);
        var thread = await FindOrStartAsync(db, subject, reservation.BranchId, nowUtc, cancellationToken);
        var ask = thread.AskComing(staffMemberId, staffName, nowUtc);
        db.ConversationMessages.Add(ask);

        // The thread's account is the booking's, read under the locks - so the live account the caller found is the one
        // the ask's notice tells (VenueMessageGuestNotice), or there is none.
        return accountLive && thread.DinerUserId is not null;
    }

    /// <summary>
    /// The guest's answer to "are you coming?" - "on my way", "running late", or "not coming" (the booking's own cancel) -
    /// and the system line after it, in the caller's unit of work, under the booking's lock. The thread is made if there
    /// is none. The guest is told nothing; the floor's <c>guestMessages</c> push hangs on the thread's
    /// <see cref="GuestAnswered"/> event.
    /// </summary>
    /// <param name="db">The caller's context, with a transaction open.</param>
    /// <param name="reservation">The booking, already changed by the answer.</param>
    /// <param name="answer">What the guest said.</param>
    /// <param name="lateMinutes">For <see cref="GuestAnswer.Late"/>.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="onlyWhenAsked">
    /// For "not coming": append only when the booking already has a thread, or the answer window has opened (two hours
    /// before the start) - a booking cancelled next week is a cancellation, not an answer to anybody's question.
    /// </param>
    public static async Task AnswerAsync(
        YallaDbContext db,
        Reservation reservation,
        GuestAnswer answer,
        int? lateMinutes,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool onlyWhenAsked = false)
    {
        var subject = ConversationSubject.Reservation(reservation.Id);
        await LockAsync(db, subject, cancellationToken);

        if (onlyWhenAsked
            && nowUtc < PreOrderRules.LockedAtUtc(reservation.StartUtc)
            && await FindAsync(db, subject, cancellationToken) is null)
        {
            return;
        }

        TimeOnly? heldUntil = null;

        if (answer == GuestAnswer.Late)
        {
            var timeZoneId = await db.Branches
                .AsNoTracking()
                .Where(b => b.Id == reservation.BranchId)
                .Select(b => b.TimeZoneId)
                .FirstAsync(cancellationToken);

            heldUntil = BranchZone.For(timeZoneId).LocalTimeAt(reservation.StartUtc.AddMinutes(lateMinutes!.Value));
        }

        var thread = await FindOrStartAsync(db, subject, reservation.BranchId, nowUtc, cancellationToken);
        var (said, status) = thread.Answer(answer, lateMinutes, heldUntil, nowUtc);

        db.ConversationMessages.Add(said);
        db.ConversationMessages.Add(status);
    }

    /// <summary>A staff member's name as a message keeps it.</summary>
    public static async Task<string> StaffNameAsync(YallaDbContext db, Guid staffMemberId, CancellationToken cancellationToken) =>
        await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffMemberId)
            .Select(s => s.FullName)
            .FirstOrDefaultAsync(cancellationToken)
        ?? string.Empty;

    // ---------------------------------------------------------------- the venue's threads

    /// <summary>
    /// The branch's threads the venue's tablet keeps in view: a message in the last 24 hours, or a subject still taking
    /// messages - a booking pending, confirmed or seated until two hours after its end, an order sent, accepted or ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One expression for the inbox and the floor's badge, so the two count the same threads. A booking that ended
    /// (cancelled, missed, completed) under two hours ago is open too; its last message is then under two hours old,
    /// inside the 24, unless nobody wrote since - and then nothing in it is unanswered or unread worth a ring.
    /// </para>
    /// <para>
    /// <b>Its cost does not grow with the branch's history</b> - every floor poll reads it. Three disjoint legs, each
    /// found from an index by a bounded predicate and glued with <c>UNION ALL</c>: the threads written to in the last 24
    /// hours (<c>IX_Conversations_BranchId_LastMessageUtc</c>); the quieter threads of the bookings still live
    /// (<c>IX_Reservations_BranchId_EndUtc_Live</c>, filtered to pending, confirmed and seated, from two hours ago on);
    /// and those of the orders still moving (<c>IX_TakeawayOrders_BranchId_Status</c>). Each subject finds its thread by
    /// the thread's unique subject index. A thread is the same branch as its subject: neither ever moves.
    /// </para>
    /// </remarks>
    public static IQueryable<Conversation> RelevantAt(YallaDbContext db, Guid branchId, DateTime nowUtc)
    {
        var recent = nowUtc.AddHours(-ConversationRules.InboxRecentHours);
        var endedBefore = nowUtc.AddHours(-ConversationRules.OpenAfterEndHours);

        var spokenRecently = db.Conversations
            .Where(c => c.BranchId == branchId && c.LastMessageUtc >= recent);

        // The status list is written out as constants so it lands in the SQL as literals - the filtered index's own
        // filter - rather than as a parameter the optimiser could not match it against.
        var liveBookings = db.Reservations
            .Where(r => r.BranchId == branchId
                        && (r.Status == ReservationStatus.PendingApproval
                            || r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.Seated)
                        && r.EndUtc > endedBefore)
            .Join(db.Conversations, r => (Guid?)r.Id, c => c.ReservationId, (r, c) => c)
            .Where(c => c.LastMessageUtc < recent);

        var movingOrders = db.TakeawayOrders
            .Where(o => o.BranchId == branchId
                        && (o.Status == TakeawayStatus.Sent
                            || o.Status == TakeawayStatus.Accepted
                            || o.Status == TakeawayStatus.Ready))
            .Join(db.Conversations, o => (Guid?)o.Id, c => c.TakeawayOrderId, (o, c) => c)
            .Where(c => c.LastMessageUtc < recent);

        return spokenRecently.Concat(liveBookings).Concat(movingOrders);
    }

    /// <summary>
    /// Those of <paramref name="threads"/> that still take messages now - <see cref="ConversationRules.ReservationClosesAtUtc"/> and
    /// <see cref="ConversationRules.TakeawayClosesAtUtc"/> in SQL, for the floor's remind: a guest message in a thread
    /// nobody can answer any more must not ring the tablet for the rest of the day.
    /// </summary>
    public static IQueryable<Conversation> OpenAt(IQueryable<Conversation> threads, YallaDbContext db, DateTime nowUtc)
    {
        var endedAfter = nowUtc.AddHours(-ConversationRules.OpenAfterEndHours);

        return threads.Where(c =>
            (c.ReservationId != null
             && db.Reservations.Any(r => r.Id == c.ReservationId
                                         && ((r.Status == ReservationStatus.PendingApproval
                                              || r.Status == ReservationStatus.Confirmed
                                              || r.Status == ReservationStatus.Seated)
                                             ? r.EndUtc > endedAfter
                                             : ((r.Status == ReservationStatus.Completed
                                                    ? db.TableSessions
                                                          .Where(s => s.ReservationId == r.Id)
                                                          .Max(s => s.ClosedAtUtc) ?? r.CancelledAtUtc
                                                    : r.CancelledAtUtc) ?? r.EndUtc) > endedAfter)))
            || (c.TakeawayOrderId != null
                && db.TakeawayOrders.Any(o => o.Id == c.TakeawayOrderId
                                              && (o.Status == TakeawayStatus.Sent
                                                  || o.Status == TakeawayStatus.Accepted
                                                  || o.Status == TakeawayStatus.Ready
                                                  || (o.CollectedAtUtc ?? o.NoShowAtUtc ?? o.DeclinedAtUtc ?? o.CancelledAtUtc)
                                                  > endedAfter))));
    }

    // ---------------------------------------------------------------- the one-line summaries

    /// <summary>
    /// The bookings' threads in one line each, for the guest's own reads: two statements whatever the number of bookings.
    /// A booking with no thread that takes none, or whose thread closed over 30 days ago, gets none.
    /// </summary>
    public static async Task<IReadOnlyList<ReservationView>> AttachToBookingsAsync(
        YallaDbContext db, IReadOnlyList<ReservationView> views, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (views.Count == 0)
        {
            return views;
        }

        var ids = views.Select(v => v.Id).Distinct().ToList();
        var subjects = await ReservationSubjectsAsync(db, ids, cancellationToken);
        var lines = await LinesAsync(db.Conversations.Where(c => c.ReservationId != null && ids.Contains(c.ReservationId.Value)), cancellationToken);
        var byBooking = lines.ToDictionary(l => l.ReservationId!.Value);

        return
        [
            .. views.Select(v => subjects.TryGetValue(v.Id, out var subject)
                ? v with { Messages = Summary(subject, byBooking.GetValueOrDefault(v.Id), nowUtc) }
                : v),
        ];
    }

    /// <summary>The takeaway orders' threads in one line each, by order id - for the Orders tab. Two statements.</summary>
    public static async Task<IReadOnlyDictionary<Guid, MessagesSummary>> SummariesForTakeawayAsync(
        YallaDbContext db, IReadOnlyCollection<Guid> orderIds, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<Guid, MessagesSummary>();
        }

        var subjects = await TakeawaySubjectsAsync(db, orderIds, cancellationToken);
        var lines = await LinesAsync(
            db.Conversations.Where(c => c.TakeawayOrderId != null && orderIds.Contains(c.TakeawayOrderId.Value)), cancellationToken);
        var byOrder = lines.ToDictionary(l => l.TakeawayOrderId!.Value);
        var summaries = new Dictionary<Guid, MessagesSummary>();

        foreach (var (id, subject) in subjects)
        {
            if (Summary(subject, byOrder.GetValueOrDefault(id), nowUtc) is { } summary)
            {
                summaries[id] = summary;
            }
        }

        return summaries;
    }

    /// <summary>A thread's line as the guest's lists read it: who wrote last, what, and how much is unread.</summary>
    internal sealed record ThreadLine
    {
        public required Guid ConversationId { get; init; }

        public Guid? ReservationId { get; init; }

        public Guid? TakeawayOrderId { get; init; }

        public required Guid BranchId { get; init; }

        public required DateTime LastMessageUtc { get; init; }

        public required int UnreadForGuest { get; init; }

        public MessageAuthor? LastAuthor { get; init; }

        public MessageKind? LastKind { get; init; }

        public string? LastBody { get; init; }
    }

    /// <summary>The threads' lines, in one statement.</summary>
    public static async Task<List<ThreadLine>> LinesAsync(IQueryable<Conversation> threads, CancellationToken cancellationToken) =>
        await threads
            .AsNoTracking()
            .Select(c => new ThreadLine
            {
                ConversationId = c.Id,
                ReservationId = c.ReservationId,
                TakeawayOrderId = c.TakeawayOrderId,
                BranchId = c.BranchId,
                LastMessageUtc = c.LastMessageUtc,
                UnreadForGuest = c.Messages.Count(m => m.Author == MessageAuthor.Venue
                                                       && (c.GuestLastReadUtc == null || m.CreatedAtUtc > c.GuestLastReadUtc)),
                LastAuthor = c.Messages
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Sequence)
                    .Select(m => (MessageAuthor?)m.Author)
                    .FirstOrDefault(),
                LastKind = c.Messages
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Sequence)
                    .Select(m => (MessageKind?)m.Kind)
                    .FirstOrDefault(),
                LastBody = c.Messages
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Sequence)
                    .Select(m => m.Body)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

    private static MessagesSummary? Summary(Subject subject, ThreadLine? line, DateTime nowUtc)
    {
        var open = subject.IsOpenAt(nowUtc);

        if (line is null)
        {
            return open ? new MessagesSummary(0, null, null, null, null, true) : null;
        }

        if (!ConversationRules.GuestMayRead(subject.ClosesAtUtc, nowUtc))
        {
            return null;
        }

        return new MessagesSummary(
            line.UnreadForGuest,
            line.LastAuthor is { } author ? AuthorName(author) : null,
            line.LastKind is { } kind ? KindName(kind) : null,
            line.LastBody is { } body ? ConversationRules.Preview(body) : null,
            line.LastAuthor is null ? null : line.LastMessageUtc,
            open);
    }
}
