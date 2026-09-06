using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Ordering;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Messaging;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The promise the tab event stream makes to whoever is reading it.
/// </summary>
/// <remarks>
/// <para>
/// A catching-up client replays <c>TabEvents</c> in <c>Sequence</c> order, ignores any type it does
/// not recognise, and remembers its position. That only works if two things stay true: the sequence
/// is <b>contiguous from one</b>, so "I have seen up to 14" is a complete statement; and every event
/// on the stream is a <c>TabEventType</c>, so an unrecognised one is a new member of a known enum
/// rather than something from another system entirely.
/// </para>
/// <para>
/// Both are properties of the whole backend rather than of one service, and the scheduler is the
/// first thing added since the stream existed that writes to the database on its own schedule. These
/// tests are here to say what it is not allowed to do to the stream.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabEventContractTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 13, 15, 30, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ the scheduler is not a writer

    /// <summary>
    /// Every one of the five message types dispatches, and the tab's event stream is untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox has its own table and its own type strings - <c>reservation.reminder</c> and the
    /// rest - which are deliberately <i>not</i> <c>TabEventType</c> members. A notification is
    /// something that leaves the building; a tab event is a fact about the bill. Putting a delivery
    /// concern on the stream would hand every client an event type it has to know to ignore, and
    /// would move the sequence for something the tab did not do.
    /// </para>
    /// <para>
    /// All five handlers are driven here rather than the two a tab naturally produces, because what
    /// is being asserted is a property of the dispatcher and its handlers as a set.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Dispatching_every_message_type_leaves_the_tab_event_stream_exactly_as_it_was()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var diner = await DinerAsync(db, clock);
        var tab = await TabWithOrdersAsync(branch, menu, clock, diner);
        var booking = await BookingAsync(branch, clock, diner);

        var before = await StreamAsync(tab.TabId, clock);

        Assert.NotEmpty(before);

        // One of each, all five, with payloads the handlers can actually read.
        await using (var enqueueDb = fixture.CreateContext(clock))
        {
            var reservationNotice = new
            {
                reservationId = booking,
                dinerUserId = diner,
                venueName = "Test Venue",
                branchName = "Test Branch",
                tableLabel = "1",
                localStartTime = new TimeOnly(20, 0),
                reservationCode = "ABCD1234",
                graceExtensionMinutes = 10,
                approved = true,
            };

            var tabNotice = new
            {
                tabId = tab.TabId,
                participantId = tab.HostParticipantId,
                dinerUserId = diner,
                tableLabel = "3",
            };

            var outbox = fixture.CreateOutbox(enqueueDb, clock);
            var run = Guid.NewGuid().ToString("N");

            outbox.Enqueue(OutboxMessageTypes.ReservationReminder, reservationNotice, clock.UtcNow, $"{run}:1");
            outbox.Enqueue(OutboxMessageTypes.ReservationLateNudge, reservationNotice, clock.UtcNow, $"{run}:2");
            outbox.Enqueue(OutboxMessageTypes.ReservationDecided, reservationNotice, clock.UtcNow, $"{run}:3");
            outbox.Enqueue(OutboxMessageTypes.ParticipantApproved, tabNotice, clock.UtcNow, $"{run}:4");
            outbox.Enqueue(OutboxMessageTypes.OrderReady, tabNotice, clock.UtcNow, $"{run}:5");

            await enqueueDb.SaveChangesAsync();
        }

        var channel = new CountingChannel();

        await using (var passDb = fixture.CreateContext(clock))
        {
            var pass = await AllHandlers(passDb, clock, channel).RunOnceAsync();

            Assert.Equal(5, pass.Sent);
            Assert.Equal(0, pass.Failed);
        }

        // Every handler ran and reached the channel, so this is not five no-ops.
        Assert.Equal(5, channel.Sent);

        var after = await StreamAsync(tab.TabId, clock);

        // Identical: same length, same positions, same types, in the same order.
        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------ contiguity under a race

    /// <summary>
    /// Writers racing on one tab, with the scheduler running through the middle of it, still produce
    /// 1..N with no gaps and no repeats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number is assigned by the application from the tab's current maximum, so racing writers
    /// reach for the same one; the unique index refuses the loser, which renumbers and retries. The
    /// failure this guards against is the tempting alternative - an <c>IDENTITY</c> column - which
    /// is contiguous per <i>table</i> and therefore full of holes per tab, so a client that has seen
    /// 14 can never tell a gap from a message still in flight.
    /// </para>
    /// <para>
    /// The dispatcher runs concurrently because it is the one thing in the system that writes
    /// without a person waiting for it. It has no business on this stream, and a race is the way to
    /// find out whether that is true or merely usually true.
    /// </para>
    /// <para>
    /// Three writers, because that is <c>TabLedger.TotalsRetryAttempts</c>. Every one of them
    /// collides on the tab's row version and all of them must still succeed; a fourth simultaneous
    /// order can exhaust the budget and be refused, which is a decision Prompt 8 made about the
    /// totals cache and not a property of the sequence.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Concurrent_writers_and_a_running_scheduler_leave_the_sequence_contiguous()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var diner = await DinerAsync(db, clock);
        var tab = await TabWithOrdersAsync(branch, menu, clock, diner);

        const int Writers = TabLedger.TotalsRetryAttempts;

        var contexts = new List<YallaDbContext>();

        try
        {
            for (var i = 0; i < Writers; i++)
            {
                contexts.Add(fixture.CreateContext(clock));
            }

            await using var schedulerDb = fixture.CreateContext(clock);

            // Released together, so they are inside each other's numbering rather than politely
            // queued behind it.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var orders = contexts.Select(context => Task.Run(async () =>
            {
                await gate.Task;

                await fixture.CreateOrderService(context, clock, TestActor.Participant(tab.HostParticipantId))
                    .PlaceOrderAsync(new PlaceOrderCommand(
                        tab.TabId, [new OrderItemInput(menu.Coffee, 1)], Guid.CreateVersion7()));
            })).ToList();

            var scheduler = Task.Run(async () =>
            {
                await gate.Task;

                await AllHandlers(schedulerDb, clock, new CountingChannel()).RunOnceAsync();
            });

            gate.SetResult();

            await Task.WhenAll([.. orders, scheduler]);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        await using var verify = fixture.CreateContext(clock);

        var events = await verify.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == tab.TabId)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Type })
            .ToListAsync();

        var trail = string.Join(", ", events.Select(e => $"{e.Sequence}:{e.Type}"));

        // Every racing order landed on the stream, alongside the one placed during setup, so the
        // writers really did overlap something rather than one of them quietly losing.
        Assert.Equal(Writers + 1, events.Count(e => e.Type == TabEventType.OrderPlaced));

        // 1..N, each exactly once. This is the statement "I have seen up to 14" depends on.
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(i => (long)i),
            events.Select(e => e.Sequence));

        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());

        // And every type on the stream is a member of the enum a client compiled against, so an
        // unrecognised one is a new tab event and never a stray from somewhere else.
        Assert.All(
            events,
            e => Assert.True(
                Enum.IsDefined(e.Type),
                $"{e.Type} is not a TabEventType. The stream was: {trail}"));
    }

    // ------------------------------------------------------------ helpers

    private async Task<IReadOnlyList<string>> StreamAsync(Guid tabId, IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == tabId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence + ":" + e.Type)
            .ToListAsync();
    }

    /// <summary>Every handler the dispatcher ships with, over one context.</summary>
    private static OutboxDispatcher AllHandlers(
        YallaDbContext db,
        IClock clock,
        INotificationChannel channel) =>
        new(
            db,
            clock,
            new OutboxOptions { StaleAfterMinutes = 10_000 },
            [
                new ReservationReminderHandler(db, channel, NullLogger<ReservationReminderHandler>.Instance),
                new ReservationLateNudgeHandler(db, channel, NullLogger<ReservationLateNudgeHandler>.Instance),
                new ReservationDecidedHandler(db, channel),
                new ParticipantApprovedHandler(db, channel),
                new OrderReadyHandler(db, channel),
            ],
            NullLogger<OutboxDispatcher>.Instance);

    private static async Task<Guid> DinerAsync(YallaDbContext db, IClock clock)
    {
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3748{unique}", "en");

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(
            diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Android, "en", clock.UtcNow));

        await db.SaveChangesAsync();

        return diner.Id;
    }

    /// <summary>A confirmed booking, so the reminder and nudge handlers have something to read.</summary>
    private async Task<Guid> BookingAsync(TestBranch branch, IClock clock, Guid dinerUserId)
    {
        await using var db = fixture.CreateContext(clock);

        var view = await fixture.CreateReservationService(db, clock, TestActor.Diner(dinerUserId))
            .CreateAsync(new CreateReservationCommand(
                branch.BranchId,
                branch.TableIds[0],
                new DateOnly(2026, 9, 13),
                new TimeOnly(20, 0),
                PartySize: 2,
                GuestName: "Ani Test",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7()));

        return view.Id;
    }

    /// <summary>A seated table with an open tab and a couple of events already on its stream.</summary>
    private async Task<(Guid TabId, Guid HostParticipantId)> TabWithOrdersAsync(
        TestBranch branch,
        TestMenu menu,
        IClock clock,
        Guid dinerUserId)
    {
        var tableId = branch.TableIds[2];

        await using (var seatDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(seatDb, clock, TestActor.Waiter(branch.WaiterId))
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));
        }

        Guid tabId;
        Guid hostId;

        await using (var openDb = fixture.CreateContext(clock))
        {
            var qr = await openDb.DiningTables
                .Where(t => t.Id == tableId).Select(t => t.QrToken).FirstAsync();

            var opened = await fixture.CreateTabService(openDb, clock, TestActor.Diner(dinerUserId))
                .OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));

            tabId = opened.Tab.TabId;
            hostId = opened.Tab.Me.ParticipantId;
        }

        await using (var orderDb = fixture.CreateContext(clock))
        {
            await fixture.CreateOrderService(orderDb, clock, TestActor.Participant(hostId))
                .PlaceOrderAsync(new PlaceOrderCommand(
                    tabId, [new OrderItemInput(menu.Wine, 1, IsShared: true)], Guid.CreateVersion7()));
        }

        return (tabId, hostId);
    }
}
