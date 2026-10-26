using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Takeaway on the branch's change stream (2026-10-26): a tablet following <c>GET /api/branches/{id}/tables/changes</c>
/// sees one signal 8 row for every move of every takeaway order - placed, accepted, ready, collected, declined by the
/// counter and by the sweep, cancelled by the guest, not collected, placed and accepted at once - and none for a replay.
/// See <c>docs/takeaway.md</c> and <c>TakeawayChangeStream</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TakeawayChangeStreamTests(SqlServerFixture fixture)
{
    /// <summary>Tomorrow at 14:00 in Yerevan (UTC+4): inside the test branch's 10:00-23:00 every day.</summary>
    private static readonly DateTime Afternoon = TestNow.UtcNow.Date.AddDays(1).AddHours(10);

    [SkippableFact]
    public async Task A_client_following_the_change_stream_sees_every_takeaway_transition_and_no_replay()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString).With("Outbox:Enabled", "false");

        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        await SettingsAsync(manager, branch, autoAccept: false);

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        // The tablet's position: where the floor it drew says the stream ends.
        var stream = new Follower(waiter, branch.BranchId,
            (await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor")).GetProperty("maxSequence").GetInt64());

        // A: placed - and the app's retry of the same send writes nothing.
        var aBody = Order(branch, menu);
        var a = (await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", aBody))).GetProperty("id").GetGuid();
        await stream.SeesAsync("takeaway placed");
        await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", aBody));
        await stream.SeesNothingAsync();

        // Accepted - a second tablet's accept is the same answer and writes nothing - then ready, then collected.
        factory.Clock.UtcNow = Afternoon.AddMinutes(1);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{a}/accept", new { readyAt = Afternoon.AddMinutes(20) }));
        await stream.SeesAsync("takeaway accepted");
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{a}/accept", new { readyAt = Afternoon.AddMinutes(25) }));
        await stream.SeesNothingAsync();

        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{a}/ready", null));
        await stream.SeesAsync("takeaway ready");

        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{a}/collect", new { method = (int)TakeawayPaymentMethod.Cash }));
        await stream.SeesAsync("takeaway collected");

        // B: declined at the counter. C: cancelled by the guest, and her retry writes nothing.
        var b = await PlaceAsync(diner, branch, menu);
        await stream.SeesAsync("takeaway placed");
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{b}/decline", new { reason = "Out of khachapuri" }));
        await stream.SeesAsync("takeaway declined");

        var c = await PlaceAsync(diner, branch, menu);
        await stream.SeesAsync("takeaway placed");
        await OkAsync(await diner.PostAsJsonAsync($"/api/diner/takeaway-orders/{c}/cancel", new { }));
        await stream.SeesAsync("takeaway cancelled");
        await OkAsync(await diner.PostAsJsonAsync($"/api/diner/takeaway-orders/{c}/cancel", new { }));
        await stream.SeesNothingAsync();

        // D: nobody answers it in five minutes, and the sweep declines it.
        factory.Clock.UtcNow = Afternoon.AddMinutes(2);
        await PlaceAsync(diner, branch, menu);
        await stream.SeesAsync("takeaway placed");

        factory.Clock.UtcNow = Afternoon.AddMinutes(7);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<TakeawayExpirySweep>().RunOnceAsync() >= 1);
        }

        await stream.SeesAsync("takeaway declined");

        // E: accepted, ready, and nobody comes for it.
        var e = await PlaceAsync(diner, branch, menu);
        await stream.SeesAsync("takeaway placed");
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{e}/accept", new { readyAt = Afternoon.AddMinutes(27) }));
        await stream.SeesAsync("takeaway accepted");
        factory.Clock.UtcNow = Afternoon.AddMinutes(27);
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{e}/ready", null));
        await stream.SeesAsync("takeaway ready");

        factory.Clock.UtcNow = Afternoon.AddMinutes(57);
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{e}/no-show", null));
        await stream.SeesAsync("takeaway not collected");
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{e}/no-show", null));
        await stream.SeesNothingAsync();

        // F: the branch accepts by itself - placed and accepted in one step is one row.
        await SettingsAsync(manager, branch, autoAccept: true);
        await stream.SeesNothingAsync();
        var f = await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(branch, menu)));
        Assert.Equal((int)TakeawayStatus.Accepted, f.GetProperty("status").GetInt32());
        await stream.SeesAsync("takeaway placed");

        // The floor still draws, and agrees on where the stream ends.
        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(stream.After, floor.GetProperty("maxSequence").GetInt64());
        Assert.Equal(2, floor.GetProperty("tables").GetArrayLength());
    }

    /// <summary>A tablet's poll of the change stream, from where it last read.</summary>
    private sealed class Follower(HttpClient client, Guid branchId, long after)
    {
        public long After { get; private set; } = after;

        /// <summary>Exactly one new entry: the takeaway signal, about no table, the system's, saying what happened.</summary>
        public async Task SeesAsync(string reason)
        {
            var page = await ReadAsync(client, $"/api/branches/{branchId}/tables/changes?afterSequence={After}");
            var change = Assert.Single(page.GetProperty("changes").EnumerateArray());

            Assert.Equal((int)TableChangeSignal.TakeawayChanged, change.GetProperty("signal").GetInt32());
            Assert.Equal(reason, change.GetProperty("reason").GetString());
            Assert.True(IsNull(change, "diningTableId"), change.ToString());
            Assert.True(IsNull(change, "tableLabel"), change.ToString());
            Assert.Equal((int)TableStatus.Free, change.GetProperty("fromStatus").GetInt32());
            Assert.Equal((int)TableStatus.Free, change.GetProperty("toStatus").GetInt32());
            Assert.Equal((int)ActorType.System, change.GetProperty("actorType").GetInt32());
            Assert.True(IsNull(change, "reservationId") && IsNull(change, "tabId") && IsNull(change, "tableSessionId"));

            var sequence = change.GetProperty("sequence").GetInt64();
            Assert.True(sequence > After);
            Assert.Equal(sequence, page.GetProperty("maxSequence").GetInt64());
            After = sequence;
        }

        public async Task SeesNothingAsync()
        {
            var page = await ReadAsync(client, $"/api/branches/{branchId}/tables/changes?afterSequence={After}");

            Assert.Empty(page.GetProperty("changes").EnumerateArray());
            Assert.Equal(After, page.GetProperty("maxSequence").GetInt64());
        }

        private static bool IsNull(JsonElement row, string name) =>
            !row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
    }

    private static object Order(AuthBranch branch, TestMenu menu) => new
    {
        branchId = branch.BranchId,
        pickup = new { asap = true },
        lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } },
        clientCommandId = Guid.CreateVersion7(),
    };

    private static async Task<Guid> PlaceAsync(HttpClient diner, AuthBranch branch, TestMenu menu) =>
        (await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(branch, menu)))).GetProperty("id").GetGuid();

    private static async Task SettingsAsync(HttpClient manager, AuthBranch branch, bool autoAccept) =>
        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/takeaway-settings", new
        {
            enabled = true,
            autoAccept,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress = 5,
        }));
}
