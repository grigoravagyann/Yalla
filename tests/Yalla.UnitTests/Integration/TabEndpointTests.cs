using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The tab surface through the real pipeline: the policies, the exception mapping, and the exact
/// shape of the JSON a client receives.
/// </summary>
/// <remarks>
/// The service tests prove the rules; these prove the rules are <i>applied</i> - that the
/// participant policy sits on every diner route, that the staff routes refuse a participant token,
/// and that a hidden total is absent from the body rather than serialised as null.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class TabEndpointTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 14. the tab boundary, with real tabs

    [SkippableFact]
    public async Task A_participant_token_for_tab_A_gets_403_on_tab_B()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();

        // Two tables, two scans, two tabs - both opened the way a diner actually opens one.
        var tableSeven = await OpenAsync(anonymous, qrTokens[0], "phone-seven");
        var tableEight = await OpenAsync(anonymous, qrTokens[1], "phone-eight");

        Assert.Equal(TabOpenOutcome.OpenedNewSession, tableSeven.Outcome);
        Assert.NotEqual(tableSeven.TabId, tableEight.TabId);

        using var diner = factory.CreateClientWithToken(tableSeven.AccessToken);

        var ownTab = await diner.GetAsync($"/api/tabs/{tableSeven.TabId}");
        Assert.Equal(HttpStatusCode.OK, ownTab.StatusCode);

        var neighboursTab = await diner.GetAsync($"/api/tabs/{tableEight.TabId}");
        Assert.Equal(HttpStatusCode.Forbidden, neighboursTab.StatusCode);

        // Every participant action, not only the read.
        var invite = await diner.PostAsJsonAsync($"/api/tabs/{tableEight.TabId}/join-tokens", new { });
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

        var rename = await diner.PostAsJsonAsync(
            $"/api/tabs/{tableEight.TabId}/display-name", new { displayName = "Not mine" });
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);

        var split = await diner.PostAsJsonAsync(
            $"/api/tabs/{tableEight.TabId}/settlement-mode", new { settlementMode = SettlementMode.HostPaysEverything });
        Assert.Equal(HttpStatusCode.Forbidden, split.StatusCode);

        var approve = await diner.PostAsJsonAsync(
            $"/api/tabs/{tableEight.TabId}/participants/{tableEight.ParticipantId}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        // And nothing at all without a token.
        var unauthenticated = await anonymous.GetAsync($"/api/tabs/{tableSeven.TabId}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    // ------------------------------------------------------------ 8. the invariant, as a 400

    [SkippableFact]
    public async Task CanPay_without_CanSeeTableTotal_is_a_400_and_a_guest_setting_flags_is_a_403()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host");
        var guest = await OpenAsync(anonymous, qrTokens[0], "phone-guest");

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, guest.Outcome);
        Assert.Equal(host.TabId, guest.TabId);

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        var approve = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var refused = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/permissions",
            new { canOrder = true, canSeeTableTotal = false, canPay = true });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var granted = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/permissions",
            new { canOrder = true, canSeeTableTotal = true, canPay = true });

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var body = await granted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("canPay").GetBoolean());

        // On the tab, but not the host: 403 from the service, through the mapper.
        var guestAttempt = await guestClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{host.ParticipantId}/permissions",
            new { canOrder = false, canSeeTableTotal = true, canPay = true });

        Assert.Equal(HttpStatusCode.Forbidden, guestAttempt.StatusCode);
    }

    // ------------------------------------------------------------ 9. absent on the wire

    [SkippableFact]
    public async Task A_guest_without_the_total_gets_their_own_lines_and_no_aggregate_member_in_the_body()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host", hideTotalFromGuests: true);
        var guest = await OpenAsync(anonymous, qrTokens[0], "phone-guest");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { })).EnsureSuccessStatusCode();

        var testBranch = new TestBranch(branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId, branch.TableIds, "Asia/Yerevan");
        await TabTestData.AddOrderLineAsync(fixture, factory.Clock, testBranch, host.TabId, host.ParticipantId, "Coffee", 2_500L);
        await TabTestData.AddOrderLineAsync(fixture, factory.Clock, testBranch, host.TabId, guest.ParticipantId, "Tea", 2_400L);
        await TabTestData.SetTotalsAsync(fixture, factory.Clock, host.TabId, subtotalAmd: 4_900L, serviceChargeAmd: 490L);

        var guestView = await ReadAsync(guestClient, $"/api/tabs/{host.TabId}");

        Assert.False(guestView.GetProperty("tableTotalVisible").GetBoolean());

        // Absent. Not null, not zero: no member at all.
        Assert.False(guestView.TryGetProperty("tableTotal", out _));
        Assert.False(guestView.TryGetProperty("tableLines", out _));

        var myLines = guestView.GetProperty("myLines");
        Assert.Equal(1, myLines.GetArrayLength());
        Assert.Equal("Tea", myLines[0].GetProperty("name").GetString());
        Assert.Equal(2_400L, guestView.GetProperty("myItemsSubtotalAmd").GetInt64());

        var hostView = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");

        Assert.True(hostView.GetProperty("tableTotalVisible").GetBoolean());
        Assert.Equal(5_390L, hostView.GetProperty("tableTotal").GetProperty("totalAmd").GetInt64());
        Assert.Equal(2, hostView.GetProperty("tableLines").GetArrayLength());
    }

    // ------------------------------------------------------------ 11. staff only, branch scoped

    [SkippableFact]
    public async Task Staff_can_reassign_the_host_over_http_and_a_participant_cannot()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host");
        var guest = await OpenAsync(anonymous, qrTokens[0], "phone-guest");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{guest.ParticipantId}/approve", new { })).EnsureSuccessStatusCode();

        // The host's own token is a participant token, and the route is staff-only.
        var participantAttempt = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/reassign-host", new { newHostParticipantId = guest.ParticipantId });
        Assert.Equal(HttpStatusCode.Forbidden, participantAttempt.StatusCode);

        // A waiter from another branch is refused too: BranchScoped resolves the branch from the tab.
        AuthBranch otherBranch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            otherBranch = await AuthTestData.CreateBranchAsync(db);
        }

        using var otherWaiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, otherBranch));
        var otherBranchAttempt = await otherWaiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/reassign-host", new { newHostParticipantId = guest.ParticipantId });
        Assert.Equal(HttpStatusCode.Forbidden, otherBranchAttempt.StatusCode);

        // The branch's own waiter can, and sees everyone on the table.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var reassigned = await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/reassign-host", new { newHostParticipantId = guest.ParticipantId });
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);

        var staffView = await reassigned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(guest.ParticipantId, staffView.GetProperty("hostParticipantId").GetGuid());
        Assert.Equal(2, staffView.GetProperty("participants").GetArrayLength());

        var roster = await ReadAsync(waiter, $"/api/tabs/{host.TabId}/participants");
        Assert.Equal(2, roster.GetProperty("participants").GetArrayLength());

        // And a participant token cannot read the staff roster.
        var rosterAsDiner = await hostClient.GetAsync($"/api/tabs/{host.TabId}/participants");
        Assert.Equal(HttpStatusCode.Forbidden, rosterAsDiner.StatusCode);
    }

    // ------------------------------------------------------------ 13. closing, over http

    [SkippableFact]
    public async Task After_closing_joining_is_refused_and_nobody_may_order()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host");
        using var hostClient = factory.CreateClientWithToken(host.AccessToken);

        var invitation = await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/join-tokens", new { });
        Assert.Equal(HttpStatusCode.OK, invitation.StatusCode);
        var joinToken = (await invitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var closing = await waiter.PostAsJsonAsync($"/api/tabs/{host.TabId}/closing", new { });
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
        Assert.Equal(TabStatus.Closing, EnumOf<TabStatus>((await closing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status")));

        // The invitation died with the closing.
        var byInvitation = await anonymous.PostAsJsonAsync(
            "/api/tabs/join", new { joinToken, deviceId = "phone-late" });
        Assert.Equal(HttpStatusCode.Unauthorized, byInvitation.StatusCode);

        // The table's QR code no longer lets anyone onto this tab.
        var byScan = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken = qrTokens[0], deviceId = "phone-stranger", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, byScan.StatusCode);

        // Nor can the host issue a new invitation. Refused by the policy now rather than by the
        // service: a tab being settled takes no changes at all, so the request never reaches the
        // code that would have explained which particular change was refused.
        var reinvite = await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/join-tokens", new { });
        Assert.Equal(HttpStatusCode.Forbidden, reinvite.StatusCode);

        // The host may still read the tab, and is told they may not order.
        var view = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");
        Assert.Equal(TabStatus.Closing, EnumOf<TabStatus>(view.GetProperty("status")));
        Assert.False(view.GetProperty("me").GetProperty("canOrderNow").GetBoolean());
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<(AuthBranch Branch, IReadOnlyList<string> QrTokens)> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        var qrTokens = await db.DiningTables.AsNoTracking()
            .Where(t => t.BranchId == branch.BranchId)
            .OrderBy(t => t.Label)
            .Select(t => t.QrToken)
            .ToListAsync();

        return (branch, qrTokens);
    }

    private sealed record Opened(Guid TabId, Guid ParticipantId, string AccessToken, TabOpenOutcome Outcome);

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

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An enum however it was serialised - as its number or its name.</summary>
    private static T EnumOf<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);
}
