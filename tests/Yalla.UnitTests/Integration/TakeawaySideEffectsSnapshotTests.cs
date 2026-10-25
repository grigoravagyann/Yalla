using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Characterisation: every side-effect row a scripted takeaway lifecycle writes, through the real host, compared with the
/// record in <c>Snapshots/TakeawaySideEffects.approved.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written on the code as it stood before the side effects of takeaway orders moved into domain-event handlers
/// (<c>docs/domain-events.md</c>), and committed with its snapshot first, so the refactor is held to it: every path of
/// the vertical - placed (and its replay), auto-accepted on placing, accepted (and its replay), declined with a reason,
/// cancelled by the guest (and its replay), declined by the sweep for no answer, accepted by the sweep at a branch that
/// turned auto-accept on, marked ready, collected, and a no-show that is the guest's third and blocks them, cancelling
/// a booking still to come - writes the same activity entries (codes, summaries, attribution, device labels), the same
/// outbox keys and payloads (the guest's pushes, the staff push, the block's notice), the same diner feed rows, the same
/// change-stream rows, and leaves the orders, their lines, the block and the booking in the same state.
/// </para>
/// <para>
/// Ids are rendered by name (the test knows what it made) and every other id as <c>&lt;guid&gt;</c>; instants as an
/// offset from the start of the script; the three-digit order codes as <c>T-###</c> (each row names its order beside
/// it). Rows of one table are sorted by their rendering, except the feed, which reads in the order it was numbered.
/// Regenerate on purpose only:
/// <c>YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TakeawaySideEffectsSnapshotTests"</c>.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TakeawaySideEffectsSnapshotTests(SqlServerFixture fixture)
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

    [SkippableFact]
    public async Task A_scripted_takeaway_lifecycle_writes_exactly_the_side_effects_on_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // The test drives the sweep itself, and reads the outbox as it was written: nothing sends meanwhile.
        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("Outbox:Enabled", "false")
            .StartingAtYerevan(new TimeOnly(15, 0));
        var startedAt = factory.Clock.UtcNow;

        var (aniToken, aniPhone) = await SignInVerifiedDinerAsync(factory);

        AuthBranch counter;
        AuthBranch auto;
        TestMenu menu;
        TestMenu autoMenu;
        Guid cook;
        Guid aniUserId;
        Guid booking;
        string bookingCode;
        long changesBefore;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            aniUserId = await db.DinerUsers.Where(d => d.PhoneE164 == aniPhone).Select(d => d.Id).SingleAsync();

            counter = await AuthTestData.CreateBranchAsync(db);
            auto = await AuthTestData.CreateBranchAsync(db);

            foreach (var branch in new[] { counter, auto })
            {
                await ReviewTestData.SavePolicyAsync(db, branch.BranchId, now, acceptsOnlineBookings: true);
            }

            menu = await TestMenuBuilder.CreateAsync(db, counter.BranchId);
            autoMenu = await TestMenuBuilder.CreateAsync(db, auto.BranchId);
            cook = await AuthTestData.SeedKitchenAsync(db, counter);

            // Both take takeaway; the second starts by answering by hand and turns auto-accept on mid-script.
            foreach (var id in new[] { counter.BranchId, auto.BranchId })
            {
                var branch = await db.Branches.FirstAsync(b => b.Id == id);
                branch.SetTakeaway(true, false, 20, 30, 8);
            }

            await db.SaveChangesAsync();

            // The guest's history: two takeaway orders left at the counter at two other venues in the last 72 hours,
            // so the no-show at the end of the script is the third and blocks her.
            foreach (var hoursAgo in new[] { 50, 20 })
            {
                var elsewhere = await AuthTestData.CreateBranchAsync(db);
                var dish = (await TestMenuBuilder.CreateAsync(db, elsewhere.BranchId)).Khachapuri;
                var readyAt = now.AddHours(-hoursAgo);
                var local = Yalla.Domain.Venues.BranchTime.ToLocal(readyAt, "Asia/Yerevan");

                var missed = new TakeawayOrder(
                    elsewhere.BranchId,
                    aniUserId,
                    $"T-{Random.Shared.Next(100, 1000)}",
                    DateOnly.FromDateTime(local),
                    "Ani",
                    aniPhone,
                    pickupAsap: false,
                    readyAt,
                    TimeOnly.FromDateTime(local),
                    note: null,
                    [new TakeawayLineInput(dish, "Adjarian khachapuri", TestMenu.KhachapuriAmd, 1)],
                    Guid.CreateVersion7(),
                    readyAt.AddMinutes(-30));

                missed.Accept(readyAt, elsewhere.WaiterId, readyAt.AddMinutes(-29));
                missed.MarkReady(elsewhere.WaiterId, readyAt);
                missed.MarkNoShow(elsewhere.WaiterId, readyAt.AddMinutes(31));

                // History, written straight to the rows: what happened then was told then.
                missed.DequeueEvents();
                db.TakeawayOrders.Add(missed);
            }

            // A booking of hers three days on at the counter's branch, which the block cancels.
            var policy = await db.Branches.AsNoTracking().Where(b => b.Id == counter.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
            var startUtc = now.AddDays(3);
            var localStart = Yalla.Domain.Venues.BranchTime.ToLocal(startUtc, "Asia/Yerevan");
            bookingCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var reservation = Reservation.Create(
                branchId: counter.BranchId,
                diningTableId: counter.TableIds[1],
                startUtc: startUtc,
                localDate: DateOnly.FromDateTime(localStart),
                localStartTime: TimeOnly.FromDateTime(localStart),
                partySize: 2,
                guestName: "Ani",
                guestPhone: aniPhone,
                code: bookingCode,
                policy: policy,
                dinerUserId: aniUserId,
                phoneUnverified: false,
                bookerPhoneE164: aniPhone);
            db.Reservations.Add(reservation);

            await db.SaveChangesAsync();
            booking = reservation.Id;

            changesBefore = await db.TableStateChanges
                .Where(c => c.BranchId == counter.BranchId)
                .MaxAsync(c => (long?)c.Sequence) ?? 0L;
        }

        using var ani = factory.CreateClientWithToken(aniToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, counter));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, counter, cook));
        using var autoWaiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, auto));

        // A: two khachapuri and a coffee as soon as possible, with a note - and the app's retry answers the same order.
        var aBody = Order(counter, menu, new { asap = true }, "No onion, separate bag", (menu.Khachapuri, 2), (menu.Coffee, 1));
        var a = await IdAsync(ani, "/api/diner/takeaway-orders", aBody);
        Assert.True((await PostAsync(ani, "/api/diner/takeaway-orders", aBody)).GetProperty("wasReplay").GetBoolean());

        // B, C, D at the counter; F at the second branch while it still answers by hand.
        var b = await IdAsync(ani, "/api/diner/takeaway-orders", Order(counter, menu, new { asap = true }, null, (menu.Khachapuri, 1)));
        var c = await IdAsync(ani, "/api/diner/takeaway-orders", Order(counter, menu, new { asap = true }, null, (menu.Coffee, 2)));
        var d = await IdAsync(ani, "/api/diner/takeaway-orders", Order(counter, menu, new { asap = true }, null, (menu.Coffee, 1)));

        // A second later, so the sweep - oldest deadline first - comes to D before F, and the feed reads in that order.
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        var f =await IdAsync(ani, "/api/diner/takeaway-orders", Order(auto, autoMenu, new { asap = true }, null, (autoMenu.Khachapuri, 1)));

        // The second branch turns auto-accept on: E is accepted the moment it is placed.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var branch = await db.Branches.FirstAsync(x => x.Id == auto.BranchId);
            branch.SetTakeaway(true, true, 20, 30, 8);
            await db.SaveChangesAsync();
        }

        var e = await IdAsync(ani, "/api/diner/takeaway-orders", Order(auto, autoMenu, new { asap = true }, null, (autoMenu.Coffee, 3)));

        // B declined with a reason; C cancelled by Ani, and her retry answers as a replay.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/takeaway-orders/{b}/decline", new { reason = " Out of khachapuri " });
        await PostAsync(ani, $"/api/diner/takeaway-orders/{c}/cancel", new { });
        Assert.True((await PostAsync(ani, $"/api/diner/takeaway-orders/{c}/cancel", new { })).GetProperty("wasReplay").GetBoolean());

        // A accepted for 25 minutes on; a second tablet's accept is the same answer.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var readyA = startedAt.AddMinutes(25);
        await PostAsync(waiter, $"/api/takeaway-orders/{a}/accept", new { readyAt = readyA });
        Assert.True((await PostAsync(waiter, $"/api/takeaway-orders/{a}/accept", new { readyAt = readyA.AddMinutes(5) }))
            .GetProperty("wasReplay").GetBoolean());

        // Six minutes in, nobody answered D or F: the sweep declines D and accepts F (its branch now accepts itself).
        factory.Clock.Advance(TimeSpan.FromMinutes(4));

        using (var scope = factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<TakeawayExpirySweep>().RunOnceAsync() >= 2);
        }

        // A up, and collected in cash.
        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        await PostAsync(kitchen, $"/api/takeaway-orders/{a}/ready", new { });
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await PostAsync(waiter, $"/api/takeaway-orders/{a}/collect", new { method = (int)TakeawayPaymentMethod.Cash });

        // G: accepted, made, and nobody comes - the third no-show blocks Ani and cancels her booking; a second tablet
        // marking it again is the same answer and no second block.
        var g = await IdAsync(ani, "/api/diner/takeaway-orders", Order(counter, menu, new { asap = true }, null, (menu.Khachapuri, 1)));
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostAsync(waiter, $"/api/takeaway-orders/{g}/accept", new { readyAt = startedAt.AddMinutes(40) });
        factory.Clock.Advance(TimeSpan.FromMinutes(17));
        await PostAsync(kitchen, $"/api/takeaway-orders/{g}/ready", new { });
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        await PostAsync(waiter, $"/api/takeaway-orders/{g}/no-show", new { });
        Assert.True((await PostAsync(waiter, $"/api/takeaway-orders/{g}/no-show", new { })).GetProperty("wasReplay").GetBoolean());

        var names = new Dictionary<Guid, string>
        {
            [counter.VenueId] = "venue:counter",
            [counter.BranchId] = "branch:counter",
            [counter.WaiterId] = "staff:waiter",
            [counter.ManagerId] = "staff:manager",
            [auto.VenueId] = "venue:auto",
            [auto.BranchId] = "branch:auto",
            [auto.WaiterId] = "staff:auto-waiter",
            [auto.ManagerId] = "staff:auto-manager",
            [cook] = "staff:cook",
            [aniUserId] = "diner:ani",
            [booking] = "booking",
            [counter.TableIds[1]] = "table:booked",
            [a] = "takeaway:A",
            [b] = "takeaway:B",
            [c] = "takeaway:C",
            [d] = "takeaway:D",
            [e] = "takeaway:E",
            [f] = "takeaway:F",
            [g] = "takeaway:G",
            [menu.Coffee] = "menu:coffee",
            [menu.Khachapuri] = "menu:khachapuri",
            [autoMenu.Coffee] = "menu:auto-coffee",
            [autoMenu.Khachapuri] = "menu:auto-khachapuri",
        };

        var text = new Dictionary<string, string>
        {
            [aniPhone] = "<ani phone>",
            [bookingCode] = "<booking code>",
        };

        var actual = await SnapshotAsync(factory, [counter, auto], aniUserId, booking, changesBefore, names, text, startedAt);

        await AssertMatchesRecordAsync(actual);
    }

    private static object Order(AuthBranch branch, TestMenu menu, object pickup, string? note, params (Guid Item, int Quantity)[] lines) => new
    {
        branchId = branch.BranchId,
        pickup,
        note,
        lines = lines.Select(l => new { menuItemId = l.Item, quantity = l.Quantity }).ToArray(),
        clientCommandId = Guid.CreateVersion7(),
    };

    private static async Task<Guid> IdAsync(HttpClient client, string url, object body) =>
        (await PostAsync(client, url, body)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"POST {url} answered {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> SnapshotAsync(
        YallaApiFactory factory,
        AuthBranch[] branches,
        Guid aniUserId,
        Guid booking,
        long changesBefore,
        Dictionary<Guid, string> names,
        Dictionary<string, string> text,
        DateTime startedAt)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branchIds = branches.Select(b => b.BranchId).ToList();
        var orders = await db.TakeawayOrders.AsNoTracking().Where(o => branchIds.Contains(o.BranchId)).ToListAsync();
        var orderIds = orders.Select(o => o.Id).ToList();
        var lines = await db.TakeawayOrderLines.AsNoTracking().Where(l => orderIds.Contains(l.TakeawayOrderId)).ToListAsync();

        foreach (var line in lines)
        {
            names[line.Id] = $"line:{names[line.TakeawayOrderId][9..]}:{line.Position}";
        }

        var blocks = await db.BookingBlocks.AsNoTracking().Where(x => x.DinerUserId == aniUserId).ToListAsync();

        foreach (var block in blocks)
        {
            names[block.Id] = "block";
        }

        var activity = await db.ActivityEntries.AsNoTracking().Where(x => x.BranchId != null && branchIds.Contains(x.BranchId.Value)).ToListAsync();

        // The tablets, named by who acted on them.
        foreach (var entry in activity.Where(x => x.DeviceId is not null))
        {
            var who = entry.StaffMemberId is { } staff && names.TryGetValue(staff, out var name) ? name[6..] : "?";
            names.TryAdd(entry.DeviceId!.Value, $"device:{who}");
        }

        // Anything about the orders, the booking or the block: the guest's pushes, the staff push, the block's notice.
        var keys = orderIds.Append(booking).Concat(blocks.Select(x => x.Id)).Select(id => id.ToString()).ToList();
        var outbox = (await db.OutboxMessages.AsNoTracking().Where(m => m.CreatedAtUtc >= startedAt).ToListAsync())
            .Where(m => keys.Any(k => m.IdempotencyKey.Contains(k, StringComparison.OrdinalIgnoreCase)
                                      || m.PayloadJson.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // In the order the feed numbered them; the number itself is the database's, shared with every other test.
        var feed = await db.DinerNotifications.AsNoTracking()
            .Where(n => n.DinerUserId == aniUserId)
            .OrderBy(n => n.Sequence)
            .ToListAsync();
        var changes = await db.TableStateChanges.AsNoTracking()
            .Where(x => branchIds.Contains(x.BranchId) && x.Sequence > changesBefore)
            .ToListAsync();
        var reservations = await db.Reservations.AsNoTracking().Where(r => r.Id == booking).ToListAsync();

        // The builders suffix the venues' and the branches' names to keep them unique across tests.
        var places = await db.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .Select(x => new { x.Id, Branch = x.Name, Venue = x.Venue.Name })
            .ToListAsync();

        foreach (var place in places)
        {
            var which = place.Id == branches[0].BranchId ? "counter" : "auto";
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
        Section(snapshot, "DinerNotifications", feed, Normalised, sorted: false);
        Section(snapshot, "TableStateChanges", changes, Normalised, sorted: true);
        Section(snapshot, "TakeawayOrders", orders, Normalised, sorted: true);
        Section(snapshot, "TakeawayOrderLines", lines, Normalised, sorted: true);
        Section(snapshot, "BookingBlocks", blocks, Normalised, sorted: true);
        Section(snapshot, "Reservations", reservations, Normalised, sorted: true);

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

                return $"T{(at - startedAt).TotalSeconds:+0;-0;+0}s";
            });

        // A day on the branch's calendar - a service day, a block's last day - as days from the script's own.
        var today = DateOnly.FromDateTime(Yalla.Domain.Venues.BranchTime.ToLocal(startedAt, "Asia/Yerevan"));

        return Regex.Replace(
            text,
            @"(?<![\d-])\d{4}-\d{2}-\d{2}(?![\dT])",
            m => $"D{DateOnly.ParseExact(m.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayNumber - today.DayNumber:+0;-0;+0}");
    }

    private static async Task AssertMatchesRecordAsync(string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(RepoFile(here))!, "Snapshots", "TakeawaySideEffects.approved.txt");

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
