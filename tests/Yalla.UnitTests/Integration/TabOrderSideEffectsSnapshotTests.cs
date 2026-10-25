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
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted tab-order lifecycle writes, through the real host, compared
/// with the record in <c>Snapshots/TabOrderSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of tab orders moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: the same
/// activity entries (codes, summaries, attribution, device labels), the same outbox keys and payloads, the same
/// diner feed rows, the same tab-stream events in the same sequence, the same change-stream rows, the same staff
/// push staging - and the same end state of the tab, its orders and lines.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script. Rows of one table are sorted by their rendering, except the tab's stream,
/// which reads in sequence. Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TabOrderSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabOrderSideEffectsSnapshotTests(SqlServerFixture fixture)
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
    public async Task A_scripted_order_lifecycle_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        var (_, aniUserId) = await DinerFeedTestData.SignInDinerAsync(factory);

        AuthBranch branch;
        TestMenu menu;
        AuthTab tab;
        Guid ani;
        Guid cook;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);
            cook = await AuthTestData.SeedKitchenAsync(db, branch);

            var guest = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false, aniUserId);
            guest.Approve(now);
            db.TabParticipants.Add(guest);
            await db.SaveChangesAsync();

            await db.Branches
                .Where(b => b.Id == branch.BranchId)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.NotifyOnOrderReady, true));

            ani = guest.Id;
        }

        using var aniPhone = factory.CreateClientWithToken(await ParticipantTokenAsync(factory, tab.TabId, ani));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cook));

        // A: Ani orders from her phone - two coffees and a khachapuri - and her retry answers with the same order.
        var aCommand = Guid.CreateVersion7();
        var aBody = new
        {
            items = new object[]
            {
                new { menuItemId = menu.Coffee, quantity = 2 },
                new { menuItemId = menu.Khachapuri, quantity = 1, note = "no egg" },
            },
            clientCommandId = aCommand,
        };
        var a = await PostAsync(aniPhone, $"/api/tabs/{tab.TabId}/orders", aBody, HttpStatusCode.Created);
        var aId = a.GetProperty("orderId").GetGuid();
        Assert.True((await PostAsync(aniPhone, $"/api/tabs/{tab.TabId}/orders", aBody, HttpStatusCode.Created)).GetProperty("wasReplay").GetBoolean());

        // B: the waiter keys in a round for Ani - her wine, and a coffee for the table - which goes straight to the
        // kitchen and makes the table theirs.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await PostAsync(
            waiter,
            $"/api/tabs/{tab.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Wine, quantity = 1 },
                    new { menuItemId = menu.Coffee, quantity = 1, isShared = true },
                },
                clientCommandId = Guid.CreateVersion7(),
                onBehalfOfParticipantId = ani,
            },
            HttpStatusCode.Created);
        var bId = b.GetProperty("orderId").GetGuid();

        // A along the rail: the waiter sends it to the kitchen, the cook marks it ready (Ani is told), the waiter
        // serves it.
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await PostAsync(waiter, $"/api/orders/{aId}/status", new { status = 2 }, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await PostAsync(kitchen, $"/api/orders/{aId}/status", new { status = 3 }, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/orders/{aId}/status", new { status = 4 }, HttpStatusCode.OK);

        // B is up too: Ani is told about the wine that is hers.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(kitchen, $"/api/orders/{bId}/status", new { status = 3 }, HttpStatusCode.OK);

        // C: a round for the table; its coffee is sent back on its own, then the whole order is voided - the
        // khachapuri comes off the bill with it, the coffee keeps its own reason.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var c = await PostAsync(
            waiter,
            $"/api/tabs/{tab.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 1 },
                    new { menuItemId = menu.Coffee, quantity = 1 },
                },
                clientCommandId = Guid.CreateVersion7(),
            },
            HttpStatusCode.Created);
        var cId = c.GetProperty("orderId").GetGuid();
        var cCoffee = LineOf(c, menu.Coffee);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(
            waiter,
            $"/api/tabs/{tab.TabId}/lines/{cCoffee}/void",
            new { reason = "sent back", clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/orders/{cId}/status", new { status = 5 }, HttpStatusCode.OK);

        // D: Ani orders again and the waiter voids it before the kitchen has it.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var d = await PostAsync(
            aniPhone,
            $"/api/tabs/{tab.TabId}/orders",
            new { items = new object[] { new { menuItemId = menu.Wine, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() },
            HttpStatusCode.Created);
        var dId = d.GetProperty("orderId").GetGuid();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/orders/{dId}/status", new { status = 5 }, HttpStatusCode.OK);

        var names = new Dictionary<Guid, string>
        {
            [branch.VenueId] = "venue",
            [branch.BranchId] = "branch",
            [branch.WaiterId] = "staff:waiter",
            [branch.ManagerId] = "staff:manager",
            [cook] = "staff:cook",
            [tab.TabId] = "tab",
            [tab.TableId] = "table",
            [tab.SessionId] = "sitting",
            [tab.HostParticipantId] = "participant:host",
            [ani] = "participant:ani",
            [aniUserId] = "diner:ani",
            [aId] = "order:A",
            [bId] = "order:B",
            [cId] = "order:C",
            [dId] = "order:D",
            [menu.Coffee] = "menu:coffee",
            [menu.Khachapuri] = "menu:khachapuri",
            [menu.Wine] = "menu:wine",
        };

        var actual = await SnapshotAsync(factory, branch, tab, aniUserId, names, startedAt);

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

    /// <summary>A participant token for a guest seeded on the tab, minted by the host's own issuer.</summary>
    private async Task<string> ParticipantTokenAsync(YallaApiFactory factory, Guid tabId, Guid participantId)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var tab = await db.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);
        var participant = await db.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == participantId);

        using var scope = factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<TabParticipantTokens>().Issue(tab, participant).AccessToken;
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        AuthTab tab,
        Guid aniUserId,
        Dictionary<Guid, string> names,
        DateTime startedAt)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var orders = await db.TabOrders.AsNoTracking().Where(o => o.TabId == tab.TabId).ToListAsync();
        var orderIds = orders.Select(o => o.Id).ToList();
        var lines = await db.TabOrderLines.AsNoTracking().Where(l => orderIds.Contains(l.TabOrderId)).ToListAsync();

        foreach (var line in lines)
        {
            names[line.Id] = $"line:{names[line.TabOrderId][6..]}:{line.NameSnapshot}";
        }

        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        // The two tablets, named by who acted on them.
        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            names.TryAdd(entry.DeviceId!.Value, entry.StaffMemberId == branch.WaiterId ? "device:waiter" : "device:kitchen");
        }

        var keys = orderIds.Select(id => id.ToString()).Append(tab.TabId.ToString()).ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // In the order the feed numbered them; the number itself is the database's, shared with every other test.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => n.DinerUserId == aniUserId)
            .OrderBy(n => n.Sequence)
            .ToListAsync();
        var stream = await db.TabEvents.AsNoTracking().Where(e => e.TabId == tab.TabId).OrderBy(e => e.Sequence).ToListAsync();
        var changes = await db.TableStateChanges.AsNoTracking().Where(c => c.BranchId == branch.BranchId).ToListAsync();
        var tabRow = await db.Tabs.AsNoTracking().Where(t => t.Id == tab.TabId).ToListAsync();
        var sitting = await db.TableSessions.AsNoTracking().Where(s => s.Id == tab.SessionId).ToListAsync();

        // The builders suffix the venue's and the branch's names to keep them unique across tests.
        var place = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Branch = b.Name, Venue = b.Venue.Name })
            .FirstAsync();

        string Normalised(string row) =>
            Normalise(row, names, startedAt)
                .Replace(place.Venue, "<venue name>", StringComparison.Ordinal)
                .Replace(place.Branch, "<branch name>", StringComparison.Ordinal);

        var text = new StringBuilder();
        Section(text, "ActivityEntries", activity, Normalised, sorted: true);
        Section(text, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(text, "DinerNotifications", feed, Normalised, sorted: false, drop: "sequence");
        Section(text, "TabEvents", stream, Normalised, sorted: false);
        Section(text, "TableStateChanges", changes, Normalised, sorted: true);
        Section(text, "Tab", tabRow, Normalised, sorted: true);
        Section(text, "TableSession", sitting, Normalised, sorted: true);
        Section(text, "TabOrders", orders, Normalised, sorted: true);
        Section(text, "TabOrderLines", lines, Normalised, sorted: true);

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

    private static string Normalise(string text, Dictionary<Guid, string> names, DateTime startedAt)
    {
        text = Regex.Replace(
            text,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => names.TryGetValue(Guid.Parse(m.Value), out var name) ? name : "<guid>");

        // The fresh id a staff push key carries for an event that can happen again.
        text = Regex.Replace(text, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", "<n>");

        return Regex.Replace(
            text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
            m =>
            {
                var at = DateTime.Parse(m.Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

                return $"T{(at - startedAt).TotalSeconds:+0;-0;+0}s";
            });
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "TabOrderSideEffects.approved.txt");

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
