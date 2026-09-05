using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Reservations;
using Yalla.Domain.Identity;
using Yalla.Domain.Messaging;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The outbox: written with its cause, leased, retried, given up on, and discarded when stale.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutboxTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    /// <summary>See <c>SqlServerFixture.ClearOutboxAsync</c> for why these tests need this.</summary>
    private Task ResetOutboxAsync() => fixture.ClearOutboxAsync(new TestClock(Now));

    // ------------------------------------------------------------ 6. one transaction or neither

    /// <summary>
    /// <b>Test 6.</b> A booking that fails to insert leaves no reminder behind.
    /// </summary>
    /// <remarks>
    /// The property the whole outbox rests on. If the message were enqueued after the booking
    /// committed, there would be a window in which the booking exists and the reminder does not - and
    /// the diner would find out by not being reminded.
    /// </remarks>
    [SkippableFact]
    public async Task A_booking_that_does_not_commit_leaves_no_message_behind()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);
        var diner = TestActor.Diner();

        await ResetOutboxAsync();

        // Somebody already has this table at this time, so the insert is refused inside the lock.
        await using (var firstDb = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(firstDb, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(19, 0)));
        }

        await using var clashDb = fixture.CreateContext(clock);

        await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => fixture.CreateReservationService(clashDb, clock, diner)
                .CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(19, 30))));

        await using var verify = fixture.CreateContext(clock);

        // The successful booking has its two messages. The refused one has none - not a partial set,
        // not an orphan reminder for a booking that does not exist.
        var messages = await verify.OutboxMessages.AsNoTracking().ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.StartsWith("reservation:", m.IdempotencyKey, StringComparison.Ordinal));

        var bookings = await verify.Reservations.CountAsync(r => r.BranchId == branch.BranchId);

        Assert.Equal(1, bookings);
    }

    // ------------------------------------------------------------ 7. the same message twice

    [SkippableFact]
    public async Task A_duplicate_idempotency_key_is_refused_by_the_database()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var key = OutboxMessageTypes.KeyFor("reservation", Guid.CreateVersion7(), "reminder");

        var outbox = fixture.CreateOutbox(db, clock);

        outbox.Enqueue(OutboxMessageTypes.ReservationReminder, new { a = 1 }, clock.UtcNow, key);
        await db.SaveChangesAsync();

        // A second write of the same key. The index refuses it - this is not a check in a service
        // that two concurrent callers could both pass.
        await using var secondDb = fixture.CreateContext(clock);
        var second = fixture.CreateOutbox(secondDb, clock);

        second.Enqueue(OutboxMessageTypes.ReservationReminder, new { a = 2 }, clock.UtcNow, key);

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => secondDb.SaveChangesAsync());

        Assert.True(UniqueViolation.IsOn(refused, DatabaseIndexNames.OutboxIdempotency));

        // And the first one is untouched: the caller that got there first is unaffected.
        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == key);

        Assert.Contains("\"a\":1", stored.PayloadJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 8. cancelling the cause

    /// <summary>
    /// <b>Test 8.</b> Cancelling a booking takes its unsent reminder with it.
    /// </summary>
    /// <remarks>
    /// A push arriving for a booking somebody cancelled an hour ago is worse than no push: it is the
    /// notification the diner remembers, and it teaches them ours are wrong.
    /// </remarks>
    [SkippableFact]
    public async Task Cancelling_a_booking_deletes_its_unsent_reminder()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();

        Guid bookingId;

        await using (var bookDb = fixture.CreateContext(clock))
        {
            var view = await fixture.CreateReservationService(bookDb, clock, diner)
                .CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(20, 0)));

            bookingId = view.Id;
        }

        await using (var check = fixture.CreateContext(clock))
        {
            Assert.Equal(
                2,
                await check.OutboxMessages.CountAsync(
                    m => m.IdempotencyKey.StartsWith($"reservation:{bookingId}:")));
        }

        await using (var cancelDb = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(cancelDb, clock, diner)
                .CancelAsync(new CancelReservationCommand(bookingId, "changed our plans"));
        }

        await using var verify = fixture.CreateContext(clock);

        Assert.Equal(
            0,
            await verify.OutboxMessages.CountAsync(
                m => m.IdempotencyKey.StartsWith($"reservation:{bookingId}:")));
    }

    // ------------------------------------------------------------ 9. giving up

    [SkippableFact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_and_stops_being_tried()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var options = new OutboxOptions { MaxAttempts = 3, BaseBackoffSeconds = 1, StaleAfterMinutes = 10_000 };
        var handler = new ExplodingHandler();

        await ResetOutboxAsync();

        Enqueue(db, clock, "reservation:x:reminder");
        await db.SaveChangesAsync();

        var attemptsSeen = 0;

        for (var pass = 0; pass < 6; pass++)
        {
            await using var passDb = fixture.CreateContext(clock);
            var dispatcher = Dispatcher(passDb, clock, options, handler);

            var result = await dispatcher.RunOnceAsync();

            if (result.Failed > 0)
            {
                attemptsSeen++;
            }

            // Move past the backoff so the next pass finds it due.
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        // Three attempts, then nothing. Not four, and not for ever.
        Assert.Equal(options.MaxAttempts, attemptsSeen);
        Assert.Equal(options.MaxAttempts, handler.Calls);

        await using var verify = fixture.CreateContext(clock);
        var message = await verify.OutboxMessages.AsNoTracking().SingleAsync();

        Assert.NotNull(message.DeadLetteredAtUtc);
        Assert.Equal(options.MaxAttempts, message.AttemptCount);
        Assert.Contains("the provider is on fire", message.LastError!, StringComparison.Ordinal);
        Assert.Null(message.SentAtUtc);
    }

    // ------------------------------------------------------------ 10. two dispatchers

    /// <summary>
    /// <b>Test 10.</b> Two dispatchers polling at once deliver each message exactly once.
    /// </summary>
    /// <remarks>
    /// Real concurrent runs against real leases, on separate connections. A mocked lease would prove
    /// only that the code compiles; what is under test is <c>UPDLOCK, READPAST</c> deciding that the
    /// second dispatcher skips a claimed row rather than blocking on it.
    /// </remarks>
    [SkippableFact]
    public async Task Two_dispatchers_running_together_send_each_message_exactly_once()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        const int Messages = 12;

        await ResetOutboxAsync();

        for (var i = 0; i < Messages; i++)
        {
            Enqueue(db, clock, $"reservation:{Guid.CreateVersion7()}:reminder");
        }

        await db.SaveChangesAsync();

        var options = new OutboxOptions { BatchSize = Messages, StaleAfterMinutes = 10_000 };
        var counting = new CountingHandler();

        await using var firstDb = fixture.CreateContext(clock);
        await using var secondDb = fixture.CreateContext(clock);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Run(YallaDbContext context)
        {
            await gate.Task;

            var pass = await Dispatcher(context, clock, options, counting).RunOnceAsync();

            return pass.Sent;
        }

        var a = Task.Run(() => Run(firstDb));
        var b = Task.Run(() => Run(secondDb));

        gate.SetResult();

        var sent = await Task.WhenAll(a, b);

        // Between them they sent every message, and the handler ran exactly once per message.
        Assert.Equal(Messages, sent.Sum());
        Assert.Equal(Messages, counting.Calls);

        await using var verify = fixture.CreateContext(clock);

        Assert.Equal(Messages, await verify.OutboxMessages.CountAsync(m => m.SentAtUtc != null));
        Assert.Equal(0, await verify.OutboxMessages.CountAsync(m => m.SentAtUtc == null));

        // Neither of them was handed a message twice: the sent counts partition the batch rather
        // than overlapping it.
        Assert.All(sent, s => Assert.InRange(s, 0, Messages));
    }

    // ------------------------------------------------------------ 11. the laptop was asleep

    /// <summary>
    /// <b>Test 11.</b> A message overdue past the threshold is dead-lettered on the first pass, not
    /// sent.
    /// </summary>
    /// <remarks>
    /// The machine-was-asleep case, and the reason the threshold exists. Waking a laptop after a
    /// night shut must not fire a reminder for a dinner that already happened.
    /// </remarks>
    [SkippableFact]
    public async Task A_message_overdue_past_the_threshold_is_discarded_rather_than_sent()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var options = new OutboxOptions { StaleAfterMinutes = 60 };
        var handler = new CountingHandler();

        await ResetOutboxAsync();

        // One from last night, one due ten minutes ago. Only the first is past saving.
        Enqueue(db, clock, "reservation:overnight:reminder", clock.UtcNow.AddHours(-9));
        Enqueue(db, clock, "reservation:recent:reminder", clock.UtcNow.AddMinutes(-10));

        await db.SaveChangesAsync();

        await using var passDb = fixture.CreateContext(clock);
        var pass = await Dispatcher(passDb, clock, options, handler).RunOnceAsync();

        Assert.Equal(1, pass.Stale);
        Assert.Equal(1, pass.Sent);

        // The overnight one was never handed to the channel at all.
        Assert.Equal(1, handler.Calls);

        await using var verify = fixture.CreateContext(clock);

        var overnight = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == "reservation:overnight:reminder");

        Assert.NotNull(overnight.DeadLetteredAtUtc);
        Assert.Null(overnight.SentAtUtc);
        Assert.Contains("Overdue by", overnight.LastError!, StringComparison.Ordinal);

        var recent = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == "reservation:recent:reminder");

        Assert.NotNull(recent.SentAtUtc);
        Assert.Null(recent.DeadLetteredAtUtc);
    }

    [SkippableFact]
    public async Task The_platform_view_reports_what_broke()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var options = new OutboxOptions { MaxAttempts = 1, StaleAfterMinutes = 10_000 };

        await ResetOutboxAsync();

        Enqueue(db, clock, "reservation:broken:reminder");
        await db.SaveChangesAsync();

        await using (var passDb = fixture.CreateContext(clock))
        {
            await Dispatcher(passDb, clock, options, new ExplodingHandler()).RunOnceAsync();
        }

        await using var readDb = fixture.CreateContext(clock);
        var health = await fixture.CreateOutbox(readDb, clock).GetHealthAsync();

        Assert.Equal(1, health.DeadLettered);
        Assert.Equal(0, health.Pending);
        Assert.Equal(0, health.SentLastDay);

        var letter = Assert.Single(health.DeadLetters);

        Assert.Equal("reservation:broken:reminder", letter.IdempotencyKey);
        Assert.Contains("the provider is on fire", letter.LastError!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private static void Enqueue(YallaDbContext db, IClock clock, string key, DateTime? dueAt = null) =>
        db.OutboxMessages.Add(new OutboxMessage(
            OutboxMessageTypes.ReservationReminder,
            "{}",
            dueAt ?? clock.UtcNow,
            key,
            clock.UtcNow));

    private static OutboxDispatcher Dispatcher(
        YallaDbContext db,
        IClock clock,
        OutboxOptions options,
        IOutboxHandler handler) =>
        new(db, clock, options, [handler], NullLogger<OutboxDispatcher>.Instance);

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, TimeOnly at) =>
        new(
            branch.BranchId,
            tableId,
            BookingDate,
            at,
            PartySize: 2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());

    /// <summary>Fails every time, with a message worth reading in the dead-letter list.</summary>
    private sealed class ExplodingHandler : IOutboxHandler
    {
        public int Calls { get; private set; }

        public string MessageType => OutboxMessageTypes.ReservationReminder;

        public Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
        {
            Calls++;

            throw new InvalidOperationException("the provider is on fire");
        }
    }

    /// <summary>Succeeds, and counts. Thread-safe: two dispatchers call it at once.</summary>
    private sealed class CountingHandler : IOutboxHandler
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string MessageType => OutboxMessageTypes.ReservationReminder;

        public Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);

            return Task.CompletedTask;
        }
    }
}
