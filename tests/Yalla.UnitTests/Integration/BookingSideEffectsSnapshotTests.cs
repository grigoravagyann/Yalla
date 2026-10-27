using System.Globalization;
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
/// Characterisation: every side-effect row a scripted evening of bookings writes, through the real host, compared with
/// the record in <c>Snapshots/BookingSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of bookings moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it. The script, at
/// one branch from 15:00 to 21:00:
/// </para>
/// <list type="bullet">
/// <item><b>Making</b>: from the app (and its replay), from the public page (a manage link), with the staff channel, and
/// eight bookings for tonight, each at short notice and so waiting for the tablet; a guest with two no-shows behind them
/// waits for the venue too.</item>
/// <item><b>Deciding</b>: approved by the waiter, declined by the manager with a reason (and a second answer refused),
/// expired by the sweep (and a late approval refused).</item>
/// <item><b>Shares</b>: a later booking asking the guest before it in the app - a yes (and its replay) and a no - and one
/// the floor would ask, settled when the guest before cancels.</item>
/// <item><b>Changes</b>: a confirmed booking's party made smaller (and the replay); three confirmed bookings moved into
/// tonight and back to waiting - the venue declining one back to the slot it had, approving one, and declining one whose
/// slot another guest has taken since; a waiting booking moved again; a move from the manage link (and its replay); a
/// declined booking's move refused.</item>
/// <item><b>Cancellations</b>: by the guest in time, from the manage link (and again), late with a pre-order the kitchen
/// already had (and again, refused), and the venue letting a booking go.</item>
/// <item><b>The evening</b>: "running late", "are you coming?", "on my way"; a party seated and freed (they came); a
/// hold extended (twice, by replay, and refused); a no-show (and its replay); a third no-show that blocks the guest and
/// cancels their next booking, withdrawn so the block is lifted (and again); another block, lifted by the platform.</item>
/// </list>
/// <para>
/// It snapshots every activity entry (codes, summaries, attribution, device labels), outbox row (reminders, nudges and
/// questions, decisions, link notices, table requests, no-show and block notices, the <c>approvals</c> staff pushes),
/// diner feed row, change-stream row (signal 5 for bookings, 4 for a block's cancellations, 6 for pre-orders, the
/// tables' own), the platform's audit of the lift, and the end state of the bookings, the table requests, the blocks,
/// the pre-order and the booking threads' messages.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants - ISO, and
/// the compact stamps idempotency keys carry - as an offset from the start of the script; dates as days from the
/// branch's today; codes, phone numbers and manage links by name. Rows of one table are sorted by their rendering: rows of
/// one save are numbered in no promised order, and every row carries its instant. Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~BookingSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BookingSideEffectsSnapshotTests(SqlServerFixture fixture)
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

    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    [SkippableFact]
    public async Task A_scripted_evening_of_bookings_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // Nothing sends meanwhile: the outbox is read as it was written. The sweep is driven by hand.
        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("Outbox:Enabled", "false")
            .StartingAtYerevan(new TimeOnly(15, 0));
        var t0 = factory.Clock.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(t0, Yerevan));

        DateTime At(int days, string time) => TimeZoneInfo.ConvertTimeToUtc(
            today.AddDays(days).ToDateTime(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture)), Yerevan);

        // Every token is issued now, before the clock moves: a token stamped later than the host's wall clock is not
        // yet valid.
        var guests = new[]
        {
            "ani", "wes", "sam", "yan", "bob", "carl", "eva", "tara", "uma", "vik", "wil", "fred", "hana", "nora", "gor",
            "lia", "nadia", "dina", "kim",
        };
        var signedIn = new Dictionary<string, (string Token, string Phone)>();

        foreach (var guest in guests)
        {
            signedIn[guest] = await SignInVerifiedDinerAsync(factory);
        }

        AuthBranch main;
        AuthBranch elsewhereOne;
        AuthBranch elsewhereTwo;
        TestMenu menu;
        PlatformAdminAccount admin;
        var accounts = new Dictionary<string, Guid>();
        var history = new Dictionary<Guid, string>();
        long changesBefore;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            foreach (var guest in guests)
            {
                accounts[guest] = await db.DinerUsers.Where(d => d.PhoneE164 == signedIn[guest].Phone).Select(d => d.Id).SingleAsync();
            }

            main = await AuthTestData.CreateBranchAsync(db, tableCount: 11);
            elsewhereOne = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            elsewhereTwo = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            await ReviewTestData.SavePolicyAsync(db, main.BranchId, t0, acceptsOnlineBookings: true);
            menu = await TestMenuBuilder.CreateAsync(db, main.BranchId);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);

            // Fred's and Hana's history: a no-show at each of two other venues in the last three days, so tonight's is
            // the third. History, written straight to the rows: what happened then was told then.
            foreach (var guest in new[] { "fred", "hana" })
            {
                foreach (var (branch, hoursAgo) in new[] { (elsewhereOne, 50), (elsewhereTwo, 20) })
                {
                    var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
                    var startUtc = t0.AddHours(-hoursAgo);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, Yerevan);
                    var code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                    var missed = Reservation.Create(
                        branchId: branch.BranchId,
                        diningTableId: branch.TableIds[0],
                        startUtc: startUtc,
                        localDate: DateOnly.FromDateTime(local),
                        localStartTime: TimeOnly.FromDateTime(local),
                        partySize: 2,
                        guestName: guest,
                        guestPhone: signedIn[guest].Phone,
                        code: code,
                        policy: policy,
                        dinerUserId: accounts[guest],
                        bookerPhoneE164: signedIn[guest].Phone);

                    missed.MarkNoShow(startUtc.AddMinutes(30), branch.WaiterId, countsTowardBlock: true);
                    missed.DequeueEvents();
                    db.Reservations.Add(missed);
                    history[missed.Id] = code;
                }
            }

            await db.SaveChangesAsync();

            changesBefore = await db.TableStateChanges
                .Where(c => c.BranchId == main.BranchId)
                .MaxAsync(c => (long?)c.Sequence) ?? 0L;
        }

        var clients = guests.ToDictionary(g => g, g => factory.CreateClientWithToken(signedIn[g].Token));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, main));
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, main));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var link = factory.CreateClient();

        try
        {
            // Pre-orders on, for Lia's, which the kitchen will already have when she cancels.
            await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{main.BranchId}/pre-order-settings", new
            {
                acceptsPreOrders = true,
                minutesBefore = 30,
                excludedMenuItemIds = Array.Empty<Guid>(),
            }));

            // Tara and Vik have the app on their phones, so a later guest's request for their table is put to them.
            foreach (var guest in new[] { "tara", "vik" })
            {
                var device = await clients[guest].PostAsJsonAsync(
                    "/api/diner/devices", new { pushToken = $"ExponentPushToken[snapshot-{guest}]", platform = 1, locale = "en" });
                Assert.True(device.IsSuccessStatusCode, await device.Content.ReadAsStringAsync());
            }

            var bookings = new Dictionary<string, JsonElement>();

            async Task<Guid> BookAsync(string name, string guest, int days, string time, int table, ReservationChannel channel, int partySize = 2)
            {
                // The public page books through its own route since 2026-10-27; the app's route records App whatever a body
                // names (docs/branch-features.md), so a Staff channel is no longer anybody's to claim from a diner session.
                var created = channel == ReservationChannel.Web
                    ? await PageCreatedAsync(clients[guest], main, Booking(main, factory, guest, signedIn[guest].Phone, days, time, table, channel, partySize))
                    : await CreatedAsync(clients[guest], Booking(main, factory, guest, signedIn[guest].Phone, days, time, table, channel, partySize));
                bookings[name] = created;

                return created.GetProperty("id").GetGuid();
            }

            // ---- 15:00, making. A from the app, and the app's retry answers as the booking.
            var aBody = Booking(main, factory, "ani", signedIn["ani"].Phone, 2, "18:00", 0, ReservationChannel.App);
            bookings["A"] = await CreatedAsync(clients["ani"], aBody);
            var a = Id(bookings["A"]);
            Assert.True((await OkAsync(await clients["ani"].PostAsJsonAsync("/api/reservations", aBody))).GetProperty("wasReplay").GetBoolean());

            var w = await BookAsync("W", "wes", 2, "18:30", 1, ReservationChannel.Web);
            var s = await BookAsync("S", "sam", 3, "18:00", 0, ReservationChannel.Staff, partySize: 3);
            var b = await BookAsync("B", "bob", 2, "20:00", 3, ReservationChannel.App);
            var e = await BookAsync("E", "eva", 2, "18:00", 4, ReservationChannel.App);
            var t = await BookAsync("T", "tara", 2, "18:00", 2, ReservationChannel.App);
            var v = await BookAsync("V", "vik", 2, "18:00", 5, ReservationChannel.App);
            var f2 = await BookAsync("F2", "fred", 4, "19:00", 6, ReservationChannel.App);
            var h2 = await BookAsync("H2", "hana", 5, "18:00", 7, ReservationChannel.App);
            var q = await BookAsync("Q", "gor", 3, "18:00", 8, ReservationChannel.App);

            // Tonight: every one at short notice, waiting fifteen minutes for the tablet.
            factory.Clock.UtcNow = t0.AddSeconds(10);
            var g = await BookAsync("G", "gor", 0, "18:00", 1, ReservationChannel.App);
            var n = await BookAsync("N", "nora", 0, "19:00", 2, ReservationChannel.App);
            var f1 = await BookAsync("F1", "fred", 0, "19:00", 5, ReservationChannel.App);
            var h1 = await BookAsync("H1", "hana", 0, "19:15", 0, ReservationChannel.App);
            var l = await BookAsync("L", "lia", 0, "19:30", 3, ReservationChannel.App);
            var n2 = await BookAsync("N2", "nadia", 0, "20:30", 6, ReservationChannel.App);
            var n3 = await BookAsync("N3", "dina", 0, "21:00", 7, ReservationChannel.App);
            var k = await BookAsync("K", "kim", 0, "18:30", 8, ReservationChannel.App);

            // Lia's pre-order, while it can still be edited.
            factory.Clock.UtcNow = t0.AddSeconds(30);
            await OkAsync(await clients["lia"].PutAsJsonAsync($"/api/reservations/{l}/pre-order", new
            {
                lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } },
                clientCommandId = Guid.CreateVersion7(),
            }));

            // ---- 15:01, deciding: the waiter says yes to six; the manager no to Nadia, with a reason - and a yes after
            // that is refused.
            factory.Clock.UtcNow = t0.AddMinutes(1);

            foreach (var yes in new[] { g, n, f1, h1, l, k })
            {
                await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{yes}/approve", new { }));
            }

            await OkAsync(await manager.PostAsJsonAsync($"/api/reservations/{n2}/reject", new { reason = "  A private party has the room  " }));
            await StatusAsync(await waiter.PostAsJsonAsync($"/api/reservations/{n2}/approve", new { }), HttpStatusCode.Conflict);

            // ---- 15:02, Sam's party is one smaller: the same slot, still confirmed - and the retry is the same change.
            factory.Clock.UtcNow = t0.AddMinutes(2);
            var smaller = Move(main, factory, 3, "18:00", 0, partySize: 2);
            await OkAsync(await clients["sam"].PostAsJsonAsync($"/api/reservations/{s}/reschedule", smaller));
            Assert.True((await OkAsync(await clients["sam"].PostAsJsonAsync($"/api/reservations/{s}/reschedule", smaller)))
                .GetProperty("wasReplay").GetBoolean());

            // ---- 15:03, shares. Yan wants Sam's table after him - Sam booked with no app, so the floor would ask; Uma
            // wants Tara's and Wil wants Vik's, and both are asked in the app.
            factory.Clock.UtcNow = t0.AddMinutes(3);
            var y = await BookAsync("Y", "yan", 3, "19:45", 0, ReservationChannel.App);
            var u = await BookAsync("U", "uma", 2, "19:45", 2, ReservationChannel.App);
            var wl = await BookAsync("WL", "wil", 2, "19:45", 5, ReservationChannel.App);
            var r = await BookAsync("R", "kim", 3, "19:45", 8, ReservationChannel.App);

            var tRequest = await IncomingRequestAsync(clients["tara"], t);
            var vRequest = await IncomingRequestAsync(clients["vik"], v);

            // ---- 15:04, Tara says yes (and her retry is the same answer); Vik says no.
            factory.Clock.UtcNow = t0.AddMinutes(4);
            var yesBody = new { accept = true, clientCommandId = Guid.CreateVersion7() };
            await OkAsync(await clients["tara"].PostAsJsonAsync($"/api/reservations/{t}/table-requests/{tRequest}/answer", yesBody));
            await OkAsync(await clients["tara"].PostAsJsonAsync($"/api/reservations/{t}/table-requests/{tRequest}/answer", yesBody));
            await OkAsync(await clients["vik"].PostAsJsonAsync(
                $"/api/reservations/{v}/table-requests/{vRequest}/answer", new { accept = false, clientCommandId = Guid.CreateVersion7() }));

            // Gor has no app to be asked on, so the floor asks him for Kim, and says yes for him.
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{r}/approve", new { }));

            // ---- 15:05, changes into tonight, back to waiting: Ani, Eva, Bob. Dina moves her waiting booking later.
            factory.Clock.UtcNow = t0.AddMinutes(5);
            await OkAsync(await clients["ani"].PostAsJsonAsync($"/api/reservations/{a}/reschedule", Move(main, factory, 0, "21:30", 10)));
            await OkAsync(await clients["eva"].PostAsJsonAsync($"/api/reservations/{e}/reschedule", Move(main, factory, 0, "20:00", 4)));
            await OkAsync(await clients["bob"].PostAsJsonAsync($"/api/reservations/{b}/reschedule", Move(main, factory, 0, "21:00", 9)));
            await OkAsync(await clients["dina"].PostAsJsonAsync($"/api/reservations/{n3}/reschedule", Move(main, factory, 0, "21:30", 7)));

            // ---- 15:06, Carl takes the slot Bob left.
            factory.Clock.UtcNow = t0.AddMinutes(6);
            var c = await BookAsync("C", "carl", 2, "20:00", 3, ReservationChannel.App);

            // ---- 15:07, the venue answers the changes: Ani back to the slot she had, Eva yes, Bob no - his slot is gone.
            factory.Clock.UtcNow = t0.AddMinutes(7);
            await OkAsync(await manager.PostAsJsonAsync($"/api/reservations/{a}/reject", new { reason = "Fully booked tonight" }));
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{e}/approve", new { }));
            await OkAsync(await manager.PostAsJsonAsync($"/api/reservations/{b}/reject", new { }));

            // ---- 15:08, Wes moves his booking from its manage link (and the link's retry is the same move); Nadia's
            // declined booking does not move.
            factory.Clock.UtcNow = t0.AddMinutes(8);
            var wLink = bookings["W"].GetProperty("manageToken").GetString()!;
            var later = Move(main, factory, 2, "19:30", 1);
            await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{wLink}/reschedule", later));
            await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{wLink}/reschedule", later));
            await StatusAsync(
                await clients["nadia"].PostAsJsonAsync($"/api/reservations/{n2}/reschedule", Move(main, factory, 0, "21:00", 6)),
                HttpStatusCode.Conflict);

            // ---- 15:09, Eva would like Ani's table after her, and the floor would ask Ani; Carl moves into tonight.
            factory.Clock.UtcNow = t0.AddMinutes(9);
            var e2 = await BookAsync("E2", "eva", 2, "19:45", 0, ReservationChannel.App);
            await OkAsync(await clients["carl"].PostAsJsonAsync($"/api/reservations/{c}/reschedule", Move(main, factory, 0, "21:30", 10)));

            // ---- 15:10, Ani moves her booking later on the same table, leaving Eva's no room: Eva's is declined.
            factory.Clock.UtcNow = t0.AddMinutes(10);
            await OkAsync(await clients["ani"].PostAsJsonAsync($"/api/reservations/{a}/reschedule", Move(main, factory, 2, "19:00", 0)));

            // ---- 15:16, nobody answered Dina: the sweep declines her, and a yes after it is refused.
            factory.Clock.UtcNow = t0.AddMinutes(16);

            using (var scope = factory.Services.CreateScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<ApprovalExpirySweep>().RunOnceAsync() >= 1);
            }

            await StatusAsync(await waiter.PostAsJsonAsync($"/api/reservations/{n3}/approve", new { }), HttpStatusCode.Conflict);

            // ---- 15:30, nobody answered Carl's change either: the sweep sends him back to the slot he had.
            factory.Clock.UtcNow = t0.AddMinutes(30);

            using (var scope = factory.Services.CreateScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<ApprovalExpirySweep>().RunOnceAsync() >= 1);
            }

            // ---- 16:00, Sam cancels in time: the question for his table goes, and Yan is confirmed.
            factory.Clock.UtcNow = At(0, "16:00");
            await OkAsync(await clients["sam"].PostAsJsonAsync($"/api/reservations/{s}/cancel", new { reason = "Change of plans" }));

            // ---- 16:30, Wes cancels from the link; again, and it is as it stands.
            factory.Clock.UtcNow = At(0, "16:30");
            await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{wLink}/cancel", new { }));
            await OkAsync(await link.PostAsJsonAsync($"/api/public/bookings/{wLink}/cancel", new { }));

            // ---- 17:00, Gor is running late; the waiter asks Kim whether she is coming.
            factory.Clock.UtcNow = At(0, "17:00");
            await OkAsync(await clients["gor"].PostAsJsonAsync($"/api/reservations/{g}/running-late", new { minutes = 15 }));
            await OkAsync(await waiter.PostAsync($"/api/reservations/{k}/ask-on-my-way", null));

            // ---- 18:00, Lia is on her way.
            factory.Clock.UtcNow = At(0, "18:00");
            await OkAsync(await clients["lia"].PostAsJsonAsync($"/api/reservations/{l}/on-my-way", new { }));

            // ---- 18:10, Gor's party sits down.
            factory.Clock.UtcNow = At(0, "18:10");
            await OkAsync(await waiter.PostAsJsonAsync(
                $"/api/branches/{main.BranchId}/tables/{main.TableIds[1]}/seat-reservation",
                new { reservationId = g, clientCommandId = Guid.CreateVersion7() }));

            // ---- 18:45, the venue lets Kim's booking go.
            factory.Clock.UtcNow = At(0, "18:45");
            await OkAsync(await waiter.PostAsJsonAsync(
                $"/api/reservations/{k}/release", new { outcome = 2, clientCommandId = Guid.CreateVersion7(), reason = "Kitchen fire" }));

            // ---- 19:00, Gor's party leaves: they came.
            factory.Clock.UtcNow = At(0, "19:00");
            await OkAsync(await waiter.PostAsJsonAsync(
                $"/api/branches/{main.BranchId}/tables/{main.TableIds[1]}/free", new { clientCommandId = Guid.CreateVersion7() }));

            // ---- 19:05, Nora is five minutes away: her one extension (the retry is the same; another is refused). The
            // kitchen gets Lia's pre-order.
            factory.Clock.UtcNow = At(0, "19:05");
            var extend = new { clientCommandId = Guid.CreateVersion7() };
            await OkAsync(await clients["nora"].PostAsJsonAsync($"/api/reservations/{n}/extend-hold", extend));
            await OkAsync(await clients["nora"].PostAsJsonAsync($"/api/reservations/{n}/extend-hold", extend));
            await StatusAsync(
                await clients["nora"].PostAsJsonAsync($"/api/reservations/{n}/extend-hold", new { clientCommandId = Guid.CreateVersion7() }),
                HttpStatusCode.Conflict);
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{l}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

            // ---- 19:10, Lia cancels, late, with her food already in the kitchen; again, and it is refused.
            factory.Clock.UtcNow = At(0, "19:10");
            await OkAsync(await clients["lia"].PostAsJsonAsync($"/api/reservations/{l}/cancel", new { }));
            await StatusAsync(await clients["lia"].PostAsJsonAsync($"/api/reservations/{l}/cancel", new { }), HttpStatusCode.Conflict);

            // ---- 19:30, nobody came for Nora (the tablet's retry is the same answer), nor for Fred - his third in three
            // days, which blocks him and cancels his booking on Saturday.
            factory.Clock.UtcNow = At(0, "19:30");
            var noShow = new { clientCommandId = Guid.CreateVersion7() };
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{n}/no-show", noShow));
            Assert.True((await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{n}/no-show", noShow))).GetProperty("wasReplay").GetBoolean());
            Assert.True((await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{f1}/no-show", new { clientCommandId = Guid.CreateVersion7() })))
                .TryGetProperty("guestBlockedUntilUtc", out _));

            // ---- 19:40, nor for Hana: blocked too.
            factory.Clock.UtcNow = At(0, "19:40");
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{h1}/no-show", new { clientCommandId = Guid.CreateVersion7() }));

            // ---- 20:00, Fred was at the bar all along: the no-show is withdrawn and his block lifted (again, and it is as
            // it stands). Eva's party sits down.
            factory.Clock.UtcNow = At(0, "20:00");
            await OkAsync(await manager.PostAsJsonAsync(
                $"/api/reservations/{f1}/undo-no-show", new { clientCommandId = Guid.CreateVersion7(), reason = "He was at the bar" }));
            await OkAsync(await manager.PostAsJsonAsync($"/api/reservations/{f1}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() }));
            await OkAsync(await waiter.PostAsJsonAsync(
                $"/api/branches/{main.BranchId}/tables/{main.TableIds[4]}/seat-reservation",
                new { reservationId = e, clientCommandId = Guid.CreateVersion7() }));

            // ---- 20:10, the platform lifts Hana's block.
            factory.Clock.UtcNow = At(0, "20:10");
            Guid hanaBlock;

            await using (var db = fixture.CreateContext(factory.Clock))
            {
                hanaBlock = await db.BookingBlocks.Where(x => x.DinerUserId == accounts["hana"]).Select(x => x.Id).SingleAsync();
            }

            await OkAsync(await platform.PostAsJsonAsync(
                $"/api/platform/booking-blocks/{hanaBlock}/lift", new { reason = "The venue confirmed she came" }));

            // ---- 21:00, Eva's party leaves.
            factory.Clock.UtcNow = At(0, "21:00");
            await OkAsync(await waiter.PostAsJsonAsync(
                $"/api/branches/{main.BranchId}/tables/{main.TableIds[4]}/free", new { clientCommandId = Guid.CreateVersion7() }));

            // ---- The record.
            var names = new Dictionary<Guid, string>
            {
                [main.VenueId] = "venue:main",
                [main.BranchId] = "branch:main",
                [main.WaiterId] = "staff:waiter",
                [main.ManagerId] = "staff:manager",
                [elsewhereOne.VenueId] = "venue:one",
                [elsewhereOne.BranchId] = "branch:one",
                [elsewhereOne.WaiterId] = "staff:one-waiter",
                [elsewhereOne.TableIds[0]] = "table:one-1",
                [elsewhereTwo.VenueId] = "venue:two",
                [elsewhereTwo.BranchId] = "branch:two",
                [elsewhereTwo.WaiterId] = "staff:two-waiter",
                [elsewhereTwo.TableIds[0]] = "table:two-1",
                [admin.StaffMemberId] = "staff:platform",
                [menu.Khachapuri] = "menu:khachapuri",
                [menu.Coffee] = "menu:coffee",
                [tRequest] = "request:U",
                [vRequest] = "request:WL",
            };

            for (var i = 0; i < main.TableIds.Count; i++)
            {
                names[main.TableIds[i]] = $"table:{i + 1}";
            }

            foreach (var (guest, account) in accounts)
            {
                names[account] = $"diner:{guest}";
            }

            foreach (var (name, booking) in bookings)
            {
                names[Id(booking)] = $"booking:{name}";
            }

            var historyNumber = 0;

            foreach (var id in history.Keys)
            {
                names[id] = $"booking:history-{++historyNumber}";
            }

            var text = new Dictionary<string, string>();

            foreach (var guest in guests)
            {
                text[signedIn[guest].Phone] = $"<{guest} phone>";
            }

            foreach (var (name, booking) in bookings)
            {
                text[booking.GetProperty("code").GetString()!] = $"<code:{name}>";
            }

            foreach (var (id, code) in history)
            {
                text[code] = $"<code:{names[id][8..]}>";
            }

            text[wLink] = "<link:W>";
            text[Uri.EscapeDataString(wLink)] = "<link:W>";

            var actual = await SnapshotAsync(factory, main, accounts, bookings, changesBefore, names, text, t0);

            await AssertMatchesRecordAsync(actual);
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }
    }

    private static Guid Id(JsonElement created) => created.GetProperty("id").GetGuid();

    /// <summary>A booking from the public page's own route: the app's body without the branch and the channel.</summary>
    private static async Task<JsonElement> PageCreatedAsync(HttpClient diner, AuthBranch branch, object appBody)
    {
        var body = JsonSerializer.SerializeToNode(appBody)!.AsObject();
        body.Remove("branchId");
        body.Remove("channel");

        var response = await diner.PostAsJsonAsync($"/api/public/branches/{branch.BranchId}/reservations", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Booking(
        AuthBranch branch,
        YallaApiFactory factory,
        string guest,
        string phone,
        int daysAhead,
        string time,
        int table,
        ReservationChannel channel,
        int partySize = 2) => new
        {
            branchId = branch.BranchId,
            tableId = branch.TableIds[table],
            date = LocalDate(factory, daysAhead),
            time,
            partySize,
            guestName = char.ToUpperInvariant(guest[0]) + guest[1..],
            guestPhone = phone,
            clientCommandId = Guid.CreateVersion7(),
            channel,
        };

    private static object Move(AuthBranch branch, YallaApiFactory factory, int daysAhead, string time, int table, int partySize = 2) => new
    {
        date = LocalDate(factory, daysAhead),
        time,
        partySize,
        diningTableId = branch.TableIds[table],
        clientCommandId = Guid.CreateVersion7(),
    };

    /// <summary>The request for this booking's table its guest is shown on their booking.</summary>
    private static async Task<Guid> IncomingRequestAsync(HttpClient guest, Guid reservationId)
    {
        var mine = await ReadAsync(guest, "/api/reservations/mine");

        return mine.GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == reservationId)
            .GetProperty("incomingTableRequest").GetProperty("requestId").GetGuid();
    }

    private static async Task StatusAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch main,
        Dictionary<string, Guid> accounts,
        Dictionary<string, JsonElement> bookings,
        long changesBefore,
        Dictionary<Guid, string> names,
        Dictionary<string, string> text,
        DateTime startedAt)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var bookingIds = bookings.Values.Select(Id).ToList();
        var diners = accounts.Values.ToList();

        var blocks = await db.BookingBlocks.AsNoTracking().Where(x => diners.Contains(x.DinerUserId)).ToListAsync();

        foreach (var block in blocks)
        {
            names[block.Id] = $"block:{names[block.DinerUserId][6..]}";
        }

        var requests = await db.TableShareRequests.AsNoTracking()
            .Where(x => bookingIds.Contains(x.LaterReservationId) || bookingIds.Contains(x.EarlierReservationId))
            .ToListAsync();

        foreach (var request in requests)
        {
            names.TryAdd(request.Id, $"request:{names[request.LaterReservationId][8..]}");
        }

        var preOrders = await db.PreOrders.AsNoTracking().Where(p => bookingIds.Contains(p.ReservationId)).ToListAsync();

        foreach (var preOrder in preOrders)
        {
            names[preOrder.Id] = $"pre-order:{names[preOrder.ReservationId][8..]}";
        }

        var activity = await db.ActivityEntries.AsNoTracking().Where(x => x.BranchId == main.BranchId).ToListAsync();

        // The tablets and phones, named by who acted on them.
        foreach (var entry in activity.Where(x => x.DeviceId is not null))
        {
            var who = entry.StaffMemberId is { } staff && names.TryGetValue(staff, out var name) ? name[6..] : "?";
            names.TryAdd(entry.DeviceId!.Value, $"device:{who}");
        }

        // Anything about the bookings, the requests, the blocks or the guests.
        var keys = bookingIds
            .Concat(requests.Select(x => x.Id))
            .Concat(blocks.Select(x => x.Id))
            .Concat(diners)
            .Select(id => id.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(key => m.IdempotencyKey.Contains(key, StringComparison.OrdinalIgnoreCase)
                                        || m.PayloadJson.Contains(key, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var feed = await db.DinerNotifications.AsNoTracking().Where(x => diners.Contains(x.DinerUserId)).ToListAsync();
        var changes = await db.TableStateChanges.AsNoTracking()
            .Where(x => x.BranchId == main.BranchId && x.Sequence > changesBefore)
            .ToListAsync();
        var blockIds = blocks.Select(x => x.Id).ToList();
        var audit = await db.PlatformAuditLogs.AsNoTracking().Where(x => blockIds.Contains(x.TargetId)).ToListAsync();
        var reservations = await db.Reservations.AsNoTracking().Where(r => bookingIds.Contains(r.Id)).ToListAsync();
        var threads = await db.Conversations.AsNoTracking()
            .Where(x => x.ReservationId != null && bookingIds.Contains(x.ReservationId.Value))
            .ToListAsync();

        foreach (var thread in threads)
        {
            names[thread.Id] = $"thread:{names[thread.ReservationId!.Value][8..]}";
        }

        var threadIds = threads.Select(x => x.Id).ToList();
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => threadIds.Contains(m.ConversationId)).ToListAsync();

        // The builders suffix the venues' and the branches' names to keep them unique across tests.
        var places = await db.Branches.AsNoTracking()
            .Where(x => x.Id == main.BranchId)
            .Select(x => new { Branch = x.Name, Venue = x.Venue.Name })
            .SingleAsync();

        text[places.Venue] = "<venue name>";
        text[places.Branch] = "<branch name>";

        string Normalised(string row)
        {
            row = NameIds(row, names);

            foreach (var (from, to) in text.OrderByDescending(p => p.Key.Length))
            {
                row = row.Replace(from, to, StringComparison.Ordinal);
            }

            return Instants(row, startedAt);
        }

        var snapshot = new StringBuilder();
        Section(snapshot, "ActivityEntries", activity, Normalised);
        Section(snapshot, "OutboxMessages", outbox, Normalised);
        Section(snapshot, "DinerNotifications", feed, Normalised);
        Section(snapshot, "TableStateChanges", changes, Normalised);
        Section(snapshot, "PlatformAuditLogs", audit, Normalised);
        Section(snapshot, "Reservations", reservations, Normalised);
        Section(snapshot, "TableShareRequests", requests, Normalised);
        Section(snapshot, "BookingBlocks", blocks, Normalised);
        Section(snapshot, "PreOrders", preOrders, Normalised);
        Section(snapshot, "ConversationMessages", messages, Normalised);

        return snapshot.ToString();
    }

    /// <summary>
    /// One table: each row rendered, its ids and instants named, then its arrays sorted - after the naming, so the order
    /// does not depend on the random part of an id - and the rows sorted the same way.
    /// </summary>
    private static void Section<T>(StringBuilder text, string title, IEnumerable<T> rows, Func<string, string> normalise)
    {
        var rendered = rows
            .Select(row => SortArrays(JsonNode.Parse(normalise(RenderRow(row!))))!.ToJsonString(Render))
            .Order(StringComparer.Ordinal)
            .ToList();

        text.Append("## ").Append(title).Append(" (").Append(rendered.Count).Append(")\n");

        foreach (var row in rendered)
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

    private static string NameIds(string text, Dictionary<Guid, string> names)
    {
        text = Regex.Replace(
            text,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => names.TryGetValue(Guid.Parse(m.Value), out var name) ? name : "<guid>");

        // The fresh id a staff push key - or a table request's push - carries.
        return Regex.Replace(text, "(?<![0-9a-zA-Z])[0-9a-f]{32}(?![0-9a-zA-Z])", m => names.TryGetValue(Guid.ParseExact(m.Value, "N"), out var name) ? name : "<n>");
    }

    private static string Instants(string text, DateTime startedAt)
    {
        string Offset(DateTime at)
        {
            var offset = at - startedAt;

            // A status line is stamped a tick after the answer it follows: say so rather than round it away.
            return offset.Ticks % TimeSpan.TicksPerSecond == 0
                ? $"T{offset.TotalSeconds:+0;-0;+0}s"
                : $"T{offset.Ticks / TimeSpan.TicksPerSecond:+0;-0;+0}s+{offset.Ticks % TimeSpan.TicksPerSecond}t";
        }

        text = Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
            m => Offset(DateTime.Parse(m.Value, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)));

        // The compact stamps in idempotency keys: an instant to the millisecond, or a booking's start to the minute.
        text = Regex.Replace(
            text,
            @"(?<!\d)\d{17}(?!\d)",
            m => Offset(DateTime.SpecifyKind(DateTime.ParseExact(m.Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture), DateTimeKind.Utc)));
        text = Regex.Replace(
            text,
            @"(?<!\d)\d{12}(?!\d)",
            m => Offset(DateTime.SpecifyKind(DateTime.ParseExact(m.Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture), DateTimeKind.Utc)));

        // A day on the branch's calendar as days from the script's own.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startedAt, Yerevan));

        return Regex.Replace(
            text,
            @"(?<![\d-])\d{4}-\d{2}-\d{2}(?![\dT])",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "BookingSideEffects.approved.txt");

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
