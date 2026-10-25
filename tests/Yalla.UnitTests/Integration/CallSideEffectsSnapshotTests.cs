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
/// Characterisation: every side-effect row a scripted lifecycle of table calls and bill requests writes, through the
/// real host, compared with the record in <c>Snapshots/CallSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of calls (service requests) moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: the same
/// activity entries (<c>table.call-claimed</c>, its attribution and response time), the same outbox rows (the
/// <c>tableCalls</c> staff push), the same tab-stream events in the same sequence, the same change-stream rows, and
/// the same end state of every call, the tabs and the sittings.
/// </para>
/// <para>
/// The script: calls raised, claimed (twice, the second a no-op), acknowledged after a claim and without one (which
/// claims it), acknowledged again (a no-op); requests for the bill with a scope and a tip, replaced by the same guest
/// asking again but not by a tablemate, a plain "the bill, please" that replaces nothing; a claim and an
/// acknowledgement of a replaced request (no-ops); the rate limit refusing a sixth call; and a scan nobody admitted,
/// whose calls - one claimed, one not - are answered by the waiter turning the tab away.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script. Rows of one table are sorted by their rendering, except the tabs' streams,
/// which read in sequence. Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~CallSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class CallSideEffectsSnapshotTests(SqlServerFixture fixture)
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
    public async Task A_scripted_call_lifecycle_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        AuthBranch branch;
        TestMenu menu;
        AuthTab tab;
        Guid ani;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);

            var guest = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false);
            guest.Approve(now);
            db.TabParticipants.Add(guest);
            await db.SaveChangesAsync();

            ani = guest.Id;
        }

        using var hostPhone = factory.CreateClientWithToken(await ParticipantTokenAsync(factory, tab.TabId, tab.HostParticipantId));
        using var aniPhone = factory.CreateClientWithToken(await ParticipantTokenAsync(factory, tab.TabId, ani));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // A scan nobody vouched for, on the other table: it waits for a waiter (D below). Scanned now, before the
        // clock moves - the host checks a token's lifetime against the wall clock the test started from.
        var scanTable = branch.TableIds[1];
        var scan = await ScanAsync(factory, await QrTokenAsync(factory, scanTable), "phone-phantom");
        var phantomTab = scan.GetProperty("tab").GetProperty("tabId").GetGuid();
        using var phantom = factory.CreateClientWithToken(scan.GetProperty("token").GetProperty("accessToken").GetString()!);

        // A: water. The waiter says "I'm on it" two minutes later, taps it again (nothing), and brings it.
        var water = await RaiseAsync(hostPhone, tab.TabId, new { preset = ServiceRequestPreset.Water });
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{water}/claim", null, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{water}/claim", null, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/service-requests/{water}/acknowledge", null, HttpStatusCode.OK);

        // B: napkins, with a note, answered without a claim - which claims it - then answered again (nothing).
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var napkins = await RaiseAsync(hostPhone, tab.TabId, new { preset = ServiceRequestPreset.Napkins, note = "a few more" });
        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        await PostAsync(waiter, $"/api/service-requests/{napkins}/acknowledge", null, HttpStatusCode.OK);
        await PostAsync(waiter, $"/api/service-requests/{napkins}/acknowledge", null, HttpStatusCode.OK);
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{napkins}/claim", null, HttpStatusCode.OK);

        // Something on the bill: the waiter's round for Ani, a coffee for the table beside it.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(
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

        // C: the bill, past the rate window of A and B. The host asks for their share with a tip, Ani for hers, the
        // host then for the whole bill (their share is replaced, Ani's is not), then a plain "the bill" (replaces
        // nothing), then something else.
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var hostShare = await RaiseAsync(hostPhone, tab.TabId, new { preset = ServiceRequestPreset.TheBill, billScope = BillScope.MyShare, tipPercent = 10 });
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        var aniShare = await RaiseAsync(aniPhone, tab.TabId, new { preset = ServiceRequestPreset.TheBill, billScope = BillScope.MyShare });
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        var hostWhole = await RaiseAsync(hostPhone, tab.TabId, new { preset = ServiceRequestPreset.TheBill, billScope = BillScope.WholeBill });
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        var hostPlain = await RaiseAsync(hostPhone, tab.TabId, new { preset = ServiceRequestPreset.TheBill });
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        var aniOther = await RaiseAsync(aniPhone, tab.TabId, new { preset = ServiceRequestPreset.Other, note = "a waiter, please" });

        // The sixth inside ten minutes is refused, and writes nothing.
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        await PostAsync(hostPhone, $"/api/tabs/{tab.TabId}/service-requests", new { preset = ServiceRequestPreset.Water }, (HttpStatusCode)429);

        // The replaced request is nobody's to claim or answer; the whole bill is claimed and brought.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{hostShare}/claim", null, HttpStatusCode.OK);
        await PostAsync(waiter, $"/api/service-requests/{hostShare}/acknowledge", null, HttpStatusCode.OK);
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{hostWhole}/claim", null, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await PostAsync(waiter, $"/api/service-requests/{hostWhole}/acknowledge", null, HttpStatusCode.OK);

        // D: the scan nobody vouched for calls twice; the waiter claims one, walks over, finds nobody and turns the
        // tab away - which answers both.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var phantomWater = await RaiseAsync(phantom, phantomTab, new { preset = ServiceRequestPreset.Water });
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        var phantomOther = await RaiseAsync(phantom, phantomTab, new { preset = ServiceRequestPreset.Other, note = "a waiter, please" });
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/calls/{phantomWater}/claim", null, HttpStatusCode.OK);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(
            waiter,
            $"/api/tabs/{phantomTab}/reject",
            new { clientCommandId = Guid.CreateVersion7(), reason = "nobody there" },
            HttpStatusCode.OK);

        var names = new Dictionary<Guid, string>
        {
            [branch.VenueId] = "venue",
            [branch.BranchId] = "branch",
            [branch.WaiterId] = "staff:waiter",
            [branch.ManagerId] = "staff:manager",
            [tab.TabId] = "tab",
            [tab.TableId] = "table:1",
            [scanTable] = "table:2",
            [tab.SessionId] = "sitting",
            [tab.HostParticipantId] = "participant:host",
            [ani] = "participant:ani",
            [phantomTab] = "tab:phantom",
            [water] = "call:water",
            [napkins] = "call:napkins",
            [hostShare] = "call:host-share",
            [aniShare] = "call:ani-share",
            [hostWhole] = "call:host-whole",
            [hostPlain] = "call:host-plain",
            [aniOther] = "call:ani-other",
            [phantomWater] = "call:phantom-water",
            [phantomOther] = "call:phantom-other",
            [menu.Coffee] = "menu:coffee",
            [menu.Khachapuri] = "menu:khachapuri",
            [menu.Wine] = "menu:wine",
        };

        var actual = await SnapshotAsync(factory, branch, [tab.TabId, phantomTab], names, startedAt);

        await AssertMatchesRecordAsync(actual);
    }

    private static async Task<Guid> RaiseAsync(HttpClient phone, Guid tabId, object body) =>
        (await PostAsync(phone, $"/api/tabs/{tabId}/service-requests", body, HttpStatusCode.Created))
            .GetProperty("serviceRequestId").GetGuid();

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object? body, HttpStatusCode expected)
    {
        var response = body is null
            ? await client.PostAsync(url, content: null)
            : await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == expected, $"POST {url} answered {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> QrTokenAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DiningTables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.QrToken).SingleAsync();
    }

    private static async Task<JsonElement> ScanAsync(YallaApiFactory factory, string qrToken, string deviceId)
    {
        using var client = factory.CreateClient();

        return await PostAsync(client, "/api/tabs/open", new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7() }, HttpStatusCode.OK);
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
        IReadOnlyList<Guid> tabIds,
        Dictionary<Guid, string> names,
        DateTime startedAt)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var calls = await db.ServiceRequests.AsNoTracking().Where(r => r.BranchId == branch.BranchId).ToListAsync();
        var orders = await db.TabOrders.AsNoTracking().Where(o => tabIds.Contains(o.TabId)).ToListAsync();

        foreach (var order in orders)
        {
            names.TryAdd(order.Id, "order");
        }

        var participants = await db.TabParticipants.AsNoTracking().Where(p => tabIds.Contains(p.TabId)).ToListAsync();

        foreach (var participant in participants)
        {
            names.TryAdd(participant.Id, $"participant:{participant.DisplayName}");
        }

        var sittings = await db.TableSessions.AsNoTracking().Where(s => s.BranchId == branch.BranchId).ToListAsync();

        foreach (var sitting in sittings)
        {
            names.TryAdd(sitting.Id, "sitting:phantom");
        }

        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            names.TryAdd(entry.DeviceId!.Value, "device:waiter");
        }

        var keys = calls.Select(c => c.Id).Concat(tabIds).Concat(orders.Select(o => o.Id)).Select(id => id.ToString()).ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var streams = await db.TabEvents.AsNoTracking()
            .Where(e => tabIds.Contains(e.TabId))
            .ToListAsync();
        var changes = await db.TableStateChanges.AsNoTracking().Where(c => c.BranchId == branch.BranchId).ToListAsync();

        // The change stream's number is the database's, shared with every other test in the class: from 1 at this
        // branch's first row, which keeps the order the floor reads them in.
        var firstChange = changes.Count == 0 ? 0L : changes.Min(c => c.Sequence);
        var tabs = await db.Tabs.AsNoTracking().Where(t => tabIds.Contains(t.Id)).ToListAsync();

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

        foreach (var tabId in tabIds)
        {
            Section(
                text,
                $"TabEvents of {names[tabId]}",
                streams.Where(e => e.TabId == tabId).OrderBy(e => e.Sequence),
                Normalised,
                sorted: false);
        }

        Section(
            text,
            "TableStateChanges",
            changes,
            Normalised,
            sorted: true,
            adjust: row => row["sequence"] = row["sequence"]!.GetValue<long>() - firstChange + 1L);
        Section(text, "ServiceRequests", calls, Normalised, sorted: true);
        Section(text, "Tabs", tabs, Normalised, sorted: true);
        Section(text, "TableSessions", sittings, Normalised, sorted: true);

        return text.ToString();
    }

    /// <summary>
    /// One table: each row rendered, its ids and instants named, then its arrays sorted - after the naming, so the
    /// order does not depend on the random part of an id - and the rows sorted the same way unless their order is the
    /// contract.
    /// </summary>
    private static void Section<T>(
        StringBuilder text,
        string title,
        IEnumerable<T> rows,
        Func<string, string> normalise,
        bool sorted,
        Action<JsonObject>? adjust = null)
    {
        var rendered = rows
            .Select(row =>
            {
                var node = SortArrays(JsonNode.Parse(normalise(RenderRow(row!))))!.AsObject();
                adjust?.Invoke(node);

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

            if (name == "itemNames" && value is JsonValue names && names.TryGetValue<string>(out var list))
            {
                result[name] = string.Join(", ", list.Split(", ").Order(StringComparer.Ordinal));
                continue;
            }

            result[name] = value?.DeepClone();
        }

        return result.ToJsonString(Render);
    }

    /// <summary>Sorts every array by its rendering: lines made in one millisecond read back in no particular order.</summary>
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
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "CallSideEffects.approved.txt");

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
