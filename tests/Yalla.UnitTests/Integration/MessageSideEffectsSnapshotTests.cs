using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted run of guest messages writes, through the real host, compared with
/// the record in <c>Snapshots/MessageSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of guest messages moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it. The script: a
/// guest's text from the app (and its replay, a reused id, a blank and an over-long text refused), the venue reading and
/// replying (and its replay, and another writer reusing the id), the guest reading, five texts in a minute and the sixth
/// slowed down; a takeaway order's thread (the kitchen refused, the counter replying); a branch where guests may not type
/// and waiters may not write (both refused, a manager writing); "not coming" from the app into a thread that exists, a
/// booking cancelled far ahead that gets none, and "not coming" from the manage link opening a thread; the venue writing
/// first to a booking with no account; "are you coming?" with and without an account to tell; "running late" from the
/// app and "on my way" from the manage link (and its replay); a reply that answers nothing; a closed thread refusing both
/// sides; read marks on an order's thread; the console's history; and the year's retention deleting a thread.
/// </para>
/// <para>
/// It snapshots every activity entry (codes, summaries, attribution, device labels), outbox row (the guest's
/// <c>message.to-guest</c> pushes, the <c>guestMessages</c> staff pushes, the bookings' and the order's own messages),
/// diner feed row, change-stream row, and the end state of the threads, their messages, the bookings and the order.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made; messages are named by thread and position) and every other id
/// as <c>&lt;guid&gt;</c>; instants as an offset from the start of the script; codes and phone numbers by name. Rows of one
/// table are sorted by their rendering - the feed too: a booking's save writes two entries, numbered in no promised order.
/// Regenerate on purpose only: <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~MessageSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class MessageSideEffectsSnapshotTests(SqlServerFixture fixture)
{
    private static readonly JsonSerializerOptions Render = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Columns that are the outbox loop's, or the database's, not the writer's.</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "rowVersion", "attemptCount", "lockedUntilUtc", "lockedBy", "sentAtUtc", "lastError",
        "deadLetteredAtUtc", "isSent", "isDeadLettered", "sequence", "manageTokenHash",
    };

    [SkippableFact]
    public async Task A_scripted_run_of_guest_messages_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // Nothing sends meanwhile: the outbox is read as it was written.
        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("Outbox:Enabled", "false")
            .StartingAtYerevan(new TimeOnly(15, 0));
        var t0 = factory.Clock.UtcNow;

        // The evening the bookings are for, two days on at 15:00: booked two days ahead, nothing waits for approval.
        var day = t0.AddDays(2);

        var (aniToken, aniPhone) = await SignInVerifiedDinerAsync(factory);
        var (bobToken, bobPhone) = await SignInVerifiedDinerAsync(factory);
        var (carlToken, carlPhone) = await SignInVerifiedDinerAsync(factory);
        var (evaToken, evaPhone) = await SignInVerifiedDinerAsync(factory);

        AuthBranch main;
        AuthBranch quiet;
        TestMenu menu;
        Guid cook;
        Guid dBooking;
        string dCode;
        long changesBefore;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            main = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            quiet = await AuthTestData.CreateBranchAsync(db, tableCount: 1);

            foreach (var branch in new[] { main, quiet })
            {
                await ReviewTestData.SavePolicyAsync(db, branch.BranchId, t0, acceptsOnlineBookings: true);
            }

            menu = await TestMenuBuilder.CreateAsync(db, main.BranchId);
            cook = await AuthTestData.SeedKitchenAsync(db, main);

            var takeaway = await db.Branches.FirstAsync(b => b.Id == main.BranchId);
            takeaway.SetTakeaway(true, false, 20, 30, 8);
            await db.SaveChangesAsync();

            // D: a booking the floor took by phone - no account behind it - at 19:00 that evening on the third table.
            var policy = await db.Branches.AsNoTracking().Where(b => b.Id == main.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
            var startUtc = day.AddHours(4);
            var localStart = Yalla.Domain.Venues.BranchTime.ToLocal(startUtc, "Asia/Yerevan");
            dCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var phoned = Reservation.Create(
                branchId: main.BranchId,
                diningTableId: main.TableIds[2],
                startUtc: startUtc,
                localDate: DateOnly.FromDateTime(localStart),
                localStartTime: TimeOnly.FromDateTime(localStart),
                partySize: 3,
                guestName: "Davit",
                guestPhone: "+37499000111",
                code: dCode,
                policy: policy);
            db.Reservations.Add(phoned);
            await db.SaveChangesAsync();
            dBooking = phoned.Id;

            changesBefore = await db.TableStateChanges
                .Where(c => c.BranchId == main.BranchId || c.BranchId == quiet.BranchId)
                .MaxAsync(c => (long?)c.Sequence) ?? 0L;
        }

        using var ani = factory.CreateClientWithToken(aniToken);
        using var bob = factory.CreateClientWithToken(bobToken);
        using var carl = factory.CreateClientWithToken(carlToken);
        using var eva = factory.CreateClientWithToken(evaToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, main));
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, main));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, main, cook));
        using var quietWaiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, quiet));
        using var quietManager = factory.CreateClientWithToken(await SignInManagerAsync(factory, quiet));
        using var link = factory.CreateClient();

        // The quiet branch keeps typing to its managers: guests answer only, waiters read only.
        await OkAsync(await quietManager.PutAsJsonAsync($"/api/branches/{quiet.BranchId}/message-settings", new
        {
            guestsCanWrite = false,
            waitersCanWrite = false,
            templates = new[] { "See you soon" },
            remindAfterMinutes = 5,
        }));

        // The bookings, two days on: A (Ani, 18:00), B (Bob, 18:30), E (Eva, the quiet branch, 18:00); C and C2 (Carl, the day after).
        var a = await CreatedAsync(ani, Booking(main, factory, 2, "18:00", 0, "Ani", aniPhone));
        var b = await CreatedAsync(bob, Booking(main, factory, 2, "18:30", 1, "Bob", bobPhone));
        var c = await CreatedAsync(carl, Booking(main, factory, 3, "18:00", 1, "Carl", carlPhone));
        var c2 = await CreatedAsync(carl, Booking(main, factory, 3, "20:00", 2, "Carl", carlPhone));
        var e = await CreatedAsync(eva, Booking(quiet, factory, 2, "18:00", 0, "Eva", evaPhone));
        var (aId, bId, cId, c2Id, eId) = (Id(a), Id(b), Id(c), Id(c2), Id(e));
        var aLink = a.GetProperty("manageToken").GetString()!;
        var bLink = b.GetProperty("manageToken").GetString()!;

        // T: Ani's takeaway order at the counter.
        var t = await OkAsync(await ani.PostAsJsonAsync("/api/diner/takeaway-orders", new
        {
            branchId = main.BranchId,
            pickup = new { asap = true },
            lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        var tId = Id(t);

        // ---- A, the guest writes from the app: a new thread. Her retry is the same message; the id reused for other
        // words is refused, and so are a blank and an over-long text.
        var aThread = $"/api/diner/reservations/{aId}/conversation";
        var aStaff = $"/api/reservations/{aId}/conversation";
        var first = Guid.CreateVersion7();
        await OkAsync(await ani.PostAsJsonAsync($"{aThread}/messages", new { text = "  We are four, one needs a high chair.  ", clientCommandId = first }));
        Assert.True((await OkAsync(await ani.PostAsJsonAsync($"{aThread}/messages", new { text = "We are four, one needs a high chair.", clientCommandId = first })))
            .GetProperty("wasReplay").GetBoolean());
        await ProblemAsync(
            await ani.PostAsJsonAsync($"{aThread}/messages", new { text = "Something else", clientCommandId = first }),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");
        await ProblemAsync(
            await ani.PostAsJsonAsync($"{aThread}/messages", new { text = "   ", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        await ProblemAsync(
            await ani.PostAsJsonAsync($"{aThread}/messages", new { text = new string('a', 501), clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        // The waiter reads it, and replies: the guest is told. The retry tells nobody twice; the manager reusing the
        // waiter's id is another writer.
        factory.Clock.UtcNow = t0.AddMinutes(1);
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsync($"{aStaff}/read", null)).StatusCode);

        factory.Clock.UtcNow = t0.AddMinutes(2);
        var replyCommand = Guid.CreateVersion7();
        var reply = await OkAsync(await waiter.PostAsJsonAsync($"{aStaff}/messages", new { text = "The chair will be at table 6.", clientCommandId = replyCommand }));
        Assert.True((await OkAsync(await waiter.PostAsJsonAsync($"{aStaff}/messages", new { text = "The chair will be at table 6.", clientCommandId = replyCommand })))
            .GetProperty("wasReplay").GetBoolean());
        await ProblemAsync(
            await manager.PostAsJsonAsync($"{aStaff}/messages", new { text = "The chair will be at table 6.", clientCommandId = replyCommand }),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        // Ani reads up to the reply.
        factory.Clock.UtcNow = t0.AddMinutes(3);
        Assert.Equal(HttpStatusCode.NoContent, (await ani.PostAsJsonAsync(
            $"{aThread}/read", new { upToUtc = reply.GetProperty("createdAtUtc").GetString() })).StatusCode);

        // Five texts in a minute, and the sixth is slowed down.
        for (var i = 0; i < 5; i++)
        {
            factory.Clock.UtcNow = t0.AddMinutes(4).AddSeconds(i * 5);
            await OkAsync(await ani.PostAsJsonAsync($"{aThread}/messages", new { text = $"And one more thing, {i + 1}.", clientCommandId = Guid.CreateVersion7() }));
        }

        factory.Clock.UtcNow = t0.AddMinutes(4).AddSeconds(25);
        await ProblemAsync(
            await ani.PostAsJsonAsync($"{aThread}/messages", new { text = "Too fast", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.TooManyRequests,
            "messages-too-fast");

        // ---- T, the takeaway order: the guest asks, the kitchen may not write, the counter answers a minute later.
        factory.Clock.UtcNow = t0.AddMinutes(5);
        await OkAsync(await ani.PostAsJsonAsync($"/api/diner/takeaway-orders/{tId}/conversation/messages", new { text = "Can I come ten minutes early?", clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await kitchen.PostAsJsonAsync($"/api/takeaway-orders/{tId}/conversation/messages", new { text = "Yes", clientCommandId = Guid.CreateVersion7() })).StatusCode);

        factory.Clock.UtcNow = t0.AddMinutes(6);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{tId}/conversation/messages", new { text = "Yes, it will be ready.", clientCommandId = Guid.CreateVersion7() }));

        // ---- E, the quiet branch: Eva may not type, the waiter may not write, the manager does.
        factory.Clock.UtcNow = t0.AddMinutes(7);
        await ProblemAsync(
            await eva.PostAsJsonAsync($"/api/diner/reservations/{eId}/conversation/messages", new { text = "Hello", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Forbidden,
            "guests-cannot-write");
        await ProblemAsync(
            await quietWaiter.PostAsJsonAsync($"/api/reservations/{eId}/conversation/messages", new { text = "Hello", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Forbidden,
            "waiters-cannot-write");
        await OkAsync(await quietManager.PostAsJsonAsync($"/api/reservations/{eId}/conversation/messages", new { text = "See you at six.", clientCommandId = Guid.CreateVersion7() }));

        // ---- C: Carl writes about his evening, then cancels - "not coming" lands in the thread that exists. C2, cancelled
        // with no thread a day ahead, gets none.
        factory.Clock.UtcNow = t0.AddMinutes(8);
        await OkAsync(await carl.PostAsJsonAsync($"/api/diner/reservations/{cId}/conversation/messages", new { text = "Can we come at seven?", clientCommandId = Guid.CreateVersion7() }));
        factory.Clock.UtcNow = t0.AddMinutes(9);
        await OkAsync(await carl.PostAsJsonAsync($"/api/reservations/{cId}/cancel", new { }));
        await OkAsync(await carl.PostAsJsonAsync($"/api/reservations/{c2Id}/cancel", new { }));

        // ---- D: the venue writes first, to a booking with no account - nobody to tell.
        factory.Clock.UtcNow = t0.AddMinutes(10);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{dBooking}/conversation/messages", new { text = "Your table is by the window.", clientCommandId = Guid.CreateVersion7() }));

        // ---- A at 17:00: "are you coming?"; Ani runs late from the app, then is on her way after all from the link (its
        // retry answers the same); the waiter's next reply answers nothing.
        factory.Clock.UtcNow = day.AddHours(2);
        Assert.True((await OkAsync(await waiter.PostAsync($"/api/reservations/{aId}/ask-on-my-way", null))).GetProperty("notified").GetBoolean());
        factory.Clock.UtcNow = day.AddHours(2).AddMinutes(1);
        await OkAsync(await ani.PostAsJsonAsync($"/api/reservations/{aId}/running-late", new { minutes = 15 }));
        factory.Clock.UtcNow = day.AddHours(2).AddMinutes(2);
        await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{aLink}/on-my-way", new { }));
        Assert.True((await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{aLink}/on-my-way", new { }))).GetProperty("wasReplay").GetBoolean());
        factory.Clock.UtcNow = day.AddHours(2).AddMinutes(3);
        await OkAsync(await waiter.PostAsJsonAsync($"{aStaff}/messages", new { text = "See you soon.", clientCommandId = Guid.CreateVersion7() }));

        // ---- B at 17:10: Bob can't come, from the manage link - inside the answer window, so the thread opens with it.
        factory.Clock.UtcNow = day.AddHours(2).AddMinutes(10);
        await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{bLink}/cancel", new { }));

        // ---- D at 18:00: "are you coming?" with no account to ask.
        factory.Clock.UtcNow = day.AddHours(3);
        Assert.False((await OkAsync(await waiter.PostAsync($"/api/reservations/{dBooking}/ask-on-my-way", null))).GetProperty("notified").GetBoolean());

        // ---- B closed two hours after the cancel: both sides are refused.
        factory.Clock.UtcNow = day.AddHours(4).AddMinutes(11);
        await ProblemAsync(
            await bob.PostAsJsonAsync($"/api/diner/reservations/{bId}/conversation/messages", new { text = "Sorry!", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "conversation-closed");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/reservations/{bId}/conversation/messages", new { text = "No worries.", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "conversation-closed");

        // ---- Reading: both sides of T's thread, the inbox, the console's history. Only the read marks write.
        factory.Clock.UtcNow = day.AddHours(4).AddMinutes(12);
        Assert.Equal(HttpStatusCode.NoContent, (await ani.PostAsync($"/api/diner/takeaway-orders/{tId}/conversation/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync($"/api/takeaway-orders/{tId}/conversation/read", new { })).StatusCode);
        await ReadAsync(waiter, $"/api/branches/{main.BranchId}/conversations");
        await ReadAsync(ani, aThread);
        await ReadAsync(waiter, aStaff);
        Assert.NotEmpty((await ReadAsync(manager, $"/api/branches/{main.BranchId}/conversations/history"))
            .GetProperty("items").EnumerateArray());

        var names = new Dictionary<Guid, string>
        {
            [main.VenueId] = "venue:main",
            [main.BranchId] = "branch:main",
            [main.WaiterId] = "staff:waiter",
            [main.ManagerId] = "staff:manager",
            [quiet.VenueId] = "venue:quiet",
            [quiet.BranchId] = "branch:quiet",
            [quiet.WaiterId] = "staff:quiet-waiter",
            [quiet.ManagerId] = "staff:quiet-manager",
            [cook] = "staff:cook",
            [main.TableIds[0]] = "table:main-1",
            [main.TableIds[1]] = "table:main-2",
            [main.TableIds[2]] = "table:main-3",
            [quiet.TableIds[0]] = "table:quiet-1",
            [aId] = "booking:A",
            [bId] = "booking:B",
            [cId] = "booking:C",
            [c2Id] = "booking:C2",
            [dBooking] = "booking:D",
            [eId] = "booking:E",
            [tId] = "takeaway:T",
            [menu.Khachapuri] = "menu:khachapuri",
            [menu.Coffee] = "menu:coffee",
        };

        var subjects = new Dictionary<Guid, string>
        {
            [aId] = "A",
            [bId] = "B",
            [cId] = "C",
            [c2Id] = "C2",
            [dBooking] = "D",
            [eId] = "E",
            [tId] = "T",
        };

        var text = new Dictionary<string, string>
        {
            [aniPhone] = "<ani phone>",
            [bobPhone] = "<bob phone>",
            [carlPhone] = "<carl phone>",
            [evaPhone] = "<eva phone>",
            [dCode] = "<code:D>",
            [t.GetProperty("code").GetString()!] = "<code:T>",
            [aLink] = "<link:A>",
            [bLink] = "<link:B>",
        };

        foreach (var (booking, letter) in new[] { (a, "A"), (b, "B"), (c, "C"), (c2, "C2"), (e, "E") })
        {
            text[booking.GetProperty("code").GetString()!] = $"<code:{letter}>";
        }

        Guid cThread;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            foreach (var (phone, who) in new[] { (aniPhone, "ani"), (bobPhone, "bob"), (carlPhone, "carl"), (evaPhone, "eva") })
            {
                names[await db.DinerUsers.Where(d => d.PhoneE164 == phone).Select(d => d.Id).SingleAsync()] = $"diner:{who}";
            }

            // Every thread and message by name - before the retention below takes C's away.
            var ids = subjects.Keys.ToList();
            var threads = await db.Conversations.AsNoTracking()
                .Where(x => (x.ReservationId != null && ids.Contains(x.ReservationId.Value))
                            || (x.TakeawayOrderId != null && ids.Contains(x.TakeawayOrderId.Value)))
                .ToListAsync();

            foreach (var thread in threads)
            {
                var letter = subjects[thread.ReservationId ?? thread.TakeawayOrderId!.Value];
                names[thread.Id] = $"thread:{letter}";

                var messages = await db.ConversationMessages.AsNoTracking()
                    .Where(m => m.ConversationId == thread.Id)
                    .OrderBy(m => m.CreatedAtUtc)
                    .ThenBy(m => m.Sequence)
                    .Select(m => m.Id)
                    .ToListAsync();

                for (var i = 0; i < messages.Count; i++)
                {
                    names[messages[i]] = $"msg:{letter}{i + 1}";
                }
            }

            Assert.Equal(["A", "B", "C", "D", "E", "T"], threads.Select(x => subjects[x.ReservationId ?? x.TakeawayOrderId!.Value]).Order().ToArray());
            cThread = threads.Single(x => x.ReservationId == cId).Id;

            // A year and a day on for C's thread: the retention deletes it, its messages with it.
            await db.Conversations
                .Where(x => x.Id == cThread)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastMessageUtc, factory.Clock.UtcNow.AddDays(-366)));
        }

        using (var scope = factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ConversationRetention>().PurgeAsync() >= 1);
        }

        var actual = await SnapshotAsync(factory, [main, quiet], subjects, changesBefore, names, text, t0);

        Assert.DoesNotContain("\"id\":\"thread:C\"", actual, StringComparison.Ordinal);
        await AssertMatchesRecordAsync(actual);
    }

    private static Guid Id(JsonElement created) => created.GetProperty("id").GetGuid();

    private static object Booking(AuthBranch branch, YallaApiFactory factory, int daysAhead, string time, int table, string name, string phone) => new
    {
        branchId = branch.BranchId,
        tableId = branch.TableIds[table],
        date = LocalDate(factory, daysAhead),
        time,
        partySize = 2,
        guestName = name,
        guestPhone = phone,
        clientCommandId = Guid.CreateVersion7(),
        channel = ReservationChannel.App,
    };

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch[] branches,
        Dictionary<Guid, string> subjects,
        long changesBefore,
        Dictionary<Guid, string> names,
        Dictionary<string, string> text,
        DateTime startedAt)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branchIds = branches.Select(x => x.BranchId).ToList();
        var subjectIds = subjects.Keys.ToList();
        var threads = await db.Conversations.AsNoTracking()
            .Where(x => (x.ReservationId != null && subjectIds.Contains(x.ReservationId.Value))
                        || (x.TakeawayOrderId != null && subjectIds.Contains(x.TakeawayOrderId.Value)))
            .ToListAsync();
        var threadIds = threads.Select(x => x.Id).ToList();
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => threadIds.Contains(m.ConversationId)).ToListAsync();
        var diners = names.Where(p => p.Value.StartsWith("diner:", StringComparison.Ordinal)).Select(p => p.Key).ToList();

        var activity = await db.ActivityEntries.AsNoTracking().Where(x => x.BranchId != null && branchIds.Contains(x.BranchId.Value)).ToListAsync();

        // The tablets and phones, named by who acted on them.
        foreach (var entry in activity.Where(x => x.DeviceId is not null))
        {
            var who = entry.StaffMemberId is { } staff && names.TryGetValue(staff, out var name) ? name[6..] : "?";
            names.TryAdd(entry.DeviceId!.Value, $"device:{who}");
        }

        // Anything about the bookings, the order, the threads, their messages or the guests.
        var keys = names
            .Where(p => p.Value.StartsWith("booking:", StringComparison.Ordinal)
                        || p.Value.StartsWith("takeaway:", StringComparison.Ordinal)
                        || p.Value.StartsWith("thread:", StringComparison.Ordinal)
                        || p.Value.StartsWith("msg:", StringComparison.Ordinal)
                        || p.Value.StartsWith("diner:", StringComparison.Ordinal))
            .Select(p => p.Key.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Sorted like the rest: a booking writes its reminder and its "on your way" entry in one save, and the database
        // numbers rows of one save in no promised order.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => diners.Contains(n.DinerUserId))
            .ToListAsync();
        var changes = await db.TableStateChanges.AsNoTracking()
            .Where(x => branchIds.Contains(x.BranchId) && x.Sequence > changesBefore)
            .ToListAsync();
        var reservations = await db.Reservations.AsNoTracking().Where(r => subjectIds.Contains(r.Id)).ToListAsync();
        var orders = await db.TakeawayOrders.AsNoTracking().Where(o => subjectIds.Contains(o.Id)).ToListAsync();

        // The builders suffix the venues' and the branches' names to keep them unique across tests.
        var places = await db.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .Select(x => new { x.Id, Branch = x.Name, Venue = x.Venue.Name })
            .ToListAsync();

        foreach (var place in places)
        {
            var which = place.Id == branches[0].BranchId ? "main" : "quiet";
            text[place.Venue] = $"<venue name:{which}>";
            text[place.Branch] = $"<branch name:{which}>";
        }

        string Normalised(string row)
        {
            row = Normalise(row, names, startedAt);

            foreach (var (from, to) in text.OrderByDescending(p => p.Key.Length))
            {
                row = row.Replace(from, to, StringComparison.Ordinal);
            }

            return row;
        }

        var snapshot = new StringBuilder();
        Section(snapshot, "ActivityEntries", activity, Normalised, sorted: true);
        Section(snapshot, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(snapshot, "DinerNotifications", feed, Normalised, sorted: true);
        Section(snapshot, "TableStateChanges", changes, Normalised, sorted: true);
        Section(snapshot, "Conversations", threads, Normalised, sorted: true);
        Section(snapshot, "ConversationMessages", messages, Normalised, sorted: true);
        Section(snapshot, "Reservations", reservations, Normalised, sorted: true);
        Section(snapshot, "TakeawayOrders", orders, Normalised, sorted: true);

        return snapshot.ToString();
    }

    /// <summary>
    /// One table: each row rendered, its ids and instants named, then its arrays sorted - after the naming, so the
    /// order does not depend on the random part of an id - and the rows sorted the same way unless their order is the
    /// contract.
    /// </summary>
    private static void Section<T>(StringBuilder text, string title, IEnumerable<T> rows, Func<string, string> normalise, bool sorted)
    {
        var rendered = rows
            .Select(row => SortArrays(JsonNode.Parse(normalise(RenderRow(row!))))!.ToJsonString(Render))
            .ToList();

        text.Append("## ").Append(title).Append(" (").Append(rendered.Count).Append(")\n");

        foreach (var row in sorted ? [.. rendered.Order(StringComparer.Ordinal)] : rendered)
        {
            text.Append(row).Append('\n');
        }

        text.Append('\n');
    }

    /// <summary>One row as JSON: every public property but the volatile ones and navigations, JSON columns inlined.</summary>
    private static string RenderRow(object row)
    {
        var node = JsonSerializer.SerializeToNode(row, row.GetType(), Render)!.AsObject();
        var result = new JsonObject();

        foreach (var (name, value) in node.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (Volatile.Contains(name) || value is JsonObject || value is JsonArray)
            {
                continue;
            }

            if (name.EndsWith("Json", StringComparison.Ordinal) && value is JsonValue json && json.TryGetValue<string>(out var raw))
            {
                result[name] = JsonNode.Parse(raw);
                continue;
            }

            result[name] = value?.DeepClone();
        }

        return result.ToJsonString(Render);
    }

    /// <summary>Sorts every array by its rendering: the set is the contract, not the sequence lines were read in.</summary>
    private static JsonNode? SortArrays(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    obj[key] = null;
                    obj[key] = SortArrays(child?.DeepClone());
                }

                return obj;

            case JsonArray array:
                var items = array.Select(i => SortArrays(i?.DeepClone())).OrderBy(i => i?.ToJsonString() ?? "null", StringComparer.Ordinal).ToList();
                var sorted = new JsonArray();

                foreach (var item in items)
                {
                    sorted.Add(item);
                }

                return sorted;

            default:
                return node;
        }
    }

    private static string Normalise(string text, Dictionary<Guid, string> names, DateTime startedAt)
    {
        text = Regex.Replace(
            text,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => names.TryGetValue(Guid.Parse(m.Value), out var name) ? name : "<guid>");

        // The fresh id a staff push key carries for an event that can happen again.
        text = Regex.Replace(text, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", "<n>");

        // The code the guest says at the counter: three random digits, unique per branch per day.
        text = Regex.Replace(text, @"\bT-\d{3}\b", "T-###");

        text = Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
            m =>
            {
                var at = DateTime.Parse(m.Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var offset = at - startedAt;

                // A status line is stamped a tick after the answer it follows: say so rather than round it away.
                return offset.Ticks % TimeSpan.TicksPerSecond == 0
                    ? $"T{offset.TotalSeconds:+0;-0;+0}s"
                    : $"T{offset.Ticks / TimeSpan.TicksPerSecond:+0;-0;+0}s+{offset.Ticks % TimeSpan.TicksPerSecond}t";
            });

        // A day on the branch's calendar as days from the script's own.
        var today = DateOnly.FromDateTime(Yalla.Domain.Venues.BranchTime.ToLocal(startedAt, "Asia/Yerevan"));

        return Regex.Replace(
            text,
            @"(?<![\d-])\d{4}-\d{2}-\d{2}(?![\dT])",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "MessageSideEffects.approved.txt");

        if (Environment.GetEnvironmentVariable("YALLA_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);

            return;
        }

        Assert.True(File.Exists(path), $"No snapshot on record at {path}. Write it with YALLA_UPDATE_SNAPSHOTS=1.");

        var expected = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");

        if (expected != actual)
        {
            await File.WriteAllTextAsync(path + ".received.txt", actual);
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// This file in the checkout the tests run from: the compile-time path when it is there, else found under the
    /// directory holding <c>Yalla.sln</c> above the test binaries (a CI shard may run binaries built elsewhere).
    /// </summary>
    private static string RepoFile(string compiledPath)
    {
        if (File.Exists(compiledPath))
        {
            return compiledPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Yalla.sln was not found above the test binaries."),
            "tests", "Yalla.UnitTests", "Integration", Path.GetFileName(compiledPath));
    }
}
