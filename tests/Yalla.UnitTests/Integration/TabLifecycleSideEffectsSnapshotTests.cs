using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted tab lifecycle writes - scans opening and joining, the host letting
/// people on and turning them away, the waiter admitting a table or turning it away, a staff-opened tab, a booker let
/// straight on, the roster's own edits, the host moved, the bill presented, and replays by <c>clientCommandId</c> -
/// through the real host, with the end state it leaves, compared with the record in
/// <c>Snapshots/TabLifecycleSideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of the tab lifecycle moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: the same
/// activity entries (codes, reasons, summaries, attribution, device labels), the same outbox rows (the host's join
/// requests, the guest's approval), the same diner feed rows, the same tab-stream events in the same sequence, the same
/// change-stream rows in the same order - and the same end state of the tabs, their participants, invitations,
/// sittings and the admission commands on record.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script, local dates as days from the branch's today. Rows of one table are sorted by
/// their rendering, except the tabs' streams, which read in sequence, and the change stream, which reads in the order
/// the floor reads it (its number given as a rank: the database's own is shared with every other test). Regenerate on
/// purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TabLifecycleSideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabLifecycleSideEffectsSnapshotTests(SqlServerFixture fixture)
{
    private static readonly JsonSerializerOptions Render = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Columns that are the outbox loop's, not the writer's, and the invitation's random value.</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "rowVersion", "attemptCount", "lockedUntilUtc", "lockedBy", "sentAtUtc", "lastError",
        "deadLetteredAtUtc", "isSent", "isDeadLettered", "token",
    };

    [SkippableFact]
    public async Task A_scripted_tab_lifecycle_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        AuthBranch branch;
        string timeZoneId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 4);
            timeZoneId = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.TimeZoneId).FirstAsync();
        }

        var (table1, table2, table3, table4) = (branch.TableIds[0], branch.TableIds[1], branch.TableIds[2], branch.TableIds[3]);

        // Hasmik has an account whose number was never proved, so her scan waits for a waiter and her phone can be
        // pushed. Ani's number is proved. Lilit booked table 4.
        var (hasmikToken, hasmikUser) = await DinerFeedTestData.RegisterUnprovedAsync(factory);
        var (aniToken, aniUser) = await DinerFeedTestData.SignInDinerAsync(factory);
        var (lilitToken, lilitUser) = await DinerFeedTestData.SignInDinerAsync(factory);

        using var anonymous = factory.CreateClient();
        using var hasmikApp = factory.CreateClientWithToken(hasmikToken);
        using var aniApp = factory.CreateClientWithToken(aniToken);
        using var lilitApp = factory.CreateClientWithToken(lilitToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var commands = new Dictionary<Guid, string>();

        Guid Command(string name)
        {
            var id = Guid.CreateVersion7();
            commands[id] = $"cmd:{name}";

            return id;
        }

        // ---- Table 1: Hasmik's scan seats the free table and opens a tab that waits for a waiter; her retry answers
        // with the same tab.
        var hasmikScan = Command("scan-hasmik");
        var opened = await ScanAsync(hasmikApp, await QrTokenAsync(factory, table1), "phone-hasmik", "Hasmik", hasmikScan);
        var tab1 = TabIdOf(opened);
        var hasmik = MeOf(opened);
        using var hasmikPhone = factory.CreateClientWithToken(TokenOf(opened));

        Step(factory);
        var replayed = await ScanAsync(hasmikApp, await QrTokenAsync(factory, table1), "phone-hasmik", "Hasmik", hasmikScan);
        Assert.True(replayed.GetProperty("wasReplay").GetBoolean());

        // Ani and Bob scan the same table: both wait for Hasmik. Hasmik's phone is told about each.
        Step(factory);
        var aniJoin = await ScanAsync(aniApp, await QrTokenAsync(factory, table1), "phone-ani", "Ani", Command("scan-ani"));
        var ani = MeOf(aniJoin);
        using var aniPhone = factory.CreateClientWithToken(TokenOf(aniJoin));

        Step(factory);
        var bobJoin = await ScanAsync(anonymous, await QrTokenAsync(factory, table1), "phone-bob", "Bob", Command("scan-bob"));
        var bob = MeOf(bobJoin);

        // Hasmik lets Ani on (her app is told), taps it again (nothing), and turns Bob away.
        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/participants/{ani}/approve", new { });
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/participants/{ani}/approve", new { });

        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/participants/{bob}/reject", new { });

        // Bob scans again from the same phone: on the roster again, pending, and Hasmik is not woken a second time.
        Step(factory);
        var bobAgain = MeOf(await ScanAsync(anonymous, await QrTokenAsync(factory, table1), "phone-bob", "Bob", Command("scan-bob-again")));

        // The waiter lets the table order; the same tap again answers as done, and a second waiter's tap writes nothing.
        Step(factory);
        var admit1 = Command("admit-1");
        await PostAsync(waiter, $"/api/tabs/{tab1}/admit", new { clientCommandId = admit1 });
        await PostAsync(waiter, $"/api/tabs/{tab1}/admit", new { clientCommandId = admit1 });
        await PostAsync(waiter, $"/api/tabs/{tab1}/admit", new { clientCommandId = Command("admit-1-again") });

        // Hasmik invites Cara, who joins by the invitation and waits for her.
        Step(factory);
        var invitation = await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/join-tokens", new { });
        Step(factory);
        var caraJoin = await PostAsync(
            anonymous,
            "/api/tabs/join",
            new { joinToken = invitation.GetProperty("token").GetString(), deviceId = "phone-cara", displayName = "Cara" });
        var cara = MeOf(caraJoin);

        // The roster's own edits: Ani may pay, Ani renames herself, the split changes, Cara is taken off.
        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/participants/{ani}/permissions", new { canOrder = true, canSeeTableTotal = true, canPay = true });
        Step(factory);
        await PostAsync(aniPhone, $"/api/tabs/{tab1}/display-name", new { displayName = "Ani B." });
        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/settlement-mode", new { settlementMode = 2 });
        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/participants/{cara}/remove", new { });

        // The waiter moves the host to Ani; Ani then leaves, handing the tab back to Hasmik.
        Step(factory);
        await PostAsync(waiter, $"/api/tabs/{tab1}/reassign-host", new { newHostParticipantId = ani });
        Step(factory);
        await PostAsync(aniPhone, $"/api/tabs/{tab1}/leave", new { });

        // A second invitation is live when the waiter presents the bill: it stops working.
        Step(factory);
        await PostAsync(hasmikPhone, $"/api/tabs/{tab1}/join-tokens", new { });
        Step(factory);
        await PostAsync(waiter, $"/api/tabs/{tab1}/closing", new { });

        // ---- Table 2: a phantom scan seats the free table; the waiter turns it away (the table goes free), the same
        // tap again answers as done, and admitting it afterwards is refused.
        Step(factory);
        var phantom = await ScanAsync(anonymous, await QrTokenAsync(factory, table2), "phone-phantom", null, Command("scan-phantom"));
        var tab2 = TabIdOf(phantom);
        var phantomHost = MeOf(phantom);

        Step(factory);
        var reject2 = Command("reject-2");
        await PostAsync(waiter, $"/api/tabs/{tab2}/reject", new { clientCommandId = reject2, reason = "nobody at the table" });
        await PostAsync(waiter, $"/api/tabs/{tab2}/reject", new { clientCommandId = reject2, reason = "nobody at the table" });
        await PostAsync(waiter, $"/api/tabs/{tab2}/admit", new { clientCommandId = Command("admit-2-refused") }, HttpStatusCode.Conflict);

        // ---- Table 3: the waiter seats a walk-in; a scan onto it waits and is turned away with no reason - the party is
        // there, so only the tab goes. The waiter then opens a tab on the sitting (twice: one tab) and Narine's phone,
        // the first onto it, hosts it.
        Step(factory);
        await PostAsync(
            waiter,
            $"/api/branches/{branch.BranchId}/tables/{table3}/seat-walk-in",
            new { partySize = 3, clientCommandId = Command("seat-3") });

        Step(factory);
        var stray = await ScanAsync(anonymous, await QrTokenAsync(factory, table3), "phone-stray", "Stray", Command("scan-stray"));
        var tab3a = TabIdOf(stray);
        var strayHost = MeOf(stray);

        Step(factory);
        await PostAsync(waiter, $"/api/tabs/{tab3a}/reject", new { clientCommandId = Command("reject-3") });

        Step(factory);
        var staffOpen = Command("open-3");
        var staffTab = await PostAsync(waiter, $"/api/branches/{branch.BranchId}/tables/{table3}/open-tab", new { clientCommandId = staffOpen });
        var tab3b = staffTab.GetProperty("tabId").GetGuid();
        await PostAsync(waiter, $"/api/branches/{branch.BranchId}/tables/{table3}/open-tab", new { clientCommandId = staffOpen });

        Step(factory);
        var narine = MeOf(await ScanAsync(anonymous, await QrTokenAsync(factory, table3), "phone-narine", "Narine", Command("scan-narine")));

        // ---- Table 4: Lilit's booking, seated by the waiter. A friend scans first and hosts a tab that waits; Lilit
        // arrives with the code and is let straight on, which lets the table order.
        Step(factory);
        var booking = await BookAsync(lilitApp, factory, branch, table4);
        await StartBookingSoonAsync(factory, booking);
        await PostAsync(
            waiter,
            $"/api/branches/{branch.BranchId}/tables/{table4}/seat-reservation",
            new { reservationId = booking, clientCommandId = Command("seat-4") });

        Step(factory);
        var friend = await ScanAsync(anonymous, await QrTokenAsync(factory, table4), "phone-friend", "Friend", Command("scan-friend"));
        var tab4 = TabIdOf(friend);
        var friendHost = MeOf(friend);

        Step(factory);
        var lilitJoin = await PostAsync(
            lilitApp,
            "/api/tabs/open-by-booking",
            new { bookingCode = await BookingCodeAsync(factory, booking), deviceId = "phone-lilit", clientCommandId = Command("open-by-booking"), displayName = "Lilit" });
        var lilit = MeOf(lilitJoin);

        var today = Yalla.Application.Reservations.BranchZone.For(timeZoneId).LocalDateAt(factory.Clock.UtcNow);

        var names = new Dictionary<Guid, string>(commands)
        {
            [branch.VenueId] = "venue",
            [branch.BranchId] = "branch",
            [branch.WaiterId] = "staff:waiter",
            [branch.ManagerId] = "staff:manager",
            [table1] = "table:1",
            [table2] = "table:2",
            [table3] = "table:3",
            [table4] = "table:4",
            [tab1] = "tab:1",
            [tab2] = "tab:2",
            [tab3a] = "tab:3-turned-away",
            [tab3b] = "tab:3-staff",
            [tab4] = "tab:4",
            [hasmik] = "participant:hasmik",
            [ani] = "participant:ani",
            [bob] = "participant:bob",
            [bobAgain] = "participant:bob-again",
            [cara] = "participant:cara",
            [phantomHost] = "participant:phantom",
            [strayHost] = "participant:stray",
            [narine] = "participant:narine",
            [friendHost] = "participant:friend",
            [lilit] = "participant:lilit",
            [hasmikUser] = "diner:hasmik",
            [aniUser] = "diner:ani",
            [lilitUser] = "diner:lilit",
            [booking] = "booking",
        };

        var actual = await SnapshotAsync(
            factory, branch, [tab1, tab2, tab3a, tab3b, tab4], [hasmikUser, aniUser], names, startedAt, today);

        await AssertMatchesRecordAsync(actual);
    }

    /// <summary>A few seconds on: every step at its own instant, well inside the life of the tokens minted at the start.</summary>
    private static void Step(YallaApiFactory factory) => factory.Clock.Advance(TimeSpan.FromSeconds(5));

    private static Guid TabIdOf(JsonElement access) => access.GetProperty("tab").GetProperty("tabId").GetGuid();

    private static Guid MeOf(JsonElement access) => access.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid();

    private static string TokenOf(JsonElement access) => access.GetProperty("token").GetProperty("accessToken").GetString()!;

    private static Task<JsonElement> ScanAsync(HttpClient client, string qrToken, string deviceId, string? displayName, Guid clientCommandId) =>
        PostAsync(client, "/api/tabs/open", new { qrToken, deviceId, clientCommandId, displayName });

    private async Task<string> QrTokenAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DiningTables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.QrToken).SingleAsync();
    }

    /// <summary>Books the table at 18:00 two days out, through the real endpoint, so the booking is Lilit's by the real rule.</summary>
    private static async Task<Guid> BookAsync(HttpClient diner, YallaApiFactory factory, AuthBranch branch, Guid tableId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        var created = await PostAsync(
            diner,
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date = DateOnly.FromDateTime(localNow).AddDays(2).ToString("yyyy-MM-dd"),
                time = "18:00",
                partySize = 2,
                guestName = "Lilit Test",
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
            },
            HttpStatusCode.Created);

        return created.GetProperty("id").GetGuid();
    }

    /// <summary>Moves the booking to start ten minutes from now, keeping its length: the party has arrived.</summary>
    private async Task StartBookingSoonAsync(YallaApiFactory factory, Guid bookingId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var booked = await db.Reservations.AsNoTracking()
            .Where(r => r.Id == bookingId)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .SingleAsync();

        var start = factory.Clock.UtcNow.AddMinutes(10);
        var end = start + (booked.EndUtc - booked.StartUtc);

        await db.Reservations
            .Where(r => r.Id == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.StartUtc, start).SetProperty(r => r.EndUtc, end));
    }

    private async Task<string> BookingCodeAsync(YallaApiFactory factory, Guid bookingId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.Reservations.AsNoTracking().Where(r => r.Id == bookingId).Select(r => r.Code).SingleAsync();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body, HttpStatusCode? expected = null)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            expected is { } status ? response.StatusCode == status : response.IsSuccessStatusCode,
            $"POST {url} answered {(int)response.StatusCode}: {text}");

        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        List<Guid> tabIds,
        List<Guid> dinerUserIds,
        Dictionary<Guid, string> names,
        DateTime startedAt,
        DateOnly today)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        // The sittings, named by their table and in the order they were seated there.
        var sittings = await db.TableSessions.AsNoTracking().Where(s => s.BranchId == branch.BranchId).ToListAsync();

        foreach (var group in sittings.GroupBy(s => s.DiningTableId))
        {
            var index = 0;

            foreach (var sitting in group.OrderBy(s => s.SeatedAtUtc).ThenBy(s => s.Id))
            {
                names[sitting.Id] = $"sitting:{names[sitting.DiningTableId][6..]}{(index++ == 0 ? string.Empty : $"-{index}")}";
            }
        }

        var sessionIds = sittings.Select(s => s.Id).ToList();
        var participants = await db.TabParticipants.AsNoTracking().Where(p => tabIds.Contains(p.TabId)).ToListAsync();
        var participantIds = participants.Select(p => p.Id).ToList();
        var invitations = await db.TabJoinTokens.AsNoTracking().Where(t => tabIds.Contains(t.TabId)).ToListAsync();
        var activity = await db.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branch.BranchId).ToListAsync();

        // The waiter's tablet, named by who acted on it.
        foreach (var entry in activity.Where(e => e.DeviceId is not null))
        {
            names.TryAdd(entry.DeviceId!.Value, entry.StaffMemberId == branch.WaiterId ? "device:waiter" : "device:other");
        }

        var keys = tabIds
            .Concat(participantIds)
            .Concat(sessionIds)
            .Select(id => id.ToString())
            .ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // In the order the feed numbered them; the number itself is the database's, shared with every other test.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => dinerUserIds.Contains(n.DinerUserId))
            .OrderBy(n => n.Sequence)
            .ToListAsync();

        var streams = await db.TabEvents.AsNoTracking().Where(e => tabIds.Contains(e.TabId)).ToListAsync();

        // The change stream in the order the floor reads it; its number as a rank, from 1 at this branch's first row.
        var changes = await db.TableStateChanges.AsNoTracking()
            .Where(c => c.BranchId == branch.BranchId)
            .OrderBy(c => c.Sequence)
            .ToListAsync();
        var rank = changes.Select((c, i) => (c.Sequence, Rank: i + 1L)).ToDictionary(p => p.Sequence, p => p.Rank);

        var tabs = await db.Tabs.AsNoTracking().Where(t => tabIds.Contains(t.Id)).ToListAsync();
        var processed = (await db.ProcessedCommands.AsNoTracking().Where(c => c.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(c => tabIds.Any(t => c.ResponseJson.Contains(t.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var code = await db.Reservations.AsNoTracking().Where(r => r.BranchId == branch.BranchId).Select(r => r.Code).ToListAsync();

        var place = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Branch = b.Name, Venue = b.Venue.Name })
            .FirstAsync();

        string Normalised(string row)
        {
            var text = Normalise(row, names, startedAt, today)
                .Replace(place.Venue, "<venue name>", StringComparison.Ordinal)
                .Replace(place.Branch, "<branch name>", StringComparison.Ordinal);

            return code.Aggregate(text, (t, c) => t.Replace(c, "<booking code>", StringComparison.Ordinal));
        }

        var text = new StringBuilder();
        Section(text, "ActivityEntries", activity, Normalised, sorted: true);
        Section(text, "OutboxMessages", outbox, Normalised, sorted: true);
        Section(text, "DinerNotifications", feed, Normalised, sorted: false, adjust: row => row.Remove("sequence"));

        foreach (var tabId in tabIds)
        {
            Section(text, $"TabEvents of {names[tabId]}", streams.Where(e => e.TabId == tabId).OrderBy(e => e.Sequence), Normalised, sorted: false);
        }

        Section(
            text,
            "TableStateChanges",
            changes,
            Normalised,
            sorted: false,
            adjust: row => row["sequence"] = rank[row["sequence"]!.GetValue<long>()]);
        Section(text, "Tabs", tabs, Normalised, sorted: true);
        Section(text, "TabParticipants", participants, Normalised, sorted: true);
        Section(text, "TabJoinTokens", invitations, Normalised, sorted: true);
        Section(text, "TableSessions", sittings, Normalised, sorted: true);
        Section(text, "ProcessedCommands", processed, Normalised, sorted: true);

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

            result[name] = value?.DeepClone();
        }

        return result.ToJsonString(Render);
    }

    /// <summary>Sorts every array by its rendering: the set is the contract, not the order rows were read in.</summary>
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

        // A fresh id written without dashes (a staff push key, an app lock's resource).
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
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "TabLifecycleSideEffects.approved.txt");

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
