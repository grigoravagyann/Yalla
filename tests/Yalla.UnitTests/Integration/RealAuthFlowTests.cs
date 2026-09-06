using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The critical flows, with real tokens, through the whole pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because a client script found in an afternoon what 522 green tests had never
/// once run.</b> Ordering resolved the participant from <c>ICurrentActor.DinerUserId</c>, which is
/// null for every real participant token; the test double put the participant id there, so every
/// ordering test passed against a shape production cannot produce and the feature this product is
/// built around had never worked outside the suite.
/// </para>
/// <para>
/// The factory forces <c>DevActor:Enabled=false</c>, so every request below carries a token minted
/// by the real sign-in flow and read by the real <c>ClaimsCurrentActor</c>. No doubles anywhere in
/// the path. That is the whole point: a test that swaps in a stand-in for the thing that was broken
/// cannot notice that it is broken.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class RealAuthFlowTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 1. a diner orders

    /// <summary>
    /// <b>Test 1.</b> A real participant token places an order.
    /// </summary>
    /// <remarks>
    /// The single most important assertion in the suite, and the one that was missing. Everything
    /// here goes over HTTP: the scan mints the token, the token is presented as a bearer, and the
    /// order lands.
    /// </remarks>
    [SkippableFact]
    public async Task A_real_participant_token_places_an_order()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host");

        using var diner = factory.CreateClientWithToken(host.AccessToken);

        var placed = await diner.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = world.Menu.Coffee, quantity = 2 } },
                clientCommandId = Guid.CreateVersion7(),
            });

        // A 403 here is the bug: the participant token was admitted by the policy and then the
        // service could not work out who was holding it.
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var order = await placed.Content.ReadFromJsonAsync<JsonElement>();

        // Attributed to the participant, from their own token and never from the body.
        Assert.Equal(host.ParticipantId, order.GetProperty("placedByParticipantId").GetGuid());
        Assert.Equal(2, order.GetProperty("lines").EnumerateArray().Single().GetProperty("quantity").GetInt32());
        Assert.Equal(2 * TestMenu.CoffeeAmd, order.GetProperty("totals").GetProperty("subtotalAmd").GetInt64());

        // And the tab's own event stream names them, rather than recording an anonymous diner -
        // which is what reading DinerUserId alone would have written for somebody with no account.
        await using var db = fixture.CreateContext(factory.Clock);

        var placedEvent = await db.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == host.TabId && e.Type == TabEventType.OrderPlaced)
            .FirstAsync();

        Assert.Equal(ActorType.Diner, placedEvent.ActorType);
        Assert.Equal(host.ParticipantId, placedEvent.ActorId);
    }

    // ------------------------------------------------------------ 2. every participant path

    /// <summary>
    /// <b>Test 2.</b> Every endpoint a tab participant can legitimately reach works with a real
    /// participant token, which carries no <c>DinerUserId</c>.
    /// </summary>
    /// <remarks>
    /// The sweep, as a test rather than as a grep. Ordering was the one that was broken; these are
    /// the rest of the surface a participant token addresses, and any of them that came to require
    /// an account would fail here the same way.
    /// </remarks>
    [SkippableFact]
    public async Task No_participant_reachable_endpoint_requires_a_diner_account()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host");
        var guest = await OpenAsync(anonymous, world.QrTokens[0], "phone-guest");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { }))
            .EnsureSuccessStatusCode();

        // Read the tab.
        Assert.Equal(HttpStatusCode.OK, (await hostClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);

        // Order.
        Assert.Equal(
            HttpStatusCode.Created,
            (await hostClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/orders",
                new
                {
                    items = new[] { new { menuItemId = world.Menu.Khachapuri, quantity = 1 } },
                    clientCommandId = Guid.CreateVersion7(),
                })).StatusCode);

        // Call a waiter.
        Assert.Equal(
            HttpStatusCode.Created,
            (await hostClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/service-requests",
                new { preset = ServiceRequestPreset.Water })).StatusCode);

        // Read the split.
        Assert.Equal(HttpStatusCode.OK, (await hostClient.GetAsync($"/api/tabs/{host.TabId}/shares")).StatusCode);

        // Read the event stream.
        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.GetAsync($"/api/tabs/{host.TabId}/events?afterSequence=0")).StatusCode);

        // Put a name on the tab, invite somebody, and change the split.
        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/display-name", new { displayName = "Aram" })).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/join-tokens", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/settlement-mode",
                new { settlementMode = SettlementMode.AnyonePaysAnyAmount })).StatusCode);

        // A guest the host has approved may order too, on their own token.
        Assert.Equal(
            HttpStatusCode.Created,
            (await guestClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/orders",
                new
                {
                    items = new[] { new { menuItemId = world.Menu.Coffee, quantity = 1 } },
                    clientCommandId = Guid.CreateVersion7(),
                })).StatusCode);
    }

    // ------------------------------------------------------------ 5. one flow per surface

    /// <summary>
    /// <b>Test 5, the staff surface.</b> A waiter signs in for real, seats a walk-in and takes cash.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_signs_in_for_real_and_takes_cash()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host");

        using var diner = factory.CreateClientWithToken(host.AccessToken);

        (await diner.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = world.Menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            })).EnsureSuccessStatusCode();

        // A real PIN on a real enrolled tablet, not a stand-in actor.
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, world.Branch));

        var owed = TestMenu.CoffeeAmd + (TestMenu.CoffeeAmd / 10L);

        var paid = await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/payments/cash",
            new { amountAmd = owed, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);

        var body = await paid.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0L, body.GetProperty("totals").GetProperty("remainingAmd").GetInt64());
        Assert.True(body.GetProperty("tabClosed").GetBoolean());
    }

    /// <summary>
    /// <b>Test 5, the admin surface.</b> An owner signs in for real and edits the menu.
    /// </summary>
    [SkippableFact]
    public async Task An_owner_signs_in_for_real_and_edits_a_menu()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, world.Branch));

        var created = await manager.PostAsJsonAsync(
            $"/api/branches/{world.Branch.BranchId}/menu/categories",
            new { name = "Pastries", displayOrder = 3 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var categoryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var item = await manager.PostAsJsonAsync(
            $"/api/branches/{world.Branch.BranchId}/menu/categories/{categoryId}/items",
            new { name = "Gata", priceAmd = 700L });

        Assert.Equal(HttpStatusCode.Created, item.StatusCode);

        var itemId = (await item.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var repriced = await manager.PatchAsJsonAsync(
            $"/api/branches/{world.Branch.BranchId}/menu/items/{itemId}", new { priceAmd = 800L });

        Assert.Equal(HttpStatusCode.OK, repriced.StatusCode);
        Assert.Equal(800L, (await repriced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priceAmd").GetInt64());
    }

    /// <summary>
    /// <b>Test 5, the diner surface.</b> Somebody verifies a phone number for real, books a table
    /// and cancels it.
    /// </summary>
    /// <remarks>
    /// The one identity type that genuinely does need an account. A no-show has to be counted
    /// against somebody, which is why booking requires a verified diner and ordering does not -
    /// and running the flow with a real diner token is what shows the two are different rather
    /// than accidentally the same.
    /// </remarks>
    [SkippableFact]
    public async Task A_diner_verifies_a_phone_books_a_table_and_cancels_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();

        // A real code, verified for real. The account is created by verifying.
        var phone = $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone, localeCode = "hy" });

        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var token = (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;

        using var diner = factory.CreateClientWithToken(token);

        // Tomorrow lunchtime in the branch's own wall clock, which is what the booking endpoint
        // takes - a UTC instant here would book the wrong hour.
        var localDate = DateOnly.FromDateTime(factory.Clock.UtcNow.AddDays(1));

        var booked = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = world.Branch.BranchId,
                tableId = world.Branch.TableIds[0],
                date = localDate,
                time = new TimeOnly(13, 0),
                partySize = 2,
                guestName = "Ani Test",
                guestPhone = phone,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        var reservation = await booked.Content.ReadFromJsonAsync<JsonElement>();
        var reservationId = reservation.GetProperty("id").GetGuid();

        // Their own bookings come back on their own token.
        var mine = await ReadAsync(diner, "/api/reservations/mine");

        Assert.Contains(
            mine.GetProperty("upcoming").EnumerateArray(),
            r => r.GetProperty("id").GetGuid() == reservationId);

        // And cancelling is the thing the whole no-show story rests on being easy.
        var cancelled = await diner.PostAsJsonAsync(
            $"/api/reservations/{reservationId}/cancel", new { reason = "plans changed" });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        Assert.Equal(
            ReservationStatus.CancelledByDiner,
            EnumOf<ReservationStatus>(
                (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status")));
    }

    // ------------------------------------------------------------ 6 to 12. the diner's bill

    /// <summary>
    /// <b>Tests 6, 7, 9, 10 and 12.</b> Everything Prompt 8 specified for the diner's tab response,
    /// asserted on the body a diner actually receives.
    /// </summary>
    /// <remarks>
    /// Written from the specification rather than from the projection: each assertion names a thing
    /// a diner needs to be able to see. Every one of these was specified in Prompt 8, and every one
    /// was absent - the tests that "covered" them asserted that the projection returned whatever the
    /// projection returned, so a missing field could never fail one.
    /// </remarks>
    [SkippableFact]
    public async Task A_diners_tab_carries_voided_lines_adjustments_the_zone_and_the_stream_position()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host");

        using var diner = factory.CreateClientWithToken(host.AccessToken);
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, world.Branch));
        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, world.Branch));

        // Two orders: one that survives, one the waiter takes off.
        var keeper = await PlaceAsync(diner, host.TabId, world.Menu.Khachapuri, quantity: 1, note: "no onions");
        var doomed = await PlaceAsync(diner, host.TabId, world.Menu.Coffee, quantity: 1);

        var doomedLineId = doomed.GetProperty("lines").EnumerateArray().Single().GetProperty("lineId").GetGuid();

        (await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/lines/{doomedLineId}/void",
            new { reason = "ordered by mistake", clientCommandId = Guid.CreateVersion7() }))
            .EnsureSuccessStatusCode();

        (await manager.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/adjustments",
            new
            {
                kind = AdjustmentKind.Discount,
                percent = 10m,
                reason = "regular customer",
                clientCommandId = Guid.CreateVersion7(),
            }))
            .EnsureSuccessStatusCode();

        var view = await ReadAsync(diner, $"/api/tabs/{host.TabId}");

        // ---- Test 6: the voided line is there, with its reason, worth nothing.
        var lines = view.GetProperty("myLines").EnumerateArray().ToList();

        Assert.Equal(2, lines.Count);

        var voided = lines.Single(l => l.GetProperty("lineId").GetGuid() == doomedLineId);

        Assert.True(voided.GetProperty("isVoided").GetBoolean());
        Assert.Equal("ordered by mistake", voided.GetProperty("voidReason").GetString());
        Assert.True(voided.TryGetProperty("voidedAtUtc", out _));
        Assert.Equal(0L, voided.GetProperty("lineTotalAmd").GetInt64());

        // Excluded from the total, which is the half that was already right.
        Assert.Equal(TestMenu.KhachapuriAmd, view.GetProperty("myItemsSubtotalAmd").GetInt64());

        // ---- Test 7: the discount, with the reason the manager typed.
        var adjustment = view.GetProperty("adjustments").EnumerateArray().Single();

        Assert.Equal("regular customer", adjustment.GetProperty("reason").GetString());
        Assert.True(adjustment.GetProperty("reductionAmd").GetInt64() > 0L);
        Assert.False(adjustment.GetProperty("isVoided").GetBoolean());

        // ---- Test 10: the branch's zone, so the client never formats in the device's.
        Assert.Equal(world.TimeZoneId, view.GetProperty("timeZoneId").GetString());

        // ---- Test 9: where the stream stands, without a second call to /events.
        var events = await ReadAsync(diner, $"/api/tabs/{host.TabId}/events?afterSequence=0");

        Assert.Equal(
            events.GetProperty("maxSequence").GetInt64(),
            view.GetProperty("maxSequence").GetInt64());

        // ---- The eight fields the client needed off a line.
        var kept = lines.Single(l => !l.GetProperty("isVoided").GetBoolean());

        Assert.Equal(keeper.GetProperty("orderId").GetGuid(), kept.GetProperty("orderId").GetGuid());
        Assert.Equal(world.Menu.Khachapuri, kept.GetProperty("menuItemId").GetGuid());
        Assert.Equal("no onions", kept.GetProperty("note").GetString());
        Assert.Equal(0, kept.GetProperty("sharedWithCount").GetInt32());

        // ---- Test 12: the line's order status follows the kitchen rail.
        Assert.Equal(TabOrderStatus.New, EnumOf<TabOrderStatus>(kept.GetProperty("orderStatus")));

        (await waiter.PostAsJsonAsync(
            $"/api/orders/{keeper.GetProperty("orderId").GetGuid()}/status",
            new { status = TabOrderStatus.InKitchen }))
            .EnsureSuccessStatusCode();

        var moved = await ReadAsync(diner, $"/api/tabs/{host.TabId}");

        var afterMove = moved.GetProperty("myLines").EnumerateArray()
            .Single(l => !l.GetProperty("isVoided").GetBoolean());

        Assert.Equal(TabOrderStatus.InKitchen, EnumOf<TabOrderStatus>(afterMove.GetProperty("orderStatus")));
    }

    /// <summary>
    /// <b>Test 8.</b> A guest whose host hid the total still gets the service charge percentage,
    /// and still gets no aggregate.
    /// </summary>
    /// <remarks>
    /// The percentage lived on <c>ReservationPolicyView</c> behind <c>ManagerOrAbove</c>, where no
    /// diner could ever read it - while Prompt 8 requires the bill to state it from the first item.
    /// It is a fact about the venue, not an aggregate, so hiding the total does not hide it.
    /// </remarks>
    [SkippableFact]
    public async Task A_guest_with_the_total_hidden_still_sees_the_service_charge_percentage()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host", hideTotalFromGuests: true);
        var guest = await OpenAsync(anonymous, world.QrTokens[0], "phone-guest");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { }))
            .EnsureSuccessStatusCode();

        await PlaceAsync(guestClient, host.TabId, world.Menu.Coffee, quantity: 1);

        var view = await ReadAsync(guestClient, $"/api/tabs/{host.TabId}");

        // Present, and a real number rather than a zero.
        Assert.Equal(10m, view.GetProperty("serviceChargePercent").GetDecimal());

        // And the aggregate is still absent - the union the client narrows on is unchanged.
        Assert.False(view.GetProperty("tableTotalVisible").GetBoolean());
        Assert.False(view.TryGetProperty("tableTotal", out _));
        Assert.False(view.TryGetProperty("tableLines", out _));

        // Their own items are always there, which is what settles "I didn't order that".
        Assert.Equal(TestMenu.CoffeeAmd, view.GetProperty("myItemsSubtotalAmd").GetInt64());
    }

    /// <summary>
    /// <b>Test 11.</b> The split badge counts who was at the table when the line was ordered.
    /// </summary>
    /// <remarks>
    /// Asserted with somebody who joined <i>after</i> the shared bottle, which is the only way to
    /// tell the snapshot from the current roster. Computed from the roster, the bottle poured for
    /// two would start reading as split three ways the moment a third person scanned the code - and
    /// the diner who paid for half of it would watch their share change on screen.
    /// </remarks>
    [SkippableFact]
    public async Task A_shared_lines_split_count_ignores_somebody_who_joined_afterwards()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, world.QrTokens[0], "phone-host");
        var early = await OpenAsync(anonymous, world.QrTokens[0], "phone-early");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{early.ParticipantId}/approve", new { }))
            .EnsureSuccessStatusCode();

        // Two people at the table, and a bottle between them.
        var round = await PlaceAsync(hostClient, host.TabId, world.Menu.Wine, quantity: 1, isShared: true);
        var sharedLineId = round.GetProperty("lines").EnumerateArray().Single().GetProperty("lineId").GetGuid();

        // The friend who is late.
        var late = await OpenAsync(anonymous, world.QrTokens[0], "phone-late");

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{late.ParticipantId}/approve", new { }))
            .EnsureSuccessStatusCode();

        var view = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");

        // Three on the tab now.
        Assert.Equal(3, view.GetProperty("participants").GetArrayLength());

        var line = view.GetProperty("myLines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetGuid() == sharedLineId);

        Assert.True(line.GetProperty("isShared").GetBoolean());

        // Two when it was poured, and two for ever.
        Assert.Equal(2, line.GetProperty("sharedWithCount").GetInt32());

        // And the latecomer does not have it among their own items at all.
        using var lateClient = factory.CreateClientWithToken(late.AccessToken);
        var lateView = await ReadAsync(lateClient, $"/api/tabs/{host.TabId}");

        Assert.DoesNotContain(
            lateView.GetProperty("myLines").EnumerateArray(),
            l => l.GetProperty("lineId").GetGuid() == sharedLineId);
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        AuthBranch Branch,
        IReadOnlyList<string> QrTokens,
        TestMenu Menu,
        string TimeZoneId);

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var qrTokens = await db.DiningTables.AsNoTracking()
            .Where(t => t.BranchId == branch.BranchId)
            .OrderBy(t => t.Label)
            .Select(t => t.QrToken)
            .ToListAsync();

        var timeZoneId = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.TimeZoneId)
            .FirstAsync();

        return new World(branch, qrTokens, menu, timeZoneId);
    }

    private sealed record Opened(Guid TabId, Guid ParticipantId, string AccessToken);

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
            body.GetProperty("token").GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> PlaceAsync(
        HttpClient client,
        Guid tabId,
        Guid menuItemId,
        int quantity,
        string? note = null,
        bool isShared = false)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/tabs/{tabId}/orders",
            new
            {
                items = new[] { new { menuItemId, quantity, note, isShared } },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An enum however it was serialised - as its number or its name.</summary>
    private static T EnumOf<T>(JsonElement element)
        where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
