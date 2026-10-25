using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted evening of table state and ownership writes - seatings of every
/// kind, freeing with and without a tab, holds, out of service, the refusals, the replays, a hold the diner extended,
/// hand-overs to another waiter - through the real host, compared with the record in
/// <c>Snapshots/TableSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of the tables vertical moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: every path of
/// <c>TableStateService</c> and <c>TableTransferService</c> - a walk-in seated and its replay, a seating refused on an
/// occupied table, a booked party seated by the waiter and by the diner's own phone, a QR scan seating a table and its
/// turn-away freeing it, a held table released, seated for its late booking and seated with nobody's booking, freed with
/// a zero-balance tab, with a balance left and with no tab, a double free refused, a stale console row refused
/// (<c>table-sitting-changed</c>), queued and changed-and-changed-back commands refused, out of service with a booking
/// stranded and back, a hold the diner extended, a hand-over by the table's waiter to somebody on shift, one by a
/// manager, one refused and one already done - writes the same activity entries, outbox rows, diner feed rows, tab-stream
/// events, change-stream rows (in sequence) and stored answers, answers the same, and leaves the tables, sittings, tabs
/// and bookings in the same state.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script, local dates as days from the branch's today; booking codes and phone numbers by
/// name. Rows of one table are sorted by their rendering, except the change stream, which reads in its sequence, the
/// feed in its, and each tab's stream in its. Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TableSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableSideEffectsSnapshotTests(SqlServerFixture fixture)
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
        "id", "rowVersion", "attemptCount", "lockedUntilUtc", "lockedBy", "sentAtUtc", "lastError",
        "deadLetteredAtUtc", "isSent", "isDeadLettered", "sequence",
    };

    private readonly List<(string Label, string Json)> _answers = [];

    private readonly Dictionary<Guid, string> _names = [];

    [SkippableFact]
    public async Task A_scripted_evening_of_tables_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        var (dinerToken, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);

        AuthBranch branch;
        TestMenu menu;
        Guid zed;
        Guid cook;
        Reservation onThree;
        Reservation lateOnFour;
        Reservation laterOnSix;
        Reservation dinersOnSeven;
        Reservation dinersLateOnEight;
        string timeZoneId;
        var phones = new List<string>();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 9);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            cook = await AuthTestData.SeedKitchenAsync(db, branch);

            // Zed, a waiter on shift now (15:00 Yerevan): the one a table is handed to.
            var onShift = new StaffMember(
                branch.VenueId, "Zed Waiter", $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}", StaffRole.Waiter, "hash",
                branch.BranchId);
            db.StaffMembers.Add(onShift);
            timeZoneId = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.TimeZoneId).FirstAsync();
            var today = BranchZone.For(timeZoneId).LocalDateAt(startedAt);
            db.StaffShifts.Add(new StaffShift(onShift.Id, branch.BranchId, today, new TimeOnly(14, 0), new TimeOnly(18, 0)));
            zed = onShift.Id;

            var policy = await db.Branches
                .AsNoTracking()
                .Where(b => b.Id == branch.BranchId)
                .Select(b => b.ReservationPolicy)
                .FirstAsync();

            Reservation Book(int table, int minutesFromStart, string guest, Guid? diner = null)
            {
                var startUtc = startedAt.AddMinutes(minutesFromStart);
                var local = BranchZone.For(timeZoneId).ToLocal(startUtc);
                var phone = ReviewTestData.NextPhone();
                phones.Add(phone);

                var booking = Reservation.Create(
                    branch.BranchId,
                    branch.TableIds[table - 1],
                    startUtc,
                    DateOnly.FromDateTime(local),
                    TimeOnly.FromDateTime(local),
                    partySize: 2,
                    guest,
                    phone,
                    ReservationCode.Generate(),
                    policy,
                    dinerUserId: diner);

                db.Reservations.Add(booking);

                // One save each: the booking hook signals a save's bookings in the tracker's order, which is no order.
                db.SaveChanges();

                return booking;
            }

            onThree = Book(3, 15, "Ani Booked");
            lateOnFour = Book(4, -5, "Late Lilit");
            laterOnSix = Book(6, 180, "Stranded Sona");
            dinersOnSeven = Book(7, 50, "Diner Davit", dinerUserId);
            dinersLateOnEight = Book(8, -11, "Late Diner Davit", dinerUserId);

            await db.SaveChangesAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var diner = factory.CreateClientWithToken(dinerToken);
        using var anonymous = factory.CreateClient();

        var b = $"/api/branches/{branch.BranchId}/tables";
        Guid T(int n) => branch.TableIds[n - 1];

        Guid Command(string name)
        {
            var id = Guid.CreateVersion7();
            _names[id] = $"cmd:{name}";

            return id;
        }

        // Table 1: a walk-in seated by the waiter, the same tap again, and a second seating refused. The waiter opens
        // the bill (nothing on it), hands the table to Zed, who is on shift; the manager's hand-over to the cook is
        // refused and the one to Zed again is already done. Freed with the zero-balance tab, which closes.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var seatOne = Command("seat-1");
        await PostAsync("seat walk-in 1", waiter, $"{b}/{T(1)}/seat-walk-in", new { partySize = 3, clientCommandId = seatOne }, HttpStatusCode.OK);
        await PostAsync("seat walk-in 1 replayed", waiter, $"{b}/{T(1)}/seat-walk-in", new { partySize = 3, clientCommandId = seatOne }, HttpStatusCode.OK);
        await PostAsync("seat walk-in 1 refused: occupied", waiter, $"{b}/{T(1)}/seat-walk-in", new { partySize = 2, clientCommandId = Command("seat-1-again") }, HttpStatusCode.UnprocessableEntity);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var tabOne = (await PostAsync(null, waiter, $"{b}/{T(1)}/open-tab", new { clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK))
            .GetProperty("tabId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("transfer 1 to Zed", waiter, $"/api/tabs/{tabOne}/transfer", new { toStaffMemberId = zed, clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK);
        await PostAsync("transfer 1 to the cook refused", manager, $"/api/tabs/{tabOne}/transfer", new { toStaffMemberId = cook, clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.UnprocessableEntity);
        await PostAsync("transfer 1 to Zed again", manager, $"/api/tabs/{tabOne}/transfer", new { toStaffMemberId = zed, clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("free 1 (zero-balance tab)", waiter, $"{b}/{T(1)}/free", new { clientCommandId = Command("free-1") }, HttpStatusCode.OK);

        // Table 2: a walk-in with a reason, freed with no tab at all, the free replayed, and freed again - refused.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("seat walk-in 2", waiter, $"{b}/{T(2)}/seat-walk-in", new { partySize = 2, reason = "window seat", clientCommandId = Command("seat-2") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var freeTwo = Command("free-2");
        await PostAsync("free 2 (no tab)", waiter, $"{b}/{T(2)}/free", new { reason = "party left", clientCommandId = freeTwo }, HttpStatusCode.OK);
        await PostAsync("free 2 replayed", waiter, $"{b}/{T(2)}/free", new { reason = "party left", clientCommandId = freeTwo }, HttpStatusCode.OK);
        await PostAsync("free 2 refused: already free", waiter, $"{b}/{T(2)}/free", new { clientCommandId = Command("free-2-again") }, HttpStatusCode.UnprocessableEntity);

        // Table 3: the booked party seated by the waiter; the console's stale row for another booking is refused, and
        // finishing this booking's sitting completes it.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("seat reservation 3", waiter, $"{b}/{T(3)}/seat-reservation", new { reservationId = onThree.Id, partySize = 4, clientCommandId = Command("seat-3") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("free 3 refused: sitting changed", manager, $"{b}/{T(3)}/free", new { reservationId = lateOnFour.Id, clientCommandId = Command("free-3-stale") }, HttpStatusCode.Conflict);
        await PostAsync("free 3 (booking's sitting)", manager, $"{b}/{T(3)}/free", new { reservationId = onThree.Id, clientCommandId = Command("free-3") }, HttpStatusCode.OK);

        // Table 4: held for the late booking, which arrives and is seated from the hold; the waiter opens its bill and
        // keys a coffee; the manager hands the table to Zed; freed with the coffee unpaid, the tab left open.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("hold 4", waiter, $"{b}/{T(4)}/hold", new { clientCommandId = Command("hold-4") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("seat held party 4 (booking)", waiter, $"{b}/{T(4)}/seat-held-party", new { partySize = 2, reservationId = lateOnFour.Id, clientCommandId = Command("seat-4") }, HttpStatusCode.OK);
        var tabFour = (await PostAsync(null, waiter, $"{b}/{T(4)}/open-tab", new { clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK))
            .GetProperty("tabId").GetGuid();
        var coffee = (await PostAsync(
                null,
                waiter,
                $"/api/tabs/{tabFour}/staff-orders",
                new { items = new object[] { new { menuItemId = menu.Coffee, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() },
                HttpStatusCode.Created))
            .GetProperty("orderId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("transfer 4 to Zed by the manager", manager, $"/api/tabs/{tabFour}/transfer", new { toStaffMemberId = zed, clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("free 4 (balance left)", waiter, $"{b}/{T(4)}/free", new { reason = "they left", clientCommandId = Command("free-4") }, HttpStatusCode.OK);

        // Table 5: held and released, the release replayed; a queued hold that saw it Held, and one that saw it Free at
        // another row version, both refused; held again and seated with nobody's booking, then freed.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("hold 5", waiter, $"{b}/{T(5)}/hold", new { reason = "regulars phoned", clientCommandId = Command("hold-5") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var releaseFive = Command("release-5");
        await PostAsync("release hold 5", waiter, $"{b}/{T(5)}/release-hold", new { clientCommandId = releaseFive }, HttpStatusCode.OK);
        await PostAsync("release hold 5 replayed", waiter, $"{b}/{T(5)}/release-hold", new { clientCommandId = releaseFive }, HttpStatusCode.OK);
        await PostAsync(
            "hold 5 refused: queued, status changed", waiter, $"{b}/{T(5)}/hold",
            new { queued = true, expectedFromStatus = 2, clientCommandId = Command("hold-5-queued") }, HttpStatusCode.Conflict);
        await PostAsync(
            "hold 5 refused: changed and changed back", waiter, $"{b}/{T(5)}/hold",
            new { queued = true, expectedFromStatus = 1, expectedRowVersion = "AAAAAAAAB9E=", clientCommandId = Command("hold-5-stale") },
            HttpStatusCode.Conflict);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("hold 5 again", waiter, $"{b}/{T(5)}/hold", new { clientCommandId = Command("hold-5-again") }, HttpStatusCode.OK);
        await PostAsync("seat held party 5 (no booking)", waiter, $"{b}/{T(5)}/seat-held-party", new { partySize = 5, clientCommandId = Command("seat-5") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("free 5", waiter, $"{b}/{T(5)}/free", new { clientCommandId = Command("free-5") }, HttpStatusCode.OK);

        // Table 6: out of service with tonight's booking stranded, a seating refused, back in service.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("out of service 6", waiter, $"{b}/{T(6)}/out-of-service", new { reason = "wobbly leg", clientCommandId = Command("oos-6") }, HttpStatusCode.OK);
        await PostAsync("seat walk-in 6 refused: out of service", waiter, $"{b}/{T(6)}/seat-walk-in", new { partySize = 2, clientCommandId = Command("seat-6") }, HttpStatusCode.UnprocessableEntity);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("return to service 6", manager, $"{b}/{T(6)}/return-to-service", new { clientCommandId = Command("back-6") }, HttpStatusCode.OK);

        // Table 9: a stranger's phone scans the QR and seats it; the waiter turns the scan away, which frees the table.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        string qrNine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var nine = T(9);
            qrNine = await db.DiningTables.Where(t => t.Id == nine).Select(t => t.QrToken).FirstAsync();
        }

        var scanned = await PostAsync(
            null, anonymous, "/api/tabs/open",
            new { qrToken = qrNine, deviceId = "phone-scanner", clientCommandId = Command("scan-9"), displayName = "Scanner" },
            HttpStatusCode.OK);
        var tabNine = scanned.GetProperty("tab").GetProperty("tabId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(null, waiter, $"/api/tabs/{tabNine}/reject", new { clientCommandId = Guid.CreateVersion7(), reason = "nobody at the table" }, HttpStatusCode.OK);

        // Table 7: the diner who booked it opens the tab from the booking, which seats the party as the booking.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var opened = await PostAsync(
            null, diner, "/api/tabs/open-by-booking",
            new { bookingCode = dinersOnSeven.Code, deviceId = "phone-davit", clientCommandId = Command("book-7"), displayName = "Davit" },
            HttpStatusCode.OK);
        var tabSeven = opened.GetProperty("tab").GetProperty("tabId").GetGuid();

        // Table 8: held for the diner's late booking; the diner keeps the table for longer.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync("hold 8", waiter, $"{b}/{T(8)}/hold", new { reason = "held for a late booking", clientCommandId = Command("hold-8") }, HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(null, diner, $"/api/reservations/{dinersLateOnEight.Id}/extend-hold", new { clientCommandId = Command("extend-8") }, HttpStatusCode.OK);

        _names[branch.VenueId] = "venue";
        _names[branch.BranchId] = "branch";
        _names[branch.WaiterId] = "staff:waiter";
        _names[branch.ManagerId] = "staff:manager";
        _names[zed] = "staff:zed";
        _names[cook] = "staff:cook";
        _names[dinerUserId] = "diner:davit";
        _names[tabOne] = "tab:1";
        _names[tabFour] = "tab:4";
        _names[tabSeven] = "tab:7";
        _names[tabNine] = "tab:9";
        _names[coffee] = "order:coffee";
        _names[menu.Coffee] = "menu:coffee";
        _names[onThree.Id] = "booking:3";
        _names[lateOnFour.Id] = "booking:4";
        _names[laterOnSix.Id] = "booking:6";
        _names[dinersOnSeven.Id] = "booking:7";
        _names[dinersLateOnEight.Id] = "booking:8";

        for (var i = 1; i <= branch.TableIds.Count; i++)
        {
            _names[T(i)] = $"table:{i}";
        }

        var codes = new Dictionary<string, string>
        {
            [onThree.Code] = "<code:3>",
            [lateOnFour.Code] = "<code:4>",
            [laterOnSix.Code] = "<code:6>",
            [dinersOnSeven.Code] = "<code:7>",
            [dinersLateOnEight.Code] = "<code:8>",
        };

        for (var i = 0; i < phones.Count; i++)
        {
            codes[phones[i]] = $"<phone:{i + 1}>";
        }

        var today2 = BranchZone.For(timeZoneId).LocalDateAt(startedAt);
        var actual = await SnapshotAsync(factory, branch, dinerUserId, startedAt, today2, codes);

        await AssertMatchesRecordAsync(actual);
    }

    /// <summary>Posts, asserts the status, and keeps the answer under <paramref name="label"/> for the record.</summary>
    private async Task<JsonElement> PostAsync(string? label, HttpClient client, string url, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == expected, $"POST {url} answered {(int)response.StatusCode}: {text}");

        if (label is not null)
        {
            _answers.Add((label, Answer((int)response.StatusCode, text)));
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>An answer as the record keeps it: a success whole, a refusal by its status, code and context.</summary>
    private static string Answer(int status, string text)
    {
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
        Guid dinerUserId,
        DateTime startedAt,
        DateOnly today,
        Dictionary<string, string> codes)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var tables = await db.DiningTables.AsNoTracking().Where(t => t.BranchId == branch.BranchId).ToListAsync();
        var sittings = (await db.TableSessions.AsNoTracking().Where(s => s.BranchId == branch.BranchId).ToListAsync())
            .OrderBy(s => s.SeatedAtUtc)
            .ThenBy(s => _names[s.DiningTableId], StringComparer.Ordinal)
            .ToList();

        foreach (var group in sittings.GroupBy(s => s.DiningTableId))
        {
            var n = 0;

            foreach (var sitting in group)
            {
                _names[sitting.Id] = $"sitting:{_names[sitting.DiningTableId][6..]}{(n++ == 0 ? string.Empty : $"#{n}")}";
            }
        }

        var tabs = await db.Tabs.AsNoTracking().Where(t => t.BranchId == branch.BranchId).ToListAsync();
        var tabIds = tabs.Select(t => t.Id).ToList();
        var bookings = await db.Reservations.AsNoTracking().Where(r => r.BranchId == branch.BranchId).ToListAsync();
        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        // The waiter's tablet, named by who acted on it; the manager is on the console.
        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            _names.TryAdd(entry.DeviceId!.Value, entry.StaffMemberId == branch.WaiterId ? "device:waiter" : "device:manager");
        }

        var keys = tabIds
            .Concat(sittings.Select(s => s.Id))
            .Concat(bookings.Select(r => r.Id))
            .Concat(branch.TableIds)
            .Append(branch.BranchId)
            .Append(dinerUserId)
            .Select(id => id.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // In the order the feed numbered them; the number itself is the database's, shared with every other test.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => n.DinerUserId == dinerUserId)
            .OrderBy(n => n.Sequence)
            .ToListAsync();

        // Each tab's stream in its own sequence, tabs in the order they were opened.
        var tabOrder = tabs.OrderBy(t => t.OpenedAtUtc).ThenBy(t => _names.GetValueOrDefault(t.Id, "?"), StringComparer.Ordinal)
            .Select(t => t.Id).ToList();
        var stream = (await db.TabEvents.AsNoTracking().Where(e => tabIds.Contains(e.TabId)).ToListAsync())
            .OrderBy(e => tabOrder.IndexOf(e.TabId))
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
            .Select(b => new { Branch = b.Name, Venue = b.Venue.Name })
            .FirstAsync();

        string Normalised(string row)
        {
            foreach (var (code, name) in codes)
            {
                row = row.Replace(code, name, StringComparison.Ordinal);
            }

            return Normalise(row, _names, startedAt, today)
                .Replace(place.Venue, "<venue name>", StringComparison.Ordinal)
                .Replace(place.Branch, "<branch name>", StringComparison.Ordinal);
        }

        var text = new StringBuilder();

        text.Append("## Answers (").Append(_answers.Count).Append(")\n");

        foreach (var (label, json) in _answers)
        {
            text.Append(label).Append(": ")
                .Append(SortArrays(JsonNode.Parse(Normalised(json)))!.ToJsonString(Render)).Append('\n');
        }

        text.Append('\n');

        Section(text, "TableStateChanges", changes, Normalised, sorted: false);
        Section(text, "ActivityEntries", activity, Normalised, sorted: true);
        Section(text, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(text, "DinerNotifications", feed, Normalised, sorted: false);
        Section(text, "TabEvents", stream, Normalised, sorted: false);
        Section(text, "ProcessedCommands", processed, Normalised, sorted: true);
        Section(text, "DiningTables", tables, Normalised, sorted: true);
        Section(text, "TableSessions", sittings, Normalised, sorted: true);
        Section(text, "Tabs", tabs, Normalised, sorted: true);
        Section(text, "Reservations", bookings, Normalised, sorted: true);

        return text.ToString();
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

    /// <summary>Sorts every array by its rendering: the set is the contract, not the sequence.</summary>
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

        // QR tokens, a participant's device, the fresh id a key carries for an event that can happen again.
        text = Regex.Replace(text, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", "<n>");

        text = Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
            m =>
            {
                var at = DateTime.Parse(m.Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

                return $"T{(at - startedAt).TotalSeconds:+0;-0;+0}s";
            });

        // Local dates, as days from the branch's today.
        return Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd").DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "TableSideEffects.approved.txt");

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
