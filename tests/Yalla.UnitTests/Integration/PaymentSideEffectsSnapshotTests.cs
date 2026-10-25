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
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted payments lifecycle writes - discounts, a comp, a reversal, cash with
/// tips, a replay, a refusal, settling in full, a write-off - through the real host, with the money it leaves behind and
/// the reports that add it up, compared with the record in <c>Snapshots/PaymentSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of payments moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: the same
/// activity entries (codes, amounts, before and after, summaries, attribution, device labels), the same outbox rows, the
/// same diner feed rows, the same tab-stream events in the same sequence, the same change-stream rows - and the same
/// end state of both tabs (totals, paid, remaining), their sittings, orders, lines, payments and adjustments, and the
/// same revenue report and Staff → Results figures for the day.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script, local dates as days from the branch's today. Rows of one table are sorted by
/// their rendering, except the tabs' streams, which read in sequence, and the reports, whose arrays pair by index.
/// Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~PaymentSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PaymentSideEffectsSnapshotTests(SqlServerFixture fixture)
{
    private static readonly JsonSerializerOptions Render = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Columns that are the outbox loop's, not the writer's: the test host may be sending as this reads.</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "rowVersion", "attemptCount", "lockedUntilUtc", "lockedBy", "sentAtUtc", "lastError",
        "deadLetteredAtUtc", "isSent", "isDeadLettered",
    };

    [SkippableFact]
    public async Task A_scripted_payments_lifecycle_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        var (_, aniUserId) = await DinerFeedTestData.SignInDinerAsync(factory);

        AuthBranch branch;
        TestMenu menu;
        AuthTab first;
        AuthTab second;
        Guid ani;
        string timeZoneId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            first = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[0], now);
            second = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[1], now);

            var guest = TabParticipant.Guest(first.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false, aniUserId);
            guest.Approve(now);
            db.TabParticipants.Add(guest);
            await db.SaveChangesAsync();

            ani = guest.Id;
            timeZoneId = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.TimeZoneId).FirstAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        // The first table's bill: a round for the table (two coffees, a khachapuri) and Ani's wine, keyed in by the waiter.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var a = await PostAsync(
            waiter,
            $"/api/tabs/{first.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Coffee, quantity = 2 },
                    new { menuItemId = menu.Khachapuri, quantity = 1 },
                },
                clientCommandId = Guid.CreateVersion7(),
            },
            HttpStatusCode.Created);
        var aId = a.GetProperty("orderId").GetGuid();
        var aKhachapuri = LineOf(a, menu.Khachapuri);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await PostAsync(
            waiter,
            $"/api/tabs/{first.TabId}/staff-orders",
            new
            {
                items = new object[] { new { menuItemId = menu.Wine, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
                onBehalfOfParticipantId = ani,
            },
            HttpStatusCode.Created);
        var bId = b.GetProperty("orderId").GetGuid();

        // The manager: 10 % off the whole bill, the khachapuri comped (it came cold), the 10 % reversed (wrong table),
        // and a flat 500 off instead. A second reversal of the same discount is refused and writes nothing.
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var percent = await PostAsync(
            manager,
            $"/api/tabs/{first.TabId}/adjustments",
            new { kind = 1, percent = 10m, reason = "birthday", clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var percentId = percent.GetProperty("adjustmentId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var comp = await PostAsync(
            manager,
            $"/api/tabs/{first.TabId}/adjustments",
            new { tabOrderLineId = aKhachapuri, kind = 2, amountAmd = 5_000L, reason = "came cold", clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var compId = comp.GetProperty("adjustmentId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(manager, $"/api/tab-adjustments/{percentId}/void", new { }, HttpStatusCode.OK);
        await PostAsync(manager, $"/api/tab-adjustments/{percentId}/void", new { }, HttpStatusCode.Conflict);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var flat = await PostAsync(
            manager,
            $"/api/tabs/{first.TabId}/adjustments",
            new { kind = 1, amountAmd = 500L, reason = "regulars", clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var flatId = flat.GetProperty("adjustmentId").GetGuid();

        // The waiter takes cash: Ani pays part with a tip, her retry answers with the same payment, too much is refused
        // with the balance, and the rest settles the bill - the tab closes and its sitting with it.
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var aniCash = new { amountAmd = 6_000L, tabParticipantId = ani, tipAmd = 700L, clientCommandId = Guid.CreateVersion7() };
        var p1 = await PostAsync(waiter, $"/api/tabs/{first.TabId}/payments/cash", aniCash, HttpStatusCode.Created);
        var p1Id = p1.GetProperty("paymentId").GetGuid();
        Assert.True((await PostAsync(waiter, $"/api/tabs/{first.TabId}/payments/cash", aniCash, HttpStatusCode.Created)).GetProperty("wasReplay").GetBoolean());

        var remaining = p1.GetProperty("totals").GetProperty("remainingAmd").GetInt64();
        await PostAsync(
            waiter,
            $"/api/tabs/{first.TabId}/payments/cash",
            new { amountAmd = remaining + 1L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Conflict);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var p2 = await PostAsync(
            waiter,
            $"/api/tabs/{first.TabId}/payments/cash",
            new { amountAmd = remaining, tipAmd = 1_000L, clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var p2Id = p2.GetProperty("paymentId").GetGuid();
        Assert.True(p2.GetProperty("tabClosed").GetBoolean());

        // The second table: a bottle of wine, part paid in cash, and the party leaves - the manager writes off the rest.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var c = await PostAsync(
            waiter,
            $"/api/tabs/{second.TabId}/staff-orders",
            new { items = new object[] { new { menuItemId = menu.Wine, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var cId = c.GetProperty("orderId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        var p3 = await PostAsync(
            waiter,
            $"/api/tabs/{second.TabId}/payments/cash",
            new { amountAmd = 4_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var p3Id = p3.GetProperty("paymentId").GetGuid();

        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await PostAsync(manager, $"/api/tabs/{second.TabId}/abandon", new { reason = "walked out" }, HttpStatusCode.OK);

        // What the day adds up to: the revenue report, Staff → Results and the waiter's own cash list.
        var today = BranchZone.For(timeZoneId).LocalDateAt(factory.Clock.UtcNow);
        var window = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var revenue = await GetAsync(manager, $"/api/branches/{branch.BranchId}/reports/revenue?{window}");
        var results = await GetAsync(manager, $"/api/branches/{branch.BranchId}/staff-performance?{window}");
        var waiterCash = await GetAsync(manager, $"/api/branches/{branch.BranchId}/staff/{branch.WaiterId}/performance?{window}&list=cash");

        var names = new Dictionary<Guid, string>
        {
            [branch.VenueId] = "venue",
            [branch.BranchId] = "branch",
            [branch.WaiterId] = "staff:waiter",
            [branch.ManagerId] = "staff:manager",
            [first.TabId] = "tab:1",
            [first.TableId] = "table:1",
            [first.SessionId] = "sitting:1",
            [first.HostParticipantId] = "participant:host1",
            [second.TabId] = "tab:2",
            [second.TableId] = "table:2",
            [second.SessionId] = "sitting:2",
            [second.HostParticipantId] = "participant:host2",
            [ani] = "participant:ani",
            [aniUserId] = "diner:ani",
            [aId] = "order:A",
            [bId] = "order:B",
            [cId] = "order:C",
            [percentId] = "adjustment:percent",
            [compId] = "adjustment:comp",
            [flatId] = "adjustment:flat",
            [p1Id] = "payment:ani",
            [p2Id] = "payment:rest",
            [p3Id] = "payment:part",
            [menu.Coffee] = "menu:coffee",
            [menu.Khachapuri] = "menu:khachapuri",
            [menu.Wine] = "menu:wine",
        };

        var actual = await SnapshotAsync(
            factory, branch, [first.TabId, second.TabId], [first.SessionId, second.SessionId], aniUserId, names, startedAt, today,
            [("RevenueReport", revenue), ("StaffResults", results), ("WaiterCashList", waiterCash)]);

        await AssertMatchesRecordAsync(actual);
    }

    private static Guid LineOf(JsonElement order, Guid menuItemId) =>
        order.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("menuItemId").GetGuid() == menuItemId)
            .GetProperty("lineId").GetGuid();

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == expected, $"POST {url} answered {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}: {text}");

        return text;
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        List<Guid> tabIds,
        List<Guid> sessionIds,
        Guid aniUserId,
        Dictionary<Guid, string> names,
        DateTime startedAt,
        DateOnly today,
        List<(string Title, string Json)> reports)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var orders = await db.TabOrders.AsNoTracking().Where(o => tabIds.Contains(o.TabId)).ToListAsync();
        var orderIds = orders.Select(o => o.Id).ToList();
        var lines = await db.TabOrderLines.AsNoTracking().Where(l => orderIds.Contains(l.TabOrderId)).ToListAsync();

        foreach (var line in lines)
        {
            names[line.Id] = $"line:{names[line.TabOrderId][6..]}:{line.NameSnapshot}";
        }

        var payments = await db.Payments.AsNoTracking().Where(p => tabIds.Contains(p.TabId)).ToListAsync();
        var adjustments = await db.TabAdjustments.AsNoTracking().Where(a => tabIds.Contains(a.TabId)).ToListAsync();
        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        // The waiter's tablet, named by who acted on it; the manager is on the console.
        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            names.TryAdd(entry.DeviceId!.Value, entry.StaffMemberId == branch.WaiterId ? "device:waiter" : "device:manager");
        }

        var keys = orderIds
            .Concat(tabIds)
            .Concat(sessionIds)
            .Concat(payments.Select(p => p.Id))
            .Concat(adjustments.Select(a => a.Id))
            .Append(branch.BranchId)
            .Select(id => id.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // In the order the feed numbered them; the number itself is the database's, shared with every other test.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => n.DinerUserId == aniUserId)
            .OrderBy(n => n.Sequence)
            .ToListAsync();

        // Each tab's stream in its own sequence, the first tab's first.
        var stream = (await db.TabEvents.AsNoTracking().Where(e => tabIds.Contains(e.TabId)).ToListAsync())
            .OrderBy(e => tabIds.IndexOf(e.TabId))
            .ThenBy(e => e.Sequence)
            .ToList();
        var changes = await db.TableStateChanges.AsNoTracking().Where(c => c.BranchId == branch.BranchId).ToListAsync();
        var tabRows = await db.Tabs.AsNoTracking().Where(t => tabIds.Contains(t.Id)).ToListAsync();
        var sittings = await db.TableSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToListAsync();

        // The builders suffix the venue's and the branch's names to keep them unique across tests.
        var place = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Branch = b.Name, Venue = b.Venue.Name })
            .FirstAsync();

        string Normalised(string row) =>
            Normalise(row, names, startedAt, today)
                .Replace(place.Venue, "<venue name>", StringComparison.Ordinal)
                .Replace(place.Branch, "<branch name>", StringComparison.Ordinal);

        var text = new StringBuilder();
        Section(text, "ActivityEntries", activity, Normalised, sorted: true);
        Section(text, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(text, "DinerNotifications", feed, Normalised, sorted: false, drop: "sequence");
        Section(text, "TabEvents", stream, Normalised, sorted: false);
        Section(text, "TableStateChanges", changes, Normalised, sorted: true);
        Section(text, "Tabs", tabRows, Normalised, sorted: true);
        Section(text, "TableSessions", sittings, Normalised, sorted: true);
        Section(text, "TabOrders", orders, Normalised, sorted: true);
        Section(text, "TabOrderLines", lines, Normalised, sorted: true);
        Section(text, "Payments", payments, Normalised, sorted: true);
        Section(text, "TabAdjustments", adjustments, Normalised, sorted: true);

        foreach (var (title, json) in reports)
        {
            text.Append("## ").Append(title).Append('\n')
                .Append(JsonNode.Parse(Normalised(json))!.ToJsonString(Render)).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// One table: each row rendered, its ids and instants named, then its arrays sorted - after the naming, so the
    /// order does not depend on the random part of an id - and the rows sorted the same way unless their order is the
    /// contract.
    /// </summary>
    private static void Section<T>(
        StringBuilder text, string title, IEnumerable<T> rows, Func<string, string> normalise, bool sorted, string? drop = null)
    {
        var rendered = rows
            .Select(row =>
            {
                var node = SortArrays(JsonNode.Parse(normalise(RenderRow(row!))))!.AsObject();

                if (drop is not null)
                {
                    node.Remove(drop);
                }

                return node.ToJsonString(Render);
            })
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

            // A list of the lines' names, in the order the lines were read - see SortArrays.
            if (name == "itemNames" && value is JsonValue names && names.TryGetValue<string>(out var list))
            {
                result[name] = string.Join(", ", list.Split(", ").Order(StringComparer.Ordinal));
                continue;
            }

            result[name] = value?.DeepClone();
        }

        return result.ToJsonString(Render);
    }

    /// <summary>
    /// Sorts every array by its rendering. An order's lines read back from the database come in key order, and two
    /// lines made in the same millisecond have UUIDv7 keys in no particular order - so a summary listing them is in no
    /// particular order either, before and after any change. The set is the contract, not the sequence.
    /// </summary>
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

        // The fresh id a staff push key carries for an event that can happen again.
        text = Regex.Replace(text, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", "<n>");

        text = Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
            m =>
            {
                var at = DateTime.Parse(m.Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

                return $"T{(at - startedAt).TotalSeconds:+0;-0;+0}s";
            });

        // The reports' local dates, as days from the branch's today.
        return Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd").DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "PaymentSideEffects.approved.txt");

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
