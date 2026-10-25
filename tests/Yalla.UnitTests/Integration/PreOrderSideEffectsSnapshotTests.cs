using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted evening of pre-orders writes, through the real host, compared with
/// the record in <c>Snapshots/PreOrderSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of pre-orders moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it. The script: a
/// pre-order placed with its booking, its replay, edited from the manage link, a send refused two days ahead, the booking
/// moved with its pre-order and then cleared from the link, the edit refused inside the two-hour lock; the console's settings saved with cards on the
/// rail; "are you coming?" with and without an account; the guest's "on my way" from the app and the link, "running
/// late"; a clear of a booking with no pre-order; the rail's "not coming" and its replay, sends of held tickets and the
/// replay, the kitchen moving a held ticket; a booking with a held ticket cancelled and one with an unsent pre-order
/// cancelled; two seated parties sent to, one onto the tab the waiter opened and one onto a tab the send opens; and two
/// parties arriving with held tickets, seated by the waiter and opening by their booking code, so the held ticket
/// becomes the order on their tab.
/// </para>
/// <para>
/// It snapshots every answer, activity entry, outbox row (the questions, the staff pushes, the bookings' own messages),
/// diner feed row, the change stream in its sequence (signal 6 rows among them), each tab's stream in its sequence,
/// the stored commands, and the end state of the pre-orders, their lines, the bookings, sittings, tabs, orders, lines and
/// the booking threads.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script, local dates as days from the branch's today; codes, links and phone numbers by
/// name. Rows of one table are sorted by their rendering, except the change stream, which reads in its sequence (rows of
/// one signal written by one save, which the database numbers in no promised order, sorted among themselves), and each
/// tab's stream in its. Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~PreOrderSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PreOrderSideEffectsSnapshotTests(SqlServerFixture fixture)
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

    private readonly List<(string Label, string Json)> _answers = [];

    private readonly Dictionary<Guid, string> _names = [];

    private readonly Dictionary<string, string> _text = [];

    [SkippableFact]
    public async Task A_scripted_evening_of_pre_orders_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // Nothing sends meanwhile: the outbox is read as it was written.
        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("Outbox:Enabled", "false")
            .StartingAtYerevan(new TimeOnly(15, 0));
        var t0 = factory.Clock.UtcNow;

        // The evening the bookings are for, two days on: 18:00 there is three hours after this instant's local 15:00.
        var day = t0.AddDays(2);
        var start = day.AddHours(3);

        var who = new[] { "ani", "bob", "eva", "fay", "gor", "hov", "ina", "jan" };
        var diners = new Dictionary<string, (string Token, string Phone)>();

        foreach (var name in who)
        {
            diners[name] = await SignInVerifiedDinerAsync(factory);
        }

        AuthBranch branch;
        TestMenu menu;
        Guid dId;
        string dCode;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 11);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, t0, acceptsOnlineBookings: true);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            // D: a booking the floor took by phone - no account behind it, no pre-order - at 18:00 on the ninth table.
            var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
            var localStart = Yalla.Domain.Venues.BranchTime.ToLocal(start, "Asia/Yerevan");
            dCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var phoned = Reservation.Create(
                branchId: branch.BranchId,
                diningTableId: branch.TableIds[8],
                startUtc: start,
                localDate: DateOnly.FromDateTime(localStart),
                localStartTime: TimeOnly.FromDateTime(localStart),
                partySize: 3,
                guestName: "Davit",
                guestPhone: "+37499000111",
                code: dCode,
                policy: policy);
            db.Reservations.Add(phoned);
            await db.SaveChangesAsync();
            dId = phoned.Id;
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var link = factory.CreateClient();
        var clients = who.ToDictionary(n => n, n => factory.CreateClientWithToken(diners[n].Token));

        try
        {
            // The branch takes pre-orders, cards 30 minutes ahead.
            await SendAsync("settings:on", manager, HttpMethod.Put, $"/api/branches/{branch.BranchId}/pre-order-settings", new
            {
                acceptsPreOrders = true,
                minutesBefore = 30,
                excludedMenuItemIds = Array.Empty<Guid>(),
            });

            // The bookings, all two days on at 18:00, one table each: A Ani, B Bob, E Eva (no pre-order), F Fay, G Gor,
            // H Hov, I Ina, J Jan.
            var letters = new[] { "A", "B", "E", "F", "G", "H", "I", "J" };
            var bookings = new Dictionary<string, JsonElement>();

            for (var i = 0; i < letters.Length; i++)
            {
                var booking = await CreatedAsync(clients[who[i]], new
                {
                    branchId = branch.BranchId,
                    tableId = branch.TableIds[i],
                    date = LocalDate(factory, 2),
                    time = "18:00",
                    partySize = 2,
                    guestName = char.ToUpperInvariant(who[i][0]) + who[i][1..],
                    guestPhone = diners[who[i]].Phone,
                    clientCommandId = Guid.CreateVersion7(),
                    channel = ReservationChannel.App,
                });

                bookings[letters[i]] = booking;
                _names[booking.GetProperty("id").GetGuid()] = $"booking:{letters[i]}";
                _text[booking.GetProperty("code").GetString()!] = $"<code:{letters[i]}>";
                _text[booking.GetProperty("manageToken").GetString()!] = $"<link:{letters[i]}>";
            }

            Guid Id(string letter) => bookings[letter].GetProperty("id").GetGuid();
            string Link(string letter) => bookings[letter].GetProperty("manageToken").GetString()!;
            HttpClient Guest(string letter) => clients[who[Array.IndexOf(letters, letter)]];

            // ---- t0: the pre-orders are placed with their bookings. A's retry is a replay.
            var aSave = Command("A-save");
            var placed = await SendAsync("A:save", Guest("A"), HttpMethod.Put, $"/api/reservations/{Id("A")}/pre-order", new
            {
                lines = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 2, note = "no egg" },
                    new { menuItemId = menu.Wine, quantity = 1 },
                },
                note = "Salad first, please",
                clientCommandId = aSave,
            });
            _names[placed.GetProperty("preOrder").GetProperty("id").GetGuid()] = "preorder:A";

            Assert.True((await SendAsync("A:save-replay", Guest("A"), HttpMethod.Put, $"/api/reservations/{Id("A")}/pre-order", new
            {
                lines = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 2, note = "no egg" },
                    new { menuItemId = menu.Wine, quantity = 1 },
                },
                note = "Salad first, please",
                clientCommandId = aSave,
            })).GetProperty("wasReplay").GetBoolean());

            foreach (var letter in new[] { "B", "F", "G", "H", "I", "J" })
            {
                var saved = await SendAsync($"{letter}:save", Guest(letter), HttpMethod.Put, $"/api/reservations/{Id(letter)}/pre-order", new
                {
                    lines = new[] { new { menuItemId = menu.Coffee, quantity = 2 } },
                    clientCommandId = Command($"{letter}-save"),
                });
                _names[saved.GetProperty("preOrder").GetProperty("id").GetGuid()] = $"preorder:{letter}";
            }

            // ---- t0+1m: A edits from the manage link.
            factory.Clock.UtcNow = t0.AddMinutes(1);
            await SendAsync("A:edit-by-link", link, HttpMethod.Put, $"/api/public/bookings/{Link("A")}/pre-order", new
            {
                lines = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 3, note = "no egg" },
                    new { menuItemId = menu.Wine, quantity = 1 },
                },
                note = "Salad first, please",
                clientCommandId = Command("A-edit-link"),
            });

            // ---- t0+2m: two days ahead the kitchen cannot have it yet.
            factory.Clock.UtcNow = t0.AddMinutes(2);
            await SendAsync("A:send-too-early", waiter, HttpMethod.Post, $"/api/reservations/{Id("A")}/pre-order/send", new
            {
                clientCommandId = Command("A-send-early"),
            }, HttpStatusCode.Conflict);

            // ---- t0+3m: Jan moves J to 20:00 on the tenth table: the pre-order goes with it.
            factory.Clock.UtcNow = t0.AddMinutes(3);
            await SendAsync("J:reschedule", Guest("J"), HttpMethod.Post, $"/api/reservations/{Id("J")}/reschedule", new
            {
                date = LocalDate(factory, 2),
                time = "20:00",
                partySize = 2,
                diningTableId = branch.TableIds[9],
                clientCommandId = Command("J-move"),
            });

            // ---- t0+4m: and clears it from the manage link - asked at 30 minutes again, like any booking.
            factory.Clock.UtcNow = t0.AddMinutes(4);
            await SendAsync("J:clear-by-link", link, HttpMethod.Post, $"/api/public/bookings/{Link("J")}/pre-order/clear", new
            {
                clientCommandId = Command("J-clear-link"),
            });

            // ---- T-2h: the lock. A's edit is refused.
            factory.Clock.UtcNow = start.AddHours(-2);
            await SendAsync("A:edit-locked", Guest("A"), HttpMethod.Put, $"/api/reservations/{Id("A")}/pre-order", new
            {
                lines = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Command("A-edit-locked"),
            }, HttpStatusCode.Conflict);

            // ---- T-90: the manager changes the settings with cards on the rail: every tablet re-reads it.
            factory.Clock.UtcNow = start.AddMinutes(-90);
            await SendAsync("settings:changed", manager, HttpMethod.Put, $"/api/branches/{branch.BranchId}/pre-order-settings", new
            {
                acceptsPreOrders = true,
                minutesBefore = 45,
                excludedMenuItemIds = new[] { menu.Wine },
            });

            // ---- T-60: "are you coming?" to Ani (an account to tell) and to Davit's phoned booking (none).
            factory.Clock.UtcNow = start.AddMinutes(-60);
            Assert.True((await SendAsync("A:ask", waiter, HttpMethod.Post, $"/api/reservations/{Id("A")}/ask-on-my-way", null))
                .GetProperty("notified").GetBoolean());
            Assert.False((await SendAsync("D:ask", waiter, HttpMethod.Post, $"/api/reservations/{dId}/ask-on-my-way", null))
                .GetProperty("notified").GetBoolean());

            // ---- T-55: Ani is on her way, from the app; Bob is running late; Eva, with no pre-order, is on her way from
            // the link.
            factory.Clock.UtcNow = start.AddMinutes(-55);
            await SendAsync("A:on-my-way", Guest("A"), HttpMethod.Post, $"/api/reservations/{Id("A")}/on-my-way", new { });
            await SendAsync("B:running-late", Guest("B"), HttpMethod.Post, $"/api/reservations/{Id("B")}/running-late", new { minutes = 15 });
            await SendAsync("E:on-my-way-by-link", link, HttpMethod.Post, $"/api/public/bookings/{Link("E")}/on-my-way", new { });

            // ---- T-50: Eva clears the pre-order she never had; the waiter marks Bob not coming, and the retry.
            factory.Clock.UtcNow = start.AddMinutes(-50);
            await SendAsync("E:clear-nothing", Guest("E"), HttpMethod.Post, $"/api/reservations/{Id("E")}/pre-order/clear", new
            {
                clientCommandId = Command("E-clear"),
            });
            await SendAsync("B:not-coming", waiter, HttpMethod.Post, $"/api/reservations/{Id("B")}/pre-order/not-coming", new
            {
                clientCommandId = Command("B-not-coming"),
            });
            Assert.True((await SendAsync("B:not-coming-replay", waiter, HttpMethod.Post, $"/api/reservations/{Id("B")}/pre-order/not-coming", new
            {
                clientCommandId = Command("B-not-coming-again"),
            })).GetProperty("wasReplay").GetBoolean());

            // ---- T-45: A, H and I go to the kitchen before anybody is here - held tickets. A second tablet's send of A is
            // answered as done.
            factory.Clock.UtcNow = start.AddMinutes(-45);

            foreach (var letter in new[] { "A", "H", "I" })
            {
                await SendAsync($"{letter}:send-held", waiter, HttpMethod.Post, $"/api/reservations/{Id(letter)}/pre-order/send", new
                {
                    clientCommandId = Command($"{letter}-send"),
                });
            }

            Assert.True((await SendAsync("A:send-replay", waiter, HttpMethod.Post, $"/api/reservations/{Id("A")}/pre-order/send", new
            {
                clientCommandId = Command("A-send-again"),
            })).GetProperty("wasReplay").GetBoolean());

            // ---- T-40: the kitchen has A's ticket ready before the party arrives.
            factory.Clock.UtcNow = start.AddMinutes(-40);
            await SendAsync("A:held-ready", waiter, HttpMethod.Post, $"/api/orders/{IdNamed("preorder:A")}/status", new { status = 3 });

            // ---- T-35: Hov cancels with his ticket held - it is voided; Bob cancels with his pre-order never sent.
            factory.Clock.UtcNow = start.AddMinutes(-35);
            await SendAsync("H:cancel", Guest("H"), HttpMethod.Post, $"/api/reservations/{Id("H")}/cancel", new { });
            await SendAsync("B:cancel", Guest("B"), HttpMethod.Post, $"/api/reservations/{Id("B")}/cancel", new { });

            // ---- T-15: F and G are seated with their pre-orders not sent - no tab; F's waiter opens one.
            factory.Clock.UtcNow = start.AddMinutes(-15);

            foreach (var letter in new[] { "F", "G" })
            {
                await SendAsync($"{letter}:seat", waiter, HttpMethod.Post, $"/api/branches/{branch.BranchId}/tables/{bookings[letter].GetProperty("tableId").GetGuid()}/seat-reservation", new
                {
                    reservationId = Id(letter),
                    clientCommandId = Command($"{letter}-seat"),
                });
            }

            factory.Clock.UtcNow = start.AddMinutes(-14);
            await SendAsync("F:open-tab", waiter, HttpMethod.Post, $"/api/branches/{branch.BranchId}/tables/{bookings["F"].GetProperty("tableId").GetGuid()}/open-tab", new
            {
                clientCommandId = Command("F-open-tab"),
            });

            // ---- T-10: sent to the seated parties - onto F's tab, and onto a tab the send opens for G.
            factory.Clock.UtcNow = start.AddMinutes(-10);

            foreach (var letter in new[] { "F", "G" })
            {
                await SendAsync($"{letter}:send-seated", waiter, HttpMethod.Post, $"/api/reservations/{Id(letter)}/pre-order/send", new
                {
                    clientCommandId = Command($"{letter}-send"),
                });
            }

            // ---- T-5: the parties with held tickets arrive: A seated by the waiter, Ina opening with her booking code.
            factory.Clock.UtcNow = start.AddMinutes(-5);
            await SendAsync("A:seat", waiter, HttpMethod.Post, $"/api/branches/{branch.BranchId}/tables/{bookings["A"].GetProperty("tableId").GetGuid()}/seat-reservation", new
            {
                reservationId = Id("A"),
                clientCommandId = Command("A-seat"),
            });
            await SendAsync("I:open-by-booking", Guest("I"), HttpMethod.Post, "/api/tabs/open-by-booking", new
            {
                bookingCode = bookings["I"].GetProperty("code").GetString(),
                deviceId = "phone-ina-0001",
                clientCommandId = Command("I-open"),
                displayName = "Ina",
            });

            var actual = await SnapshotAsync(factory, branch, letters.Select(Id).Append(dId).ToList(), who, diners, t0, dCode);

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

    private Guid Command(string name)
    {
        var id = Guid.CreateVersion7();
        _names[id] = $"cmd:{name}";

        return id;
    }

    private Guid IdNamed(string name) => _names.Single(p => p.Value == name).Key;

    /// <summary>Sends, asserts the status, and keeps the answer under <paramref name="label"/> for the record.</summary>
    private async Task<JsonElement> SendAsync(
        string label, HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == expected, $"{method} {url} answered {(int)response.StatusCode}: {text}");

        _answers.Add((label, Answer((int)response.StatusCode, text)));

        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>An answer as the record keeps it: a success whole, a refusal by its status, code and context.</summary>
    private static string Answer(int status, string text)
    {
        if (text.Length == 0)
        {
            return $"{{\"status\":{status}}}";
        }

        var node = JsonNode.Parse(text)!.AsObject();

        if (status < 400)
        {
            return node.ToJsonString(Render);
        }

        var refusal = new JsonObject
        {
            ["status"] = status,
            ["code"] = node["code"]?.DeepClone(),
            ["context"] = node["context"]?.DeepClone(),
        };

        return refusal.ToJsonString(Render);
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        List<Guid> bookingIds,
        string[] who,
        Dictionary<string, (string Token, string Phone)> diners,
        DateTime startedAt,
        string dCode)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        _names[branch.VenueId] = "venue";
        _names[branch.BranchId] = "branch";
        _names[branch.WaiterId] = "staff:waiter";
        _names[branch.ManagerId] = "staff:manager";
        _names[bookingIds[^1]] = "booking:D";
        _text[dCode] = "<code:D>";
        _text["+37499000111"] = "<davit phone>";

        for (var i = 0; i < branch.TableIds.Count; i++)
        {
            _names[branch.TableIds[i]] = $"table:{i + 1}";
        }

        foreach (var name in who)
        {
            _names[await db.DinerUsers.Where(d => d.PhoneE164 == diners[name].Phone).Select(d => d.Id).SingleAsync()] = $"diner:{name}";
            _text[diners[name].Phone] = $"<{name} phone>";
        }

        var letterOf = bookingIds.ToDictionary(id => id, id => _names[id]["booking:".Length..]);

        var sittings = await db.TableSessions.AsNoTracking().Where(s => s.BranchId == branch.BranchId).ToListAsync();

        foreach (var sitting in sittings)
        {
            _names[sitting.Id] = sitting.ReservationId is { } r && letterOf.TryGetValue(r, out var letter) ? $"sitting:{letter}" : "sitting:?";
        }

        var tabs = await db.Tabs.AsNoTracking().Where(t => t.BranchId == branch.BranchId).ToListAsync();

        foreach (var tab in tabs)
        {
            var sitting = sittings.Single(s => s.Id == tab.TableSessionId);
            _names[tab.Id] = _names[sitting.Id].Replace("sitting:", "tab:", StringComparison.Ordinal);
        }

        var tabIds = tabs.Select(t => t.Id).ToList();
        var preOrders = await db.PreOrders.AsNoTracking().Where(p => p.BranchId == branch.BranchId).ToListAsync();
        var preOrderIds = preOrders.Select(p => p.Id).ToList();
        var preOrderLines = await db.PreOrderLines.AsNoTracking().Where(l => preOrderIds.Contains(l.PreOrderId)).ToListAsync();
        var orders = await db.TabOrders.AsNoTracking().Where(o => tabIds.Contains(o.TabId)).ToListAsync();
        var orderIds = orders.Select(o => o.Id).ToList();
        var orderLines = await db.TabOrderLines.AsNoTracking().Where(l => orderIds.Contains(l.TabOrderId)).ToListAsync();
        var reservations = await db.Reservations.AsNoTracking().Where(r => bookingIds.Contains(r.Id)).ToListAsync();
        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            var by = entry.StaffMemberId is { } staff && _names.TryGetValue(staff, out var name) ? name[6..] : "?";
            _names.TryAdd(entry.DeviceId!.Value, $"device:{by}");
        }

        var threads = await db.Conversations.AsNoTracking()
            .Where(c => c.ReservationId != null && bookingIds.Contains(c.ReservationId.Value))
            .ToListAsync();
        var threadIds = threads.Select(c => c.Id).ToList();
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => threadIds.Contains(m.ConversationId)).ToListAsync();

        foreach (var thread in threads)
        {
            var letter = letterOf[thread.ReservationId!.Value];
            _names[thread.Id] = $"thread:{letter}";
            var n = 0;

            foreach (var message in messages.Where(m => m.ConversationId == thread.Id).OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Sequence))
            {
                _names[message.Id] = $"msg:{letter}{++n}";
            }
        }

        // Anything about the bookings, the tabs, the sittings, the pre-orders or the guests.
        var keys = _names
            .Where(p => p.Value.Split(':')[0] is "booking" or "tab" or "sitting" or "preorder" or "diner" or "thread" or "msg")
            .Select(p => p.Key.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt.AddMinutes(-1)).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var dinerIds = _names.Where(p => p.Value.StartsWith("diner:", StringComparison.Ordinal)).Select(p => p.Key).ToList();
        var feed = await db.DinerNotifications.AsNoTracking().Where(n => dinerIds.Contains(n.DinerUserId)).ToListAsync();

        // Each tab's stream in its own sequence, tabs by name.
        var stream = (await db.TabEvents.AsNoTracking().Where(e => tabIds.Contains(e.TabId)).ToListAsync())
            .OrderBy(e => _names[e.TabId], StringComparer.Ordinal)
            .ThenBy(e => e.Sequence)
            .ToList();

        // The branch's change stream in its own order: the sequence is the contract a tablet catching up reads.
        var changes = await db.TableStateChanges.AsNoTracking()
            .Where(c => c.BranchId == branch.BranchId)
            .OrderBy(c => c.Sequence)
            .ToListAsync();

        var commandIds = _names.Where(p => p.Value.StartsWith("cmd:", StringComparison.Ordinal)).Select(p => p.Key).ToList();
        var processed = await db.ProcessedCommands.AsNoTracking().Where(c => commandIds.Contains(c.ClientCommandId)).ToListAsync();

        // The builders suffix the venue's and the branch's names to keep them unique across tests.
        var place = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Branch = b.Name, Venue = b.Venue.Name, b.TimeZoneId })
            .FirstAsync();
        _text[place.Venue] = "<venue name>";
        _text[place.Branch] = "<branch name>";

        var today = Yalla.Application.Reservations.BranchZone.For(place.TimeZoneId).LocalDateAt(startedAt);

        string Normalised(string row)
        {
            foreach (var (from, to) in _text.OrderByDescending(p => p.Key.Length))
            {
                row = row.Replace(from, to, StringComparison.Ordinal);
            }

            return Normalise(row, _names, startedAt, today);
        }

        var text = new StringBuilder();

        text.Append("## Answers (").Append(_answers.Count).Append(")\n");

        foreach (var (label, json) in _answers)
        {
            text.Append(label).Append(": ")
                .Append(SortArrays(JsonNode.Parse(Normalised(json)))!.ToJsonString(Render)).Append('\n');
        }

        text.Append('\n');

        ChangeStream(text, changes, Normalised);
        Section(text, "ActivityEntries", activity, Normalised, sorted: true);
        Section(text, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(text, "DinerNotifications", feed, Normalised, sorted: true);
        Section(text, "TabEvents", stream, Normalised, sorted: false);
        Section(text, "ProcessedCommands", processed, Normalised, sorted: true);
        Section(text, "PreOrders", preOrders, Normalised, sorted: true);
        Section(text, "PreOrderLines", preOrderLines, Normalised, sorted: true);
        Section(text, "Reservations", reservations, Normalised, sorted: true);
        Section(text, "TableSessions", sittings, Normalised, sorted: true);
        Section(text, "Tabs", tabs, Normalised, sorted: true);
        Section(text, "TabOrders", orders, Normalised, sorted: true);
        Section(text, "TabOrderLines", orderLines, Normalised, sorted: true);
        Section(text, "Conversations", threads, Normalised, sorted: true);
        Section(text, "ConversationMessages", messages, Normalised, sorted: true);

        return text.ToString();
    }

    /// <summary>
    /// The change stream in its sequence. Rows of one signal written by one save are numbered by the database in no
    /// promised order (the settings' signals for every card on the rail), so a run of them is sorted among itself; the
    /// order of one signal against another within a save is kept.
    /// </summary>
    private static void ChangeStream(StringBuilder text, List<Yalla.Domain.Audit.TableStateChange> changes, Func<string, string> normalise)
    {
        var rendered = new List<string>();
        var run = new List<string>();
        (DateTime, TableChangeSignal?)? runKey = null;

        foreach (var change in changes)
        {
            var key = (change.AtUtc, change.Signal);

            if (runKey != key)
            {
                rendered.AddRange(run.Order(StringComparer.Ordinal));
                run.Clear();
                runKey = key;
            }

            run.Add(SortArrays(JsonNode.Parse(normalise(RenderRow(change))))!.ToJsonString(Render));
        }

        rendered.AddRange(run.Order(StringComparer.Ordinal));

        text.Append("## TableStateChanges (").Append(rendered.Count).Append(")\n");

        foreach (var row in rendered)
        {
            text.Append(row).Append('\n');
        }

        text.Append('\n');
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

            // The History's dish names are joined in the order the lines were read, which no query orders: as a set.
            if (name == "itemNames" && value is JsonValue names && names.TryGetValue<string>(out var joined))
            {
                result[name] = string.Join(" | ", DishNames(joined).Order(StringComparer.Ordinal));
                continue;
            }

            result[name] = value?.DeepClone();
        }

        return result.ToJsonString(Render);
    }

    /// <summary>The test menu's dishes in a joined list - one of them has a comma in its name.</summary>
    private static IEnumerable<string> DishNames(string joined)
    {
        string[] dishes = ["Adjarian khachapuri", "Areni red, bottle", "Flat white"];
        var rest = joined;

        while (rest.Length > 0)
        {
            var dish = dishes.FirstOrDefault(d => rest.StartsWith(d, StringComparison.Ordinal)) ?? rest;
            yield return dish;
            rest = rest[dish.Length..].TrimStart(',', ' ');
        }
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

    private static string Normalise(string text, Dictionary<Guid, string> names, DateTime startedAt, DateOnly today)
    {
        text = Regex.Replace(
            text,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => names.TryGetValue(Guid.Parse(m.Value), out var name) ? name : "<guid>");

        // Participant tokens' devices, the fresh id a staff push key carries for an event that can happen again.
        text = Regex.Replace(text, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", "<n>");

        // A participant's token, minted for the phone that opened a tab.
        text = Regex.Replace(text, @"eyJ[\w-]+\.[\w-]+\.[\w-]+", "<jwt>");

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

        // The compact stamps in idempotency keys (a booking's start to the minute), as offsets like the instants above:
        // written as dates, the record held only on the day it was approved.
        text = Regex.Replace(
            text,
            @"(?<!\d)\d{12}(?!\d)",
            m =>
            {
                var at = DateTime.SpecifyKind(DateTime.ParseExact(m.Value, "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
                return $"T{(at - startedAt).TotalSeconds:+0;-0;+0}s";
            });

        // Local dates, as days from the branch's today.
        return Regex.Replace(
            text,
            @"(?<![\d-])\d{4}-\d{2}-\d{2}(?![\dT])",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "PreOrderSideEffects.approved.txt");

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
