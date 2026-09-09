using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The money endpoints through the real pipeline: cash, write-offs, and reversing a comp.
/// </summary>
/// <remarks>
/// <para>
/// <c>PaymentAndServiceTests</c> proves the arithmetic and the races by calling the services. It
/// hands each one an actor and a command it built itself, so three things it cannot see sit
/// between a waiter's tablet and that call.
/// </para>
/// <para>
/// <b>The branch boundary.</b> <c>BranchScoped</c> is an authorization handler; there is no way to
/// reach it except through a request. It is also the only policy in the product that resolves its
/// branch from a <c>tabId</c> rather than reading one out of the route, which is a second thing
/// that only runs in the pipeline. <b>The money on the wire.</b> <c>RecordCashRequest.ToCommand</c>
/// is where <c>amountAmd</c> and <c>tipAmd</c> become a command, and the two are adjacent
/// <c>long</c>s - transposed, every service test still passes and every tip is taken as payment.
/// <b>The refusal a waiter has to read.</b> The 409 for overpayment is documented as carrying the
/// balance, and the balance is put there by the exception mapper, above every service.
/// </para>
/// <para>
/// Where a route policy and a service check enforce the same thing, these say so rather than
/// claiming to pin the route. The exception is <c>voidTabAdjustment</c>, where nothing is
/// redundant: that route is addressed by adjustment id, so <c>BranchScoped</c> could not be
/// applied to it, and one call in <c>VoidAdjustmentAsync</c> is the whole of what stops a manager
/// reversing another venue's comp.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class MoneyEndpointTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A waiter at another branch cannot take money against this tab, and the attempt moves nothing.
    /// </summary>
    /// <remarks>
    /// The route is <c>/api/tabs/{tabId}/payments/cash</c> and names no branch. <c>BranchScoped</c>
    /// resolves the tab's branch and compares the token's claim against that - the only place in
    /// the product where it does - and the service checks the same boundary again underneath. Both
    /// would have to go for money to cross a venue; this asserts that it does not.
    /// </remarks>
    [SkippableFact]
    public async Task Cash_from_a_waiter_at_another_branch_is_refused_and_moves_nothing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu, tables) = await ArrangeAsync(factory);

        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        var tab = await OpenTabAsync(factory, tables[0].QrToken);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var owed = await OrderAsync(waiter, tab.TabId, menu.Khachapuri, quantity: 2);

        Assert.True(owed > 0, "Nothing was ordered, so there is no balance to protect.");

        // A waiter at the other branch, with a perfectly valid staff token.
        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/payments/cash",
                new { amountAmd = owed, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // And the diner sitting at the table cannot take their own cash payment either.
        using var participant = factory.CreateClientWithToken(tab.AccessToken);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await participant.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/payments/cash",
                new { amountAmd = owed, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // Nothing was taken. This is the half that matters: a 403 that had already moved money
        // would be a worse outcome than a 200.
        await using var verify = fixture.CreateContext(factory.Clock);

        Assert.False(
            await verify.Payments.AsNoTracking().AnyAsync(p => p.TabId == tab.TabId),
            "A refused request wrote a payment row.");
    }

    /// <summary>
    /// The amounts a client sends are the amounts that land, and the tip stays outside the bill.
    /// </summary>
    /// <remarks>
    /// <c>amountAmd</c> and <c>tipAmd</c> are adjacent <c>long</c>s on the wire and adjacent
    /// positional arguments in <c>ToCommand</c>. Transposed, the bill is settled with the tip and
    /// the tip is recorded as the payment - and every assertion in the service tests still holds,
    /// because they never build the request.
    /// </remarks>
    [SkippableFact]
    public async Task A_cash_payment_binds_its_amounts_and_keeps_the_tip_off_the_bill()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu, tables) = await ArrangeAsync(factory);

        var tab = await OpenTabAsync(factory, tables[0].QrToken);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var total = await OrderAsync(waiter, tab.TabId, menu.Wine, quantity: 1);

        // Deliberately different numbers, and neither of them the total: a transposition has to
        // change what the assertions see.
        const long Paid = 4_000L;
        const long Tip = 700L;

        Assert.True(Paid < total, "The part payment is not smaller than the bill, so this proves nothing.");

        var response = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/payments/cash",
            new { amountAmd = Paid, tipAmd = Tip, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(Paid, body.GetProperty("amountAmd").GetInt64());
        Assert.Equal(Tip, body.GetProperty("tipAmd").GetInt64());
        Assert.False(body.GetProperty("tabClosed").GetBoolean());

        var totals = body.GetProperty("totals");

        // The tip is outside both of these. A venue that folded it in would be taking a tip off
        // what the diner still owes.
        Assert.Equal(Paid, totals.GetProperty("paidAmd").GetInt64());
        Assert.Equal(total - Paid, totals.GetProperty("remainingAmd").GetInt64());
        Assert.Equal(total, totals.GetProperty("totalAmd").GetInt64());

        // The rest settles the tab, and the table is deliberately left occupied: a party that has
        // paid usually sits on.
        var settled = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/payments/cash",
            new { amountAmd = total - Paid, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, settled.StatusCode);

        var closing = await settled.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(closing.GetProperty("tabClosed").GetBoolean());
        Assert.Equal(0L, closing.GetProperty("totals").GetProperty("remainingAmd").GetInt64());

        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");

        var table = floor.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("tableId").GetGuid() == tables[0].TableId);

        Assert.Equal((int)TableStatus.Occupied, table.GetProperty("physicalStatus").GetInt32());
    }

    /// <summary>
    /// Offering more than is owed is refused with the balance in the body.
    /// </summary>
    /// <remarks>
    /// The endpoint's own description promises this - "show <c>context.remainingAmd</c>, the waiter
    /// is at the table and needs the number" - and it is the exception mapper that puts it there,
    /// which is above every service. A service test can only see that an exception was thrown.
    /// </remarks>
    [SkippableFact]
    public async Task Overpaying_answers_409_carrying_the_balance_the_waiter_is_standing_there_needing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu, tables) = await ArrangeAsync(factory);

        var tab = await OpenTabAsync(factory, tables[0].QrToken);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var owed = await OrderAsync(waiter, tab.TabId, menu.Coffee, quantity: 3);

        var tooMuch = owed + 5_000L;

        var refused = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/payments/cash",
            new { amountAmd = tooMuch, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("payment-exceeds-remaining", problem.GetProperty("code").GetString());

        var context = problem.GetProperty("context");

        Assert.Equal(tab.TabId, context.GetProperty("tabId").GetGuid());
        Assert.Equal(tooMuch, context.GetProperty("requestedAmd").GetInt64());

        // The number the waiter reads off the screen and asks the table for.
        Assert.Equal(owed, context.GetProperty("remainingAmd").GetInt64());

        // Refused means refused: the exact balance still goes through afterwards.
        Assert.Equal(
            HttpStatusCode.Created,
            (await waiter.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/payments/cash",
                new { amountAmd = owed, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() })).StatusCode);
    }

    /// <summary>
    /// Writing off a balance takes a manager, and writing the same one off twice is refused.
    /// </summary>
    /// <remarks>
    /// There is no idempotency key on this route - <c>AbandonTabRequest</c> carries only a reason -
    /// so what stops a retried write-off being counted twice is the tab's own state. That is worth
    /// an assertion precisely because nothing above it is guarding.
    /// </remarks>
    [SkippableFact]
    public async Task A_write_off_takes_a_manager_and_a_second_one_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu, tables) = await ArrangeAsync(factory);

        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        var tab = await OpenTabAsync(factory, tables[0].QrToken);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var owed = await OrderAsync(waiter, tab.TabId, menu.Khachapuri, quantity: 1);

        // The waiter who served them cannot decide the venue eats the loss.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/abandon", new { reason = "they walked out" })).StatusCode);

        // Nor a manager of somewhere else.
        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/abandon", new { reason = "not my venue" })).StatusCode);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var written = await manager.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/abandon", new { reason = "they left without paying" });

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var totals = await written.Content.ReadFromJsonAsync<JsonElement>();

        // Kept, not deleted: a venue's write-offs are a number it needs.
        Assert.Equal(owed, totals.GetProperty("remainingAmd").GetInt64());
        Assert.Equal(0L, totals.GetProperty("paidAmd").GetInt64());

        // The same tablet sends it again. Once written off, not twice.
        var again = await manager.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/abandon", new { reason = "they left without paying" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);

        Assert.Equal(
            1,
            await verify.TabEvents.AsNoTracking()
                .CountAsync(e => e.TabId == tab.TabId && e.Type == TabEventType.TabAbandoned));
    }

    /// <summary>
    /// Reversing a comp is scoped to the venue by the service alone, and this is what says so.
    /// </summary>
    /// <remarks>
    /// <c>/api/tab-adjustments/{adjustmentId}/void</c> carries <c>ManagerOrAbove</c> and nothing
    /// else. <c>BranchScoped</c> is not on it and could not be: the route names no branch, no table
    /// and no tab, and that handler fails closed when it cannot find one - so applying it would
    /// refuse every manager in the product. The single <c>branchGuard.RequireAsync</c> inside
    /// <c>VoidAdjustmentAsync</c> is therefore the entire boundary between one venue's manager and
    /// another venue's takings, and it is reachable by guessing an id.
    /// </remarks>
    [SkippableFact]
    public async Task Reversing_a_comp_refuses_a_manager_from_another_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu, tables) = await ArrangeAsync(factory);

        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        var tab = await OpenTabAsync(factory, tables[0].QrToken);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var beforeComp = await OrderAsync(waiter, tab.TabId, menu.Wine, quantity: 1);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        const long CompAmd = 2_500L;

        var added = await manager.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/adjustments",
            new
            {
                kind = (int)AdjustmentKind.Comp,
                amountAmd = CompAmd,
                reason = "the wine was corked",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var adjustment = await added.Content.ReadFromJsonAsync<JsonElement>();
        var adjustmentId = adjustment.GetProperty("adjustmentId").GetGuid();

        Assert.Equal(CompAmd, adjustment.GetProperty("reductionAmd").GetInt64());
        Assert.False(adjustment.GetProperty("isVoided").GetBoolean());

        // Read back rather than computed here. The comp comes off the subtotal and the service
        // charge is recalculated on what is left, so the bill drops by more than the comp - and
        // pinning that formula in a test about authorisation would be pinning the wrong thing.
        var afterComp = await RemainingAsync(waiter, tab.TabId);

        Assert.True(afterComp < beforeComp, "The comp took nothing off the bill.");

        // A manager of another venue, holding a valid manager token, with only an id to go on.
        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/tab-adjustments/{adjustmentId}/void", new { })).StatusCode);

        // A waiter of the right branch is refused too: this is a manager's decision.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PostAsJsonAsync(
                $"/api/tab-adjustments/{adjustmentId}/void", new { })).StatusCode);

        // The comp is still standing after both attempts.
        Assert.Equal(afterComp, await RemainingAsync(waiter, tab.TabId));

        // Their own manager can, and the money comes back onto the bill.
        var voided = await manager.PostAsJsonAsync(
            $"/api/tab-adjustments/{adjustmentId}/void", new { });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.True((await voided.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isVoided").GetBoolean());

        Assert.Equal(beforeComp, await RemainingAsync(waiter, tab.TabId));
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private sealed record OpenTab(Guid TabId, Guid ParticipantId, string AccessToken);

    /// <summary>A table as these tests address it: the id to look for on the floor, and its code.</summary>
    private sealed record ScannableTable(Guid TableId, string QrToken);

    /// <summary>A branch with a menu, and its tables with the codes printed on them.</summary>
    private async Task<(AuthBranch Branch, TestMenu Menu, IReadOnlyList<ScannableTable> Tables)> ArrangeAsync(
        YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var tables = await db.DiningTables.AsNoTracking()
            .Where(t => t.BranchId == branch.BranchId)
            .OrderBy(t => t.Label)
            .Select(t => new ScannableTable(t.Id, t.QrToken))
            .ToListAsync();

        return (branch, menu, tables);
    }

    /// <summary>Scans the QR code the way a diner does, and opens a tab on that table.</summary>
    private static async Task<OpenTab> OpenTabAsync(YallaApiFactory factory, string qrToken)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken,
                deviceId = $"phone-{Guid.NewGuid():N}",
                displayName = "Ani",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tab = body.GetProperty("tab");

        return new OpenTab(
            tab.GetProperty("tabId").GetGuid(),
            tab.GetProperty("me").GetProperty("participantId").GetGuid(),
            body.GetProperty("token").GetProperty("accessToken").GetString()!);
    }

    /// <summary>
    /// Puts something on the bill as the waiter would, and answers with what the tab now owes -
    /// read back off the API rather than computed here, so the service charge is whatever the
    /// branch says it is.
    /// </summary>
    private static async Task<long> OrderAsync(
        HttpClient waiter,
        Guid tabId,
        Guid menuItemId,
        int quantity)
    {
        var response = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new
            {
                items = new[] { new { menuItemId, quantity } },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await RemainingAsync(waiter, tabId);
    }

    /// <summary>What the tab owes right now, as the staff view reports it.</summary>
    private static async Task<long> RemainingAsync(HttpClient staff, Guid tabId)
    {
        var view = await ReadAsync(staff, $"/api/tabs/{tabId}/participants");

        return view.GetProperty("totals").GetProperty("remainingAmd").GetInt64();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
