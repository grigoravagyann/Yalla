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

    // ------------------------------------------------------------ settlement mode, over HTTP

    /// <summary>
    /// The settlement flow driven the way a client drives it: set, stick, and lock once money exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other settlement test calls <c>ITabService</c> directly, which cannot see a request
    /// body at all. The two that did go over HTTP asserted a 403 - which fires on the participant
    /// policy, before the body is ever read - and a bare 200. So nothing pinned that the mode a
    /// <i>client</i> sends is the mode the tab ends up in, and nothing pinned the lock as a status
    /// code a client has to handle.
    /// </para>
    /// <para>
    /// The body-shape half matters because <c>[Required]</c> on <c>SetSettlementModeRequest</c>
    /// reaches the OpenAPI schema and nothing enforces it at runtime: there is no validation filter
    /// in this pipeline. A misnamed or absent field therefore binds
    /// <c>default(SettlementMode)</c>, which is <c>0</c> and not a defined member, so the refusal
    /// comes out of the domain guard rather than the edge. It is a good refusal - 400, naming
    /// <c>settlementMode</c> in <c>context.field</c> - and that is worth holding still, because it
    /// is what tells a client which field it got wrong.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task The_settlement_mode_a_client_sends_is_the_mode_the_tab_ends_up_in()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        var url = $"/api/tabs/{host.TabId}/settlement-mode";

        // Every mode, set and then read back off a fresh GET - not merely a 200.
        foreach (var mode in new[]
                 {
                     SettlementMode.EveryonePaysOwnItems,
                     SettlementMode.AnyonePaysAnyAmount,
                     SettlementMode.HostPaysEverything,
                 })
        {
            var response = await hostClient.PostAsJsonAsync(url, new { settlementMode = mode });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var returned = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(mode, EnumOf<SettlementMode>(returned.GetProperty("settlementMode")));

            var reread = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");
            Assert.Equal(mode, EnumOf<SettlementMode>(reread.GetProperty("settlementMode")));
        }

        // The shapes a client can get wrong. All four answer identically, and the answer names the
        // field - which is what a client needs in order to find out it sent the wrong one.
        foreach (var (label, body) in new (string, object)[]
                 {
                     ("the field misnamed", new { mode = SettlementMode.EveryonePaysOwnItems }),
                     ("the field absent", new { }),
                     ("out of range", new { settlementMode = 99 }),
                     ("the undefined zero", new { settlementMode = 0 }),
                 })
        {
            var refused = await hostClient.PostAsJsonAsync(url, body);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("invalid-request", problem.GetProperty("code").GetString());
            Assert.Equal(
                "settlementMode",
                problem.GetProperty("context").GetProperty("field").GetString());

            // And none of them moved the mode. The last accepted write still stands.
            var unchanged = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");
            Assert.Equal(
                SettlementMode.HostPaysEverything,
                EnumOf<SettlementMode>(unchanged.GetProperty("settlementMode")));
        }
    }

    /// <summary>
    /// The other half of the flow: the mode locks the moment money exists, and says so as a 409.
    /// </summary>
    /// <remarks>
    /// Proven at the service level already, but never as a status code. A client that cannot tell
    /// "you may not change this any more" from "something went wrong" shows the wrong thing to a
    /// host standing at a table with a waiter waiting for an answer.
    /// </remarks>
    [SkippableFact]
    public async Task The_settlement_mode_locks_once_a_payment_exists_and_answers_409()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        IReadOnlyList<string> qrTokens;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            qrTokens = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId)
                .OrderBy(t => t.Label)
                .Select(t => t.QrToken)
                .ToListAsync();
        }

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-host");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        var url = $"/api/tabs/{host.TabId}/settlement-mode";

        // Before any money: freely changed.
        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.PostAsJsonAsync(
                url, new { settlementMode = SettlementMode.EveryonePaysOwnItems })).StatusCode);

        (await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            })).EnsureSuccessStatusCode();

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var paid = await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/payments/cash",
            new { amountAmd = 100L, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);

        // Money exists, so the split is settled. Refused as a conflict, not a 400 or a 403: the
        // host is entitled to ask and the request is well formed; the tab has moved past it.
        var locked = await hostClient.PostAsJsonAsync(
            url, new { settlementMode = SettlementMode.HostPaysEverything });

        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);

        // And the mode in force is the one from before the payment, not the refused one.
        var view = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");

        Assert.Equal(
            SettlementMode.EveryonePaysOwnItems,
            EnumOf<SettlementMode>(view.GetProperty("settlementMode")));

        Assert.True(view.GetProperty("settlementModeLocked").GetBoolean());
    }

    // ------------------------------------------------------------ the diner-flow fixes

    /// <summary>The tab names the venue and branch it is at, so the phone need not ask elsewhere.</summary>
    [SkippableFact]
    public async Task The_tab_names_the_venue_and_the_branch_it_is_at()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);

        string venueName;
        string branchName;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var names = await db.Branches.AsNoTracking()
                .Where(b => b.Id == branch.BranchId)
                .Select(b => new { b.Name, VenueName = b.Venue.Name })
                .FirstAsync();

            venueName = names.VenueName;
            branchName = names.Name;
        }

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-names");

        using var diner = factory.CreateClientWithToken(host.AccessToken);
        var tab = await ReadAsync(diner, $"/api/tabs/{host.TabId}");

        Assert.Equal(venueName, tab.GetProperty("venueName").GetString());
        Assert.Equal(branchName, tab.GetProperty("branchName").GetString());
    }

    /// <summary>
    /// An order with no command id is refused before anything is placed.
    /// </summary>
    /// <remarks>
    /// Left out, the field binds <c>Guid.Empty</c>, and the replay lookup answered with whichever
    /// order first used <c>Guid.Empty</c> - on any tab, with that tab's totals.
    /// </remarks>
    [SkippableFact]
    public async Task An_order_without_a_command_id_is_refused_and_places_nothing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);
        var menu = await MenuAsync(branch);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-no-command");

        using var diner = factory.CreateClientWithToken(host.AccessToken);

        var refused = await diner.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new { items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } } });

        await AssertRefusedForTheCommandIdAsync(refused);

        await using var verify = fixture.CreateContext(factory.Clock);

        Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == host.TabId));
    }

    /// <summary>
    /// The staff money commands refuse a missing command id the same way, before they run.
    /// </summary>
    [SkippableTheory]
    [InlineData("void")]
    [InlineData("adjustment")]
    [InlineData("cash")]
    public async Task A_staff_money_command_without_a_command_id_is_refused(string command)
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);
        var menu = await MenuAsync(branch);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-money");

        using var diner = factory.CreateClientWithToken(host.AccessToken);

        var ordered = await diner.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, ordered.StatusCode);

        var lineId = (await ordered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lines")[0].GetProperty("lineId").GetGuid();

        using var staff = factory.CreateClientWithToken(command == "adjustment"
            ? await StaffAuthTests.SignInManagerAsync(factory, branch)
            : await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var refused = command switch
        {
            "void" => await staff.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/lines/{lineId}/void", new { reason = "Wrong dish" }),
            "adjustment" => await staff.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/adjustments",
                new { tabOrderLineId = lineId, kind = 2, amountAmd = 100, reason = "Sorry" }),
            _ => await staff.PostAsJsonAsync(
                $"/api/tabs/{host.TabId}/payments/cash", new { amountAmd = 100, tipAmd = 0 }),
        };

        await AssertRefusedForTheCommandIdAsync(refused);

        await using var verify = fixture.CreateContext(factory.Clock);

        Assert.False(await verify.TabOrderLines.AnyAsync(l => l.Id == lineId && l.VoidedAtUtc != null));
        Assert.False(await verify.TabAdjustments.AnyAsync(a => a.TabId == host.TabId));
        Assert.False(await verify.Payments.AnyAsync(p => p.TabId == host.TabId));
    }

    /// <summary>
    /// A guest who leaves is taken off the tab, refused on their next call, and not split into what
    /// is ordered after they walked out.
    /// </summary>
    /// <remarks>
    /// There was no way to leave, so the app faked it: it cleared the tab on the phone while the
    /// server kept the guest approved - on every shared bottle ordered afterwards and in the split.
    /// </remarks>
    [SkippableFact]
    public async Task A_guest_who_leaves_is_off_the_tab_and_off_what_is_ordered_after()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, qrTokens) = await ArrangeAsync(factory);
        var menu = await MenuAsync(branch);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-staying");
        var guest = await JoinApprovedAsync(factory, anonymous, host, qrTokens[0], "phone-leaving");

        using var guestClient = factory.CreateClientWithToken(guest.AccessToken);

        var left = await guestClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/leave", new { });

        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        Assert.Equal(
            ParticipantStatus.Removed,
            EnumOf<ParticipantStatus>((await left.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status")));

        // Their token no longer opens the tab.
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);

        // The host no longer sees them at the table.
        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        var tab = await ReadAsync(hostClient, $"/api/tabs/{host.TabId}");

        Assert.DoesNotContain(
            tab.GetProperty("participants").EnumerateArray(),
            p => p.GetProperty("participantId").GetGuid() == guest.ParticipantId);

        // And a bottle ordered after they walked out is not split with them.
        var ordered = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Wine, quantity = 1, isShared = true } },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, ordered.StatusCode);

        var sharedWith = (await ordered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lines")[0].GetProperty("sharedWithParticipantIds").EnumerateArray()
            .Select(p => p.GetGuid())
            .ToList();

        Assert.Equal([host.ParticipantId], sharedWith);
    }

    /// <summary>
    /// A host who leaves hands the tab to whoever has been on it longest, which is what the app
    /// already tells them will happen.
    /// </summary>
    [SkippableFact]
    public async Task A_host_who_leaves_hands_the_tab_to_whoever_has_been_on_it_longest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        // Two minutes ago and then one, so "longest" is a fact rather than a tie. Back rather than
        // forward: a token is not valid before the instant it says it was issued.
        factory.Clock.UtcNow = DateTime.UtcNow.AddMinutes(-2);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-first");
        var earlier = await JoinApprovedAsync(factory, anonymous, host, qrTokens[0], "phone-second");

        factory.Clock.UtcNow = DateTime.UtcNow.AddMinutes(-1);

        var later = await JoinApprovedAsync(factory, anonymous, host, qrTokens[0], "phone-third");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);

        Assert.Equal(
            HttpStatusCode.OK,
            (await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/leave", new { })).StatusCode);

        using var earlierClient = factory.CreateClientWithToken(earlier.AccessToken);
        var tab = await ReadAsync(earlierClient, $"/api/tabs/{host.TabId}");

        Assert.Equal(earlier.ParticipantId, tab.GetProperty("hostParticipantId").GetGuid());
        Assert.Equal(ParticipantRole.Host, EnumOf<ParticipantRole>(tab.GetProperty("me").GetProperty("role")));

        // Hosting brings sight of the total and the right to pay, as it does when staff reassign.
        Assert.True(tab.GetProperty("me").GetProperty("canPay").GetBoolean());

        // The later joiner stays a guest, and the old host is off the tab.
        using var laterClient = factory.CreateClientWithToken(later.AccessToken);
        var laterView = await ReadAsync(laterClient, $"/api/tabs/{host.TabId}");

        Assert.Equal(ParticipantRole.Guest, EnumOf<ParticipantRole>(laterView.GetProperty("me").GetProperty("role")));
        Assert.Equal(HttpStatusCode.Forbidden, (await hostClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);
    }

    /// <summary>With nobody approved to hand it to, the host cannot walk away from the tab.</summary>
    [SkippableFact]
    public async Task A_host_alone_on_the_tab_cannot_leave_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-alone");

        // Somebody pending does not count: they cannot host a tab they have not been let onto.
        await OpenAsync(anonymous, qrTokens[0], "phone-waiting");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        var refused = await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/leave", new { });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hostClient.GetAsync($"/api/tabs/{host.TabId}")).StatusCode);
    }

    /// <summary>
    /// A code read back in capitals still opens the tab - by the server's own rule rather than the
    /// database's default collation.
    /// </summary>
    [SkippableFact]
    public async Task A_qr_code_read_back_in_capitals_opens_the_same_tab()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var shouted = await OpenAsync(anonymous, qrTokens[0].ToUpperInvariant(), "phone-capitals");
        var typed = await OpenAsync(anonymous, qrTokens[0], "phone-as-printed");

        Assert.Equal(TabOpenOutcome.OpenedNewSession, shouted.Outcome);
        Assert.Equal(shouted.TabId, typed.TabId);
    }

    /// <summary>
    /// The QR token column compares exactly, so the lookup cannot come to depend on a database
    /// default nobody chose.
    /// </summary>
    /// <remarks>
    /// Scanning worked only because the column had no collation of its own and SQL Server's
    /// default is case-insensitive, which quietly hid the diner app upper-casing every scan.
    /// </remarks>
    [SkippableFact]
    public async Task The_qr_token_column_compares_exactly_rather_than_by_the_database_default()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(DateTime.UtcNow));

        var collation = await db.Database
            .SqlQueryRaw<string>(
                "SELECT c.collation_name AS [Value] FROM sys.columns c "
                + "WHERE c.object_id = OBJECT_ID(N'dbo.DiningTables') AND c.name = N'QrToken'")
            .SingleAsync();

        Assert.EndsWith("_BIN2", collation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The share link is a path on the diner link domain, which the app's /join route and its
    /// Android intent filter both expect.
    /// </summary>
    /// <remarks>
    /// It was a query string on the web console's local host, which the app's link parser reduced
    /// to the word "join" and which no phone would ever open in the app.
    /// </remarks>
    [SkippableFact]
    public async Task The_share_link_is_a_path_on_the_diner_link_domain()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (_, qrTokens) = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var host = await OpenAsync(anonymous, qrTokens[0], "phone-sharing");

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);
        var invite = await hostClient.PostAsJsonAsync($"/api/tabs/{host.TabId}/join-tokens", new { });

        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);

        var body = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        Assert.Equal($"https://yalla.am/join/{Uri.EscapeDataString(token)}", body.GetProperty("shareUrl").GetString());
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

    private async Task<TestMenu> MenuAsync(AuthBranch branch)
    {
        await using var db = fixture.CreateContext(new TestClock(DateTime.UtcNow));

        return await TestMenuBuilder.CreateAsync(db, branch.BranchId);
    }

    /// <summary>Scans onto a tab that is already open, and is approved by its host.</summary>
    private static async Task<Opened> JoinApprovedAsync(
        YallaApiFactory factory,
        HttpClient anonymous,
        Opened host,
        string qrToken,
        string deviceId)
    {
        var joined = await OpenAsync(anonymous, qrToken, deviceId);

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, joined.Outcome);

        using var hostClient = factory.CreateClientWithToken(host.AccessToken);

        var approved = await hostClient.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/participants/{joined.ParticipantId}/approve", new { });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        return joined;
    }

    /// <summary>A 400 from the command-id filter itself, not merely some 400.</summary>
    private static async Task AssertRefusedForTheCommandIdAsync(HttpResponseMessage refused)
    {
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid-request", problem.GetProperty("code").GetString());
        Assert.Contains(
            "clientCommandId",
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
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
