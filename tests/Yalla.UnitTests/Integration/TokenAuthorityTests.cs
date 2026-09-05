using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The three lifecycle rules a stateless token cannot enforce about itself.
/// </summary>
/// <remarks>
/// A JWT is a statement about the past. A participant token's lifetime is meant to track its tab,
/// but the close time is unknown when the token is minted; a participant removed from a tab keeps
/// the token they were given; a tablet left in a taxi holds a year-long one. Each is checked in the
/// authorisation pipeline rather than in a handler, because a check inside a handler is one the
/// next handler forgets and the failure is silent.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class TokenAuthorityTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 15. removed means removed

    /// <summary>
    /// The approval flow exists so the stranger from the next table cannot order on your bill.
    /// It only means something if removing them stops the token they already hold.
    /// </summary>
    [SkippableFact]
    public async Task A_removed_participants_existing_token_is_refused_on_reads_and_mutations()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qr) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qr, "phone-host");
        var guest = await OpenAsync(anonymous, qr, "phone-guest");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { })).EnsureSuccessStatusCode();

        // Approved, so the token works. The refusal below is the removal, not a broken token.
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);

        var removed = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/remove", new { });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // Same token, immediately after. Not "after the cache lapses".
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guestClient.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/display-name", new { displayName = "Still here" })).StatusCode);

        // And their record survives them, which is the other half of the rule.
        await using var verify = fixture.CreateContext(factory.Clock);
        var row = await verify.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == guest.ParticipantId);
        Assert.Equal(ParticipantStatus.Removed, row.Status);
    }

    // ------------------------------------------------------------ 16. a closed tab

    /// <summary>
    /// Once the receipt grace period is over the token is simply finished, and says so - a client
    /// can render "this tab is closed" rather than a generic auth failure.
    /// </summary>
    [SkippableFact]
    public async Task A_token_for_a_closed_tab_is_refused_with_a_reason_once_the_receipt_grace_is_over()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qr) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qr, "phone-host");
        using var diner = factory.CreateClientWithToken(host.AccessToken);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var tab = await db.Tabs.FirstAsync(t => t.Id == host.TabId);
            tab.Close(factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        // Just closed: the receipt is still readable, which is deliberate - a diner may look at
        // what they paid for a while afterwards.
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);

        // Past the grace period, the token is over.
        factory.Clock.Advance(TimeSpan.FromMinutes(121));

        var refused = await diner.GetAsync($"/api/tabs/{host.TabId}");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // 401 rather than 403, and carrying a reason: the holder did nothing wrong, their tab ended.
        Assert.Contains(refused.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    // ------------------------------------------------------------ 17. settling is not changing

    [SkippableFact]
    public async Task A_token_on_a_closing_tab_can_read_but_not_change_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qr) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qr, "phone-host");
        using var diner = factory.CreateClientWithToken(host.AccessToken);

        // While it is open, changing it is fine.
        Assert.Equal(
            HttpStatusCode.OK,
            (await diner.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/display-name", new { displayName = "Aram" })).StatusCode);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        (await waiter.PostAsJsonAsync($"/api/tabs/{host.TabId}/closing", new { })).EnsureSuccessStatusCode();

        // Reading a bill that is being settled: yes.
        var read = await diner.GetAsync($"/api/tabs/{host.TabId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(
            (int)TabStatus.Closing,
            (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());

        // Changing it: no. Somebody who paid their share and left must not find it moving.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await diner.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/display-name", new { displayName = "Changed my mind" })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await diner.PostAsJsonAsync($"/api/tabs/{host.TabId}/join-tokens", new { })).StatusCode);
    }

    // ------------------------------------------------------------ 18. the revoked tablet

    /// <summary>
    /// Prompt 3's guarantee, re-asserted through the shared check that now serves all three rules.
    /// The cache in front of it must never delay this: the manager taps revoke and means now.
    /// </summary>
    [SkippableFact]
    public async Task A_revoked_device_token_is_refused_immediately_through_the_shared_check()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        using var tablet = factory.CreateClientWithToken(deviceToken);

        // The device token can do exactly one thing, and it can do it now.
        var beforeRevoke = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        Guid deviceId;
        using (var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch)))
        {
            var devices = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branch.BranchId}/devices");
            deviceId = devices[0].GetProperty("id").GetGuid();

            (await manager.PostAsJsonAsync(
                $"/api/branches/{branch.BranchId}/devices/{deviceId}/revoke", new { })).EnsureSuccessStatusCode();
        }

        // Immediately after, with no wait for a cache to lapse.
        var afterRevoke = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });

        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<(AuthBranch Branch, string QrToken)> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        var qr = await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        return (branch, qr);
    }

    private sealed record Opened(Guid TabId, Guid ParticipantId, string AccessToken);

    private static async Task<Opened> OpenAsync(HttpClient client, string qrToken, string deviceId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7(), displayName = deviceId });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tab = body.GetProperty("tab");

        return new Opened(
            tab.GetProperty("tabId").GetGuid(),
            tab.GetProperty("me").GetProperty("participantId").GetGuid(),
            body.GetProperty("token").GetProperty("accessToken").GetString()!);
    }
}
