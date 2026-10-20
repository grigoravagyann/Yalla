using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A guest the host hid the total from, over a whole evening, through every read a diner has: the
/// event stream, the tab, the answer to their own order and the split.
/// </summary>
/// <remarks>
/// <para>
/// The stream used to hand every participant the payloads as written, so a guest without the table
/// total could add it back up: every line's price, every payment with the balance left after it, the
/// total the tab closed on. The tab read listed every comp with what it took off, and the answer to
/// ordering a coffee carried the whole tab's totals. <see cref="TabEventRedactorTests"/> and
/// <see cref="TabProjectionTests"/> pin the rules; this pins that every endpoint applies them, to the
/// reader the token names, over rows the real writers wrote - that the host still gets everything,
/// and that what the guest is shown as theirs is what <c>/shares</c> charges them for.
/// </para>
/// <para>
/// The evening: the host hides the total. A waiter keys in a coffee for the table while the host is
/// alone on it. The guest is let on; the host orders two coffees and a bottle shared with the guest,
/// the guest a khachapuri, the waiter two more for the guest on their behalf and one for the table.
/// The waiter voids the first coffee; a manager comps the whole tab, the guest's khachapuri, the
/// host's coffees and the table's khachapuri. A stranger scans the wrong code and waits. The guest asks for water with a note,
/// the host for the whole bill with a tip; the waiter marks the tab closing, takes the guest's cash
/// with a tip, then the rest from the host, which closes it.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabEventVisibilityTests(SqlServerFixture fixture)
{
    /// <summary>Money members that are the table's, never a restricted reader's.</summary>
    private static readonly string[] TableMoney = ["remainingAmd", "totalAmd", "writtenOffAmd"];

    private const long GuestPaysAmd = 3_000L;
    private const long GuestTipAmd = 300L;
    private const long TabCompAmd = 1_000L;
    private const long GuestsCompAmd = 200L;
    private const long HostsCompAmd = 250L;
    private const long TableCompAmd = 150L;

    // A waiter's order is placed and sent to the kitchen in one commit (2026-10-20), so each of the
    // waiter's three orders is followed by its OrderStatusChanged; the phones' two are not. The host scanned
    // with no proved number, so the waiter's first order is also what lets the table in (TabAdmitted, before
    // it) - an event every reader gets whole.
    private static readonly TabEventType[] TheEvening =
    [
        TabEventType.TabAdmitted,
        TabEventType.OrderPlaced,
        TabEventType.OrderStatusChanged,
        TabEventType.ParticipantJoined,
        TabEventType.ParticipantApproved,
        TabEventType.OrderPlaced,
        TabEventType.OrderPlaced,
        TabEventType.OrderPlaced,
        TabEventType.OrderStatusChanged,
        TabEventType.OrderPlaced,
        TabEventType.OrderStatusChanged,
        TabEventType.LineVoided,
        TabEventType.AdjustmentAdded,
        TabEventType.AdjustmentAdded,
        TabEventType.AdjustmentAdded,
        TabEventType.AdjustmentAdded,
        TabEventType.ParticipantJoined,
        TabEventType.ServiceRequested,
        TabEventType.ServiceRequested,
        TabEventType.TabClosing,
        TabEventType.PaymentRecorded,
        TabEventType.PaymentRecorded,
        TabEventType.TabClosed,
    ];

    [SkippableFact]
    public async Task A_guest_without_the_total_reads_every_event_and_none_of_the_tables_money()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var evening = await EveningAsync(factory);

        using var hostClient = factory.CreateClientWithToken(evening.Host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(evening.Guest.AccessToken);
        using var pendingClient = factory.CreateClientWithToken(evening.Stranger.AccessToken);

        var hostStream = await EventsAsync(hostClient, evening.TabId);
        var guestStream = await EventsAsync(guestClient, evening.TabId);
        var pendingStream = await EventsAsync(pendingClient, evening.TabId);

        // Every reader gets every event, in the same places, contiguous from one. A redacted event is
        // still an event: a hole would read as a missed one, and every phone would refetch for ever.
        foreach (var stream in new[] { hostStream, guestStream, pendingStream })
        {
            Assert.Equal(TheEvening, stream.Select(e => e.Type));
            Assert.Equal(Enumerable.Range(1, TheEvening.Length).Select(i => (long)i), stream.Select(e => e.Sequence));
        }

        // ---- The host: everything, every member of every event, exactly as written.
        await AssertHostReadsTheStoredStreamAsync(evening.TabId, hostStream);

        Assert.All(Payloads(hostStream, TabEventType.PaymentRecorded), p => Assert.True(p.TryGetProperty("remainingAmd", out _)));
        Assert.Equal(evening.TotalAmd, Payloads(hostStream, TabEventType.TabClosed).Single().GetProperty("totalAmd").GetInt64());
        Assert.Equal(6, LinesOn(hostStream).Count());

        // ---- The guest: their own lines, their own payment, their own comp. Nothing of the table's.
        var guestView = await ReadAsync(guestClient, $"/api/tabs/{evening.TabId}");
        Assert.False(guestView.GetProperty("tableTotalVisible").GetBoolean());

        // Exactly the lines their bill shows them - their khachapuri, the two the waiter keyed in for
        // them, and the shared bottle - out of six. Not the waiter's khachapuri for the table, though
        // they are charged a part of it: lines for nobody in particular are not itemised without the
        // total.
        var guestsLines = LineIds(guestView.GetProperty("myLines"));
        Assert.Equal(3, guestsLines.Count);
        Assert.DoesNotContain(evening.TableKhachapuriLineId, guestsLines);
        Assert.Equal(guestsLines, LinesOn(guestStream).Order());

        Assert.DoesNotContain(TableMoney, member => Contains(guestStream, member));

        var payments = Payloads(guestStream, TabEventType.PaymentRecorded);
        var (theirs, theHosts) = (payments[0], payments[1]);
        Assert.Equal(GuestPaysAmd, theirs.GetProperty("amountAmd").GetInt64());
        Assert.Equal(GuestTipAmd, theirs.GetProperty("tipAmd").GetInt64());
        Assert.False(theHosts.TryGetProperty("amountAmd", out _));
        Assert.False(theHosts.TryGetProperty("tipAmd", out _));
        Assert.Equal(evening.Host.ParticipantId, theHosts.GetProperty("tabParticipantId").GetGuid());

        // Off the whole tab, off the host's coffees and off the table's khachapuri: that something
        // came off, and nothing more. Off the guest's own khachapuri: all of it.
        var comps = Payloads(guestStream, TabEventType.AdjustmentAdded);
        Assert.Equal(["adjustmentId", "tabOrderLineId"], Members(comps[0]));
        Assert.Equal(GuestsCompAmd, comps[1].GetProperty("amountAmd").GetInt64());
        Assert.Equal("sorry about the wait", comps[1].GetProperty("reason").GetString());
        Assert.Equal(["adjustmentId", "tabOrderLineId"], Members(comps[2]));
        Assert.Equal(["adjustmentId", "tabOrderLineId"], Members(comps[3]));

        // The coffee went on before the guest did, so its void is not theirs to read about.
        var voided = Payloads(guestStream, TabEventType.LineVoided).Single();
        Assert.Equal(evening.TableCoffeeLineId, voided.GetProperty("lineId").GetGuid());
        Assert.False(voided.TryGetProperty("name", out _));
        Assert.False(voided.TryGetProperty("reason", out _));

        Assert.True(Payloads(guestStream, TabEventType.TabClosed).Single().GetProperty("tableSessionClosed").GetBoolean());

        // The host's ask still says what was asked for, and never said how much.
        var hostsAsk = Payloads(guestStream, TabEventType.ServiceRequested)[1];
        Assert.Equal(1, hostsAsk.GetProperty("billScope").GetInt32());
        Assert.Equal(10, hostsAsk.GetProperty("tipPercent").GetInt32());

        // And the sums the host hid are nowhere on it, as any number at all: not the total, not what
        // was left before the host paid it, not the host's coffees, not the comps that were not theirs.
        var guestNumbers = Numbers(guestStream);
        Assert.DoesNotContain(evening.TotalAmd, guestNumbers);
        Assert.DoesNotContain(evening.LeftForTheHostAmd, guestNumbers);
        Assert.DoesNotContain(TestMenu.CoffeeAmd, guestNumbers);
        Assert.DoesNotContain(TabCompAmd, guestNumbers);
        Assert.DoesNotContain(HostsCompAmd, guestNumbers);
        Assert.DoesNotContain(TableCompAmd, guestNumbers);

        // ---- The roster: both joins, as written, for everyone allowed the roster.
        var hostsJoins = Payloads(hostStream, TabEventType.ParticipantJoined);
        Assert.Equal(
            [evening.Guest.ParticipantId, evening.Stranger.ParticipantId],
            hostsJoins.Select(p => p.GetProperty("participantId").GetGuid()));
        Assert.Equal(["phone-guest", "phone-stranger"], hostsJoins.Select(p => p.GetProperty("displayName").GetString()));
        Assert.All(hostsJoins, p => Assert.Equal((int)ParticipantStatus.PendingApproval, p.GetProperty("status").GetInt32()));

        // The guest, without the total, still reads the roster: the stranger's join names them.
        Assert.Equal(
            "phone-stranger",
            Payloads(guestStream, TabEventType.ParticipantJoined)[1].GetProperty("displayName").GetString());

        // ---- The stranger: no lines, no money, no names, no notes, no reasons - but their own.
        Assert.Empty(LinesOn(pendingStream));

        // Their own join is theirs to read, name and all; it is the only event about them.
        var strangersOwn = pendingStream.Single(e =>
            e.Type == TabEventType.ParticipantJoined
            && e.Payload.GetProperty("participantId").GetGuid() == evening.Stranger.ParticipantId);
        Assert.Equal("phone-stranger", strangersOwn.Payload.GetProperty("displayName").GetString());

        // The guest's join is not: that somebody joined, and how they stand, and not who.
        var guestsJoin = Payloads(pendingStream, TabEventType.ParticipantJoined)[0];
        Assert.Equal(evening.Guest.ParticipantId, guestsJoin.GetProperty("participantId").GetGuid());
        Assert.Equal(["canOrder", "canPay", "canSeeTableTotal", "participantId", "status"], Members(guestsJoin));

        string[] notTheStrangers =
            ["amountAmd", "tipAmd", "unitPriceAmd", "displayName", "note", "reason", "kind", "percent", .. TableMoney];
        Assert.DoesNotContain(
            notTheStrangers,
            member => Contains(pendingStream.Where(e => !ReferenceEquals(e, strangersOwn)), member));

        // The guest's approval is still there for them - it moves nothing on their screen - but it
        // does not say who the guest is.
        var approval = Payloads(pendingStream, TabEventType.ParticipantApproved).Single();
        Assert.Equal(evening.Guest.ParticipantId, approval.GetProperty("participantId").GetGuid());
    }

    [SkippableFact]
    public async Task A_guest_without_the_total_finds_it_on_no_other_read_and_their_own_items_are_what_they_are_charged()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var evening = await EveningAsync(factory);

        using var hostClient = factory.CreateClientWithToken(evening.Host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(evening.Guest.AccessToken);

        // ---- Their own order's answer carries no totals; the host's and the waiter's do.
        Assert.False(evening.GuestsOrder.GetProperty("tableTotalVisible").GetBoolean());
        Assert.False(evening.GuestsOrder.TryGetProperty("totals", out _));

        Assert.True(evening.HostsOrder.GetProperty("tableTotalVisible").GetBoolean());
        Assert.True(evening.HostsOrder.GetProperty("totals").GetProperty("subtotalAmd").GetInt64() > 0L);
        Assert.True(evening.WaitersOrder.GetProperty("tableTotalVisible").GetBoolean());
        Assert.True(evening.WaitersOrder.TryGetProperty("totals", out _));

        // ---- The tab: their three lines, and of the four comps only the one off their own dish.
        var guestView = await ReadAsync(guestClient, $"/api/tabs/{evening.TabId}");
        var myLines = guestView.GetProperty("myLines").EnumerateArray().ToList();

        Assert.Equal(
            new[] { evening.WineLineId, evening.GuestsLineId, evening.ForTheGuestLineId }.Order(),
            myLines.Select(l => l.GetProperty("lineId").GetGuid()).Order());

        var forThem = myLines.Single(l => l.GetProperty("lineId").GetGuid() == evening.ForTheGuestLineId);
        Assert.Equal(JsonValueKind.Null, ValueKindOf(forThem, "placedByParticipantId"));
        Assert.Equal(evening.Guest.ParticipantId, forThem.GetProperty("onBehalfOfParticipantId").GetGuid());

        // The table's khachapuri is not itemised for them; the host, who sees the total, has it on
        // their own lines - they were at the table for it too - flagged and split two ways.
        Assert.DoesNotContain(myLines, l => l.GetProperty("isTableAttributed").GetBoolean());

        // Their own unsplit lines, whole: one khachapuri of their own and two the waiter keyed in.
        var myItemsAmd = guestView.GetProperty("myItemsSubtotalAmd").GetInt64();
        Assert.Equal(3 * TestMenu.KhachapuriAmd, myItemsAmd);

        var adjustment = Assert.Single(guestView.GetProperty("adjustments").EnumerateArray().ToList());
        Assert.Equal(evening.GuestsLineId, adjustment.GetProperty("lineId").GetGuid());
        Assert.Equal(GuestsCompAmd, adjustment.GetProperty("reductionAmd").GetInt64());

        var hostView = await ReadAsync(hostClient, $"/api/tabs/{evening.TabId}");
        Assert.Equal(4, hostView.GetProperty("adjustments").GetArrayLength());
        Assert.Equal(6, hostView.GetProperty("tableLines").GetArrayLength());

        var hostsTableLine = hostView.GetProperty("myLines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetGuid() == evening.TableKhachapuriLineId);
        Assert.True(hostsTableLine.GetProperty("isTableAttributed").GetBoolean());
        Assert.Equal(2, hostsTableLine.GetProperty("sharedWithCount").GetInt32());

        // ---- The split agrees: what their bill calls their own items is what they are charged for
        // them, less the comp they can see on them. And the table's khachapuri is charged all the
        // same: their half of the shared items is half the bottle and half that khachapuri after its
        // comp - money, without the line it came from.
        var myShare = (await ReadAsync(guestClient, $"/api/tabs/{evening.TabId}/shares")).GetProperty("myShare");
        Assert.Equal(myItemsAmd - GuestsCompAmd, myShare.GetProperty("ownItemsAmd").GetInt64());
        Assert.Equal(
            (TestMenu.WineAmd / 2) + ((TestMenu.KhachapuriAmd - TableCompAmd) / 2),
            myShare.GetProperty("sharedItemsAmd").GetInt64());
    }

    /// <summary>
    /// The host's read is the stored stream, member for member, and every member any writer put on
    /// it has a decision in <see cref="TabEventRedactor"/> - so the next member a writer adds is
    /// either classified or caught here, rather than silently dropped for one reader and shown to the
    /// other.
    /// </summary>
    private async Task AssertHostReadsTheStoredStreamAsync(Guid tabId, IReadOnlyList<Event> hostStream)
    {
        await using var db = fixture.CreateContext(new TestClock(DateTime.UtcNow));

        var stored = await db.TabEvents.AsNoTracking()
            .Where(e => e.TabId == tabId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.PayloadJson)
            .ToListAsync();

        Assert.Equal(stored.Count, hostStream.Count);

        for (var i = 0; i < stored.Count; i++)
        {
            using var written = JsonDocument.Parse(stored[i]);

            Assert.True(
                System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(stored[i]),
                    System.Text.Json.Nodes.JsonNode.Parse(hostStream[i].Payload.GetRawText())),
                $"The host's {hostStream[i].Type} differs from what was stored.");

            var classified = TabEventRedactor.ClassifiedMembers(hostStream[i].Type);

            Assert.All(
                written.RootElement.EnumerateObject(),
                member => Assert.True(
                    classified.Contains(member.Name),
                    $"{hostStream[i].Type}.{member.Name} is written but TabEventRedactor has no rule for it."));
        }
    }

    // ------------------------------------------------------------ the evening

    private sealed record Opened(Guid TabId, Guid ParticipantId, string AccessToken, TabOpenOutcome Outcome);

    private sealed record Evening(
        Guid TabId,
        Opened Host,
        Opened Guest,
        Opened Stranger,
        JsonElement HostsOrder,
        JsonElement GuestsOrder,
        JsonElement WaitersOrder,
        Guid TableCoffeeLineId,
        Guid WineLineId,
        Guid GuestsLineId,
        Guid ForTheGuestLineId,
        Guid TableKhachapuriLineId,
        long TotalAmd,
        long LeftForTheHostAmd);

    private sealed record Event(long Sequence, TabEventType Type, JsonElement Payload);

    /// <summary>The evening in the type's remarks, through the API, as each person would do it.</summary>
    private async Task<Evening> EveningAsync(YallaApiFactory factory)
    {
        var (branch, menu, qrToken) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrToken, "phone-host", hideTotalFromGuests: true);
        var tabId = host.TabId;

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        // A coffee for the table while the host is alone on it: split with the host and nobody else.
        var tableCoffee = await PlacedAsync(waiter.PostAsJsonAsync($"/api/tabs/{tabId}/staff-orders", new
        {
            items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        var guest = await OpenAsync(anonymous, qrToken, "phone-guest");
        Assert.Equal(HttpStatusCode.OK, (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{tabId}/participants/{guest.ParticipantId}/approve", new { })).StatusCode);

        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        var hostsOrder = await PlacedAsync(hostClient.PostAsJsonAsync($"/api/tabs/{tabId}/orders", new
        {
            items = new object[]
            {
                new { menuItemId = menu.Coffee, quantity = 2 },
                new { menuItemId = menu.Wine, quantity = 1, isShared = true },
            },
            clientCommandId = Guid.CreateVersion7(),
        }));

        var guestsOrder = await PlacedAsync(guestClient.PostAsJsonAsync($"/api/tabs/{tabId}/orders", new
        {
            items = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        // Two more for the guest, keyed in by the waiter in their name.
        var forTheGuest = await PlacedAsync(waiter.PostAsJsonAsync($"/api/tabs/{tabId}/staff-orders", new
        {
            items = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
            onBehalfOfParticipantId = guest.ParticipantId,
        }));

        // And one for the table, now that both of them are at it.
        var tableKhachapuri = await PlacedAsync(waiter.PostAsJsonAsync($"/api/tabs/{tabId}/staff-orders", new
        {
            items = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        var tableCoffeeLineId = FirstLine(tableCoffee);
        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/lines/{tableCoffeeLineId}/void",
            new { reason = "keyed twice", clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var hostsCoffeeLineId = hostsOrder.GetProperty("lines").EnumerateArray()
            .Single(l => !l.GetProperty("isShared").GetBoolean()).GetProperty("lineId").GetGuid();
        var wineLineId = hostsOrder.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("isShared").GetBoolean()).GetProperty("lineId").GetGuid();
        var guestsLineId = FirstLine(guestsOrder);

        // A comp off the whole tab, one off the guest's own khachapuri, one off the host's coffees,
        // and one off the khachapuri the waiter keyed in for the table.
        foreach (var (lineId, amountAmd) in new (Guid?, long)[]
                 {
                     (null, TabCompAmd), (guestsLineId, GuestsCompAmd), (hostsCoffeeLineId, HostsCompAmd),
                     (FirstLine(tableKhachapuri), TableCompAmd),
                 })
        {
            Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync($"/api/tabs/{tabId}/adjustments", new
            {
                tabOrderLineId = lineId,
                kind = (int)AdjustmentKind.Comp,
                amountAmd,
                reason = "sorry about the wait",
                clientCommandId = Guid.CreateVersion7(),
            })).StatusCode);
        }

        // Somebody at the next table scans the wrong code and waits for a host who will not come.
        var stranger = await OpenAsync(anonymous, qrToken, "phone-stranger");
        Assert.Equal(TabOpenOutcome.JoinedExistingTab, stranger.Outcome);

        Assert.Equal(HttpStatusCode.Created, (await guestClient.PostAsJsonAsync(
            $"/api/tabs/{tabId}/service-requests", new { preset = 2, note = "sparkling, please" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{tabId}/service-requests", new { preset = 3, billScope = 1, tipPercent = 10 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/closing", new { })).StatusCode);

        await PaidAsync(waiter, tabId, GuestPaysAmd, guest.ParticipantId, GuestTipAmd);

        var beforeTheRest = (await ReadAsync(hostClient, $"/api/tabs/{tabId}")).GetProperty("tableTotal");
        var totalAmd = beforeTheRest.GetProperty("totalAmd").GetInt64();
        var leftAmd = beforeTheRest.GetProperty("remainingAmd").GetInt64();
        await PaidAsync(waiter, tabId, leftAmd, host.ParticipantId, tipAmd: 0L);

        return new Evening(
            tabId,
            host,
            guest,
            stranger,
            hostsOrder,
            guestsOrder,
            forTheGuest,
            tableCoffeeLineId,
            wineLineId,
            guestsLineId,
            FirstLine(forTheGuest),
            FirstLine(tableKhachapuri),
            totalAmd,
            leftAmd);
    }

    // ------------------------------------------------------------ helpers

    private async Task<(AuthBranch Branch, TestMenu Menu, string QrToken)> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var qrToken = await db.DiningTables.AsNoTracking()
            .Where(t => t.BranchId == branch.BranchId)
            .OrderBy(t => t.Label)
            .Select(t => t.QrToken)
            .FirstAsync();

        return (branch, menu, qrToken);
    }

    private static async Task<Opened> OpenAsync(
        HttpClient client,
        string qrToken,
        string deviceId,
        bool? hideTotalFromGuests = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7(), displayName = deviceId, hideTotalFromGuests });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tab = body.GetProperty("tab");

        return new Opened(
            tab.GetProperty("tabId").GetGuid(),
            tab.GetProperty("me").GetProperty("participantId").GetGuid(),
            body.GetProperty("token").GetProperty("accessToken").GetString()!,
            EnumOf<TabOpenOutcome>(body.GetProperty("outcome")));
    }

    /// <summary>An enum however it was serialised - as its number or its name.</summary>
    private static T EnumOf<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);

    private static async Task<JsonElement> PlacedAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid FirstLine(JsonElement order) => order.GetProperty("lines")[0].GetProperty("lineId").GetGuid();

    private static async Task PaidAsync(HttpClient waiter, Guid tabId, long amountAmd, Guid participantId, long tipAmd)
    {
        var response = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/payments/cash",
            new { amountAmd, tabParticipantId = participantId, tipAmd, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<IReadOnlyList<Event>> EventsAsync(HttpClient client, Guid tabId)
    {
        var page = await ReadAsync(client, $"/api/tabs/{tabId}/events?afterSequence=0");

        Assert.False(page.GetProperty("hasMore").GetBoolean());

        return
        [
            .. page.GetProperty("events").EnumerateArray().Select(e => new Event(
                e.GetProperty("sequence").GetInt64(),
                EnumOf<TabEventType>(e.GetProperty("type")),
                e.GetProperty("payload").Clone())),
        ];
    }

    private static List<JsonElement> Payloads(IEnumerable<Event> stream, TabEventType type) =>
        [.. stream.Where(e => e.Type == type).Select(e => e.Payload)];

    private static string[] Members(JsonElement payload) =>
        [.. payload.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal)];

    private static JsonValueKind ValueKindOf(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) ? value.ValueKind : JsonValueKind.Null;

    private static List<Guid> LineIds(JsonElement lines) =>
        [.. lines.EnumerateArray().Select(l => l.GetProperty("lineId").GetGuid()).Order()];

    /// <summary>Every line id on every <c>OrderPlaced</c> in the stream.</summary>
    private static IEnumerable<Guid> LinesOn(IEnumerable<Event> stream) =>
        Payloads(stream, TabEventType.OrderPlaced)
            .SelectMany(p => p.GetProperty("lines").EnumerateArray())
            .Select(l => l.GetProperty("lineId").GetGuid());

    /// <summary>Whether a member of that name appears anywhere in any payload, at any depth.</summary>
    private static bool Contains(IEnumerable<Event> stream, string member) =>
        stream.Any(e => Walk(e.Payload).Any(found => found.Name == member));

    /// <summary>Every number anywhere in any payload.</summary>
    private static List<long> Numbers(IEnumerable<Event> stream) =>
        [
            .. stream.SelectMany(e => Walk(e.Payload))
                .Where(found => found.Value.ValueKind == JsonValueKind.Number && found.Value.TryGetInt64(out _))
                .Select(found => found.Value.GetInt64()),
        ];

    private static IEnumerable<(string Name, JsonElement Value)> Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    yield return (member.Name, member.Value);

                    foreach (var inner in Walk(member.Value))
                    {
                        yield return inner;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in Walk(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}
