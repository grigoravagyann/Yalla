using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Somebody scans a table whose tab is already open and lands pending. The host finds out: on the
/// tab's stream, which every phone at the table polls, and - when the host has an account - on the
/// lock screen.
/// </summary>
/// <remarks>
/// <para>
/// Before this the joiner sat on "waiting for the host" while the host, who had no reason to open
/// the People screen, never learned anybody was waiting. <c>ParticipantJoined</c> was declared and
/// classified and never written.
/// </para>
/// <para>
/// Only a join that lands <b>pending</b> is announced. The first phone onto a tab staff opened
/// hosts it, and the booker joining their own booking's sitting is approved on the spot and already
/// moves the roster with <c>ParticipantApproved</c>; neither is waiting for anybody. See
/// <c>docs/tabs.md</c>, section 3.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class JoinRequestTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 18, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>A phone with no account behind it.</summary>
    private static TestActor Anonymous => new(ActorType.Diner, null, null, null);

    // ------------------------------------------------------------ the stream

    /// <summary>
    /// One <c>ParticipantJoined</c> per pending join, shaped exactly as the other roster events are,
    /// and the same phone scanning again is recognised rather than announced twice.
    /// </summary>
    [SkippableFact]
    public async Task A_pending_join_is_on_the_stream_once_in_the_shape_of_every_other_roster_event()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, branch, qr) = await ArrangeAsync();
        var host = await OpenAsync(clock, Anonymous, qr, "phone-host", "Aram");

        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        Assert.Equal(TabOpenOutcome.JoinedExistingTab, anna.Outcome);
        Assert.Equal(ParticipantStatus.PendingApproval, anna.Tab.Me.Status);

        // The phone locked and she scanned again: the same row, and nothing new to say about it.
        var again = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        Assert.Equal(anna.Tab.Me.ParticipantId, again.Tab.Me.ParticipantId);

        var joined = Assert.Single(await StreamAsync(clock, host.Tab.TabId));
        Assert.Equal(1L, joined.Sequence);
        Assert.Equal(TabEventType.ParticipantJoined, joined.Type);

        using var payload = JsonDocument.Parse(joined.PayloadJson);
        var root = payload.RootElement;

        Assert.Equal(
            ["canOrder", "canPay", "canSeeTableTotal", "displayName", "participantId", "status"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(anna.Tab.Me.ParticipantId, root.GetProperty("participantId").GetGuid());
        Assert.Equal("Anna", root.GetProperty("displayName").GetString());
        Assert.Equal((int)ParticipantStatus.PendingApproval, root.GetProperty("status").GetInt32());
        Assert.True(root.GetProperty("canOrder").GetBoolean());
        Assert.True(root.GetProperty("canSeeTableTotal").GetBoolean());
        Assert.False(root.GetProperty("canPay").GetBoolean());

        // Every member it carries has a decision in the redactor, so a pending reader gets exactly
        // the participant rule and nothing slips through by default.
        Assert.All(
            root.EnumerateObject(),
            member => Assert.Contains(member.Name, TabEventRedactor.ClassifiedMembers(TabEventType.ParticipantJoined)));

        // The host lets her on, and the stream reads on from where the join left it.
        await using (var approveDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(approveDb, clock, Anonymous)
                .ApproveParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, anna.Tab.Me.ParticipantId);
        }

        Assert.Equal(
            [(1L, TabEventType.ParticipantJoined), (2L, TabEventType.ParticipantApproved)],
            (await StreamAsync(clock, host.Tab.TabId)).Select(e => (e.Sequence, e.Type)));
    }

    /// <summary>
    /// The invitation the host passed round is the other way onto a tab, and it lands pending too.
    /// </summary>
    [SkippableFact]
    public async Task A_join_through_the_hosts_invitation_is_announced_like_a_scan()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        var hostDinerId = Guid.CreateVersion7();
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");

        TabJoinTokenResult invitation;

        await using (var inviteDb = fixture.CreateContext(clock))
        {
            invitation = await fixture.CreateTabService(inviteDb, clock, Anonymous)
                .CreateJoinTokenAsync(host.Tab.TabId, host.Tab.Me.ParticipantId);
        }

        TabAccessResult late;

        await using (var joinDb = fixture.CreateContext(clock))
        {
            late = await fixture.CreateTabService(joinDb, clock, Anonymous)
                .JoinAsync(new JoinTabCommand(invitation.Token, "phone-late", "Lusine"));
        }

        Assert.Equal(ParticipantStatus.PendingApproval, late.Tab.Me.Status);

        var joined = Assert.Single(await StreamAsync(clock, host.Tab.TabId));
        Assert.Equal(TabEventType.ParticipantJoined, joined.Type);

        var push = await JoinPushAsync(clock, late.Tab.Me.ParticipantId);
        Assert.Equal(hostDinerId, push.GetProperty("dinerUserId").GetGuid());
        Assert.Equal("Lusine", push.GetProperty("guestName").GetString());
    }

    // ------------------------------------------------------------ the push

    /// <summary>
    /// A host who scanned with an account is sent one push per person waiting, keyed by that
    /// person's join, carrying what the lock screen says and where a tap goes.
    /// </summary>
    [SkippableFact]
    public async Task A_host_with_an_account_is_sent_one_push_per_join_and_never_a_second()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, branch, qr) = await ArrangeAsync();
        var hostDinerId = Guid.CreateVersion7();
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");

        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");

        // Somebody who did not give a name: the push says "a guest", not the "Guest 3" put on the
        // roster in their place.
        var nameless = await OpenAsync(clock, Anonymous, qr, "phone-nameless", displayName: null);

        await using var verify = fixture.CreateContext(clock);
        var tabId = host.Tab.TabId.ToString();

        var pushes = await verify.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.JoinRequested && m.PayloadJson.Contains(tabId))
            .ToListAsync();

        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, m => Assert.Equal(Now, m.ScheduledForUtc));

        Assert.Equal(
            new[]
            {
                OutboxMessageTypes.KeyFor("participant", anna.Tab.Me.ParticipantId, "join-requested"),
                OutboxMessageTypes.KeyFor("participant", nameless.Tab.Me.ParticipantId, "join-requested"),
            }.Order(StringComparer.Ordinal),
            pushes.Select(m => m.IdempotencyKey).Order(StringComparer.Ordinal));

        var tableLabel = await verify.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.FirstTableId).Select(t => t.Label).SingleAsync();

        var forAnna = await JoinPushAsync(clock, anna.Tab.Me.ParticipantId);

        Assert.Equal(host.Tab.TabId, forAnna.GetProperty("tabId").GetGuid());
        Assert.Equal(anna.Tab.Me.ParticipantId, forAnna.GetProperty("participantId").GetGuid());
        Assert.Equal(hostDinerId, forAnna.GetProperty("dinerUserId").GetGuid());
        Assert.Equal(tableLabel, forAnna.GetProperty("tableLabel").GetString());
        Assert.Equal("Anna", forAnna.GetProperty("guestName").GetString());

        var forNameless = await JoinPushAsync(clock, nameless.Tab.Me.ParticipantId);
        Assert.Equal(JsonValueKind.Null, forNameless.GetProperty("guestName").ValueKind);
    }

    /// <summary>
    /// Most hosts scanned without an account, and a push has no other address. The roster still
    /// moves on the stream.
    /// </summary>
    [SkippableFact]
    public async Task A_host_without_an_account_is_sent_nothing_and_the_stream_still_says_so()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        var host = await OpenAsync(clock, Anonymous, qr, "phone-host", "Aram");

        // A joiner with an account changes nothing: the push is the host's, and the host has none.
        var anna = await OpenAsync(clock, TestActor.Diner(), qr, "phone-anna", "Anna");

        Assert.Equal(TabEventType.ParticipantJoined, Assert.Single(await StreamAsync(clock, host.Tab.TabId)).Type);
        Assert.Empty(await JoinPushesForTabAsync(clock, host.Tab.TabId));
        Assert.Equal(ParticipantStatus.PendingApproval, anna.Tab.Me.Status);
    }

    /// <summary>
    /// The host's own account on a second phone lands pending like anybody else - the roster says
    /// so - but telling the host that they want to join their own tab is noise.
    /// </summary>
    [SkippableFact]
    public async Task The_hosts_own_account_on_another_phone_is_not_announced_to_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        var hostDinerId = Guid.CreateVersion7();
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");

        var secondPhone = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host-tablet", "Aram");

        Assert.Equal(ParticipantStatus.PendingApproval, secondPhone.Tab.Me.Status);
        Assert.Equal(TabEventType.ParticipantJoined, Assert.Single(await StreamAsync(clock, host.Tab.TabId)).Type);
        Assert.Empty(await JoinPushesForTabAsync(clock, host.Tab.TabId));
    }

    /// <summary>
    /// A tab staff opened has nobody on it, and the first phone onto it hosts it: nobody to tell and
    /// nothing to wait for. The next phone waits for that host, and that host is told.
    /// </summary>
    [SkippableFact]
    public async Task The_first_phone_onto_a_tab_staff_opened_hosts_it_unannounced_and_the_next_is_announced_to_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, branch, qr) = await ArrangeAsync();
        var tableId = branch.FirstTableId;
        var waiter = TestActor.Waiter(branch.WaiterId);

        await using (var seatDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(seatDb, clock, waiter)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));
        }

        TabStaffView staffTab;

        await using (var openDb = fixture.CreateContext(clock))
        {
            staffTab = await fixture.CreateTabService(openDb, clock, waiter)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()));
        }

        var hostDinerId = Guid.CreateVersion7();
        var first = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-ani", "Ani");

        Assert.Equal(staffTab.TabId, first.Tab.TabId);
        Assert.Equal(ParticipantRole.Host, first.Tab.Me.Role);
        Assert.Empty(await StreamAsync(clock, staffTab.TabId));
        Assert.Empty(await JoinPushesForTabAsync(clock, staffTab.TabId));

        var gor = await OpenAsync(clock, Anonymous, qr, "phone-gor", "Gor");

        Assert.Equal(ParticipantStatus.PendingApproval, gor.Tab.Me.Status);
        Assert.Equal(TabEventType.ParticipantJoined, Assert.Single(await StreamAsync(clock, staffTab.TabId)).Type);
        Assert.Equal(hostDinerId, (await JoinPushAsync(clock, gor.Tab.Me.ParticipantId)).GetProperty("dinerUserId").GetGuid());
    }

    // ------------------------------------------------------------ what the host reads

    [Theory]
    [InlineData("en", "Anna", "Table 4", "Anna wants to join your tab.")]
    [InlineData("en", null, "Table 4", "A guest wants to join your tab.")]
    [InlineData("ru-RU", "Анна", "Столик 4", "Анна хочет присоединиться к вашему счёту.")]
    [InlineData("ru", "  ", "Столик 4", "Гость хочет присоединиться к вашему счёту.")]
    [InlineData("hy", "Աննա", "Սեղան 4", "Աննա՝ ցանկանում է միանալ Ձեր հաշվին։")]
    [InlineData("hy-AM", "Գոռ", "Սեղան 4", "Գոռ՝ ցանկանում է միանալ Ձեր հաշվին։")]
    [InlineData("hy", "Anna", "Սեղան 4", "Anna՝ ցանկանում է միանալ Ձեր հաշվին։")]
    [InlineData("hy", null, "Սեղան 4", "Մի հյուր ցանկանում է միանալ Ձեր հաշվին։")]
    [InlineData("de", "Anna", "Սեղան 4", "Anna՝ ցանկանում է միանալ Ձեր հաշվին։")]
    public void The_join_request_names_the_table_and_the_joiner_in_each_language(
        string locale,
        string? guestName,
        string title,
        string body)
    {
        Assert.Equal((title, body), NotificationText.JoinRequested(locale, "4", guestName));
    }

    /// <summary>
    /// Through the dispatcher, in the language of the host's own phone, and carrying what a tap needs
    /// to open the tab.
    /// </summary>
    [SkippableTheory]
    [InlineData("hy", "Սեղան 1", "Anna՝ ցանկանում է միանալ Ձեր հաշվին։")]
    [InlineData("ru", "Столик 1", "Anna хочет присоединиться к вашему счёту.")]
    [InlineData("en", "Table 1", "Anna wants to join your tab.")]
    public async Task The_host_is_told_in_their_own_language_and_the_tap_opens_the_tab(
        string locale,
        string title,
        string body)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        await fixture.ClearOutboxAsync(clock);

        var hostDinerId = await DinerWithPhoneAsync(clock, locale);
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");
        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");

        var channel = new RecordingChannel();
        var pass = await DispatchAsync(clock, channel);

        Assert.Equal(1, pass.Sent);

        var message = Assert.Single(channel.OfKind("tabJoinRequested"));

        Assert.Equal(hostDinerId, message.DinerUserId);
        Assert.Equal(title, message.Title);
        Assert.Equal(body, message.Body);
        Assert.Equal(["kind", "participantId", "tabId"], message.Data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(host.Tab.TabId.ToString(), message.Data["tabId"]);
        Assert.Equal(anna.Tab.Me.ParticipantId.ToString(), message.Data["participantId"]);
    }

    /// <summary>
    /// The host let her on from the People screen before the push went out. Telling them she wants
    /// to join now would be wrong, and the message is done with rather than retried.
    /// </summary>
    [SkippableFact]
    public async Task A_join_the_host_already_answered_is_not_pushed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        await fixture.ClearOutboxAsync(clock);

        var hostDinerId = await DinerWithPhoneAsync(clock, "en");
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");
        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        var gor = await OpenAsync(clock, Anonymous, qr, "phone-gor", "Gor");

        await using (var answerDb = fixture.CreateContext(clock))
        {
            var service = fixture.CreateTabService(answerDb, clock, Anonymous);

            await service.ApproveParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, anna.Tab.Me.ParticipantId);
            await service.RejectParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, gor.Tab.Me.ParticipantId);
        }

        var channel = new RecordingChannel();
        var pass = await DispatchAsync(clock, channel);

        Assert.Equal(2, pass.Sent);
        Assert.Empty(channel.OfKind("tabJoinRequested"));

        await using var verify = fixture.CreateContext(clock);

        var messages = await verify.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.JoinRequested)
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m =>
        {
            Assert.True(m.IsSent);
            Assert.Null(m.LastError);
        });
    }

    /// <summary>
    /// Staff marked the tab closing before the push went out. Closing leaves a pending joiner pending,
    /// but nobody new is let on a tab being settled, so asking the host to is a question they cannot
    /// say yes to.
    /// </summary>
    [SkippableFact]
    public async Task A_join_on_a_tab_staff_have_since_marked_closing_is_not_pushed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, branch, qr) = await ArrangeAsync();
        await fixture.ClearOutboxAsync(clock);

        var hostDinerId = await DinerWithPhoneAsync(clock, "en");
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");
        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");

        await using (var closeDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(closeDb, clock, TestActor.Waiter(branch.WaiterId))
                .BeginClosingAsync(host.Tab.TabId);
        }

        var channel = new RecordingChannel();
        var pass = await DispatchAsync(clock, channel);

        Assert.Equal(1, pass.Sent);
        Assert.Empty(channel.OfKind("tabJoinRequested"));
        Assert.True(await JoinPushSentAsync(clock, anna.Tab.Me.ParticipantId));
    }

    /// <summary>
    /// Staff moved the host role before the push went out. The message was addressed to the host as
    /// they were when Anna scanned; that person can no longer let her on, and the new host sees her
    /// on the bill.
    /// </summary>
    [SkippableFact]
    public async Task A_join_whose_host_has_since_been_replaced_is_not_pushed_to_the_old_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, branch, qr) = await ArrangeAsync();
        await fixture.ClearOutboxAsync(clock);

        var hostDinerId = await DinerWithPhoneAsync(clock, "en");
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");
        var nare = await OpenAsync(clock, Anonymous, qr, "phone-nare", "Nare");

        await using (var approveDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(approveDb, clock, Anonymous)
                .ApproveParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, nare.Tab.Me.ParticipantId);
        }

        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");

        await using (var reassignDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(reassignDb, clock, TestActor.Waiter(branch.WaiterId))
                .ReassignHostAsync(host.Tab.TabId, nare.Tab.Me.ParticipantId);
        }

        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        Assert.Empty(channel.OfKind("tabJoinRequested"));
        Assert.True(await JoinPushSentAsync(clock, anna.Tab.Me.ParticipantId));
    }

    /// <summary>
    /// The host turned a phone away, and it scanned again. It lands pending like anybody else and the
    /// stream says so - the host sees it on the bill and can still let it on - but the host is not
    /// woken again for somebody they already said no to. Recognised by the phone and by the account.
    /// </summary>
    [SkippableFact]
    public async Task Somebody_the_host_turned_away_who_scans_again_is_shown_but_not_pushed_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, qr) = await ArrangeAsync();
        var hostDinerId = Guid.CreateVersion7();
        var host = await OpenAsync(clock, TestActor.Diner(hostDinerId), qr, "phone-host", "Aram");

        var gorsAccount = Guid.CreateVersion7();
        var anna = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        var gor = await OpenAsync(clock, TestActor.Diner(gorsAccount), qr, "phone-gor", "Gor");

        await using (var rejectDb = fixture.CreateContext(clock))
        {
            var service = fixture.CreateTabService(rejectDb, clock, Anonymous);

            await service.RejectParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, anna.Tab.Me.ParticipantId);
            await service.RejectParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, gor.Tab.Me.ParticipantId);
        }

        // Anna on the same phone; Gor on another phone, signed in to the same account.
        var annaAgain = await OpenAsync(clock, Anonymous, qr, "phone-anna", "Anna");
        var gorAgain = await OpenAsync(clock, TestActor.Diner(gorsAccount), qr, "phone-gor-new", "Gor");

        Assert.NotEqual(anna.Tab.Me.ParticipantId, annaAgain.Tab.Me.ParticipantId);
        Assert.Equal(ParticipantStatus.PendingApproval, annaAgain.Tab.Me.Status);
        Assert.Equal(ParticipantStatus.PendingApproval, gorAgain.Tab.Me.Status);

        Assert.Equal(
            [
                TabEventType.ParticipantJoined, TabEventType.ParticipantJoined,
                TabEventType.ParticipantRejected, TabEventType.ParticipantRejected,
                TabEventType.ParticipantJoined, TabEventType.ParticipantJoined,
            ],
            (await StreamAsync(clock, host.Tab.TabId)).Select(e => e.Type));

        // One push each, for the first time they asked.
        Assert.Equal(
            new[]
            {
                OutboxMessageTypes.KeyFor("participant", anna.Tab.Me.ParticipantId, "join-requested"),
                OutboxMessageTypes.KeyFor("participant", gor.Tab.Me.ParticipantId, "join-requested"),
            }.Order(StringComparer.Ordinal),
            (await JoinPushesForTabAsync(clock, host.Tab.TabId)).Order(StringComparer.Ordinal));

        // Somebody who was let on and left is not somebody the host turned away: asking to come back
        // is a new question.
        var lusine = await OpenAsync(clock, Anonymous, qr, "phone-lusine", "Lusine");

        await using (var leaveDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(leaveDb, clock, Anonymous)
                .ApproveParticipantAsync(host.Tab.TabId, host.Tab.Me.ParticipantId, lusine.Tab.Me.ParticipantId);
        }

        await using (var leaveDb = fixture.CreateContext(clock))
        {
            await fixture.CreateTabService(leaveDb, clock, TestActor.Participant(lusine.Tab.Me.ParticipantId))
                .LeaveAsync(host.Tab.TabId, lusine.Tab.Me.ParticipantId);
        }

        var lusineAgain = await OpenAsync(clock, Anonymous, qr, "phone-lusine", "Lusine");

        Assert.Equal(hostDinerId, (await JoinPushAsync(clock, lusineAgain.Tab.Me.ParticipantId)).GetProperty("dinerUserId").GetGuid());
    }

    // ------------------------------------------------------------ helpers

    private sealed record StoredEvent(long Sequence, TabEventType Type, string PayloadJson);

    private async Task<(TestClock Clock, TestBranch Branch, string QrToken)> ArrangeAsync()
    {
        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var qr = await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        return (clock, branch, qr);
    }

    /// <summary>One scan, as one request: its own context, as production gives every request.</summary>
    private async Task<TabAccessResult> OpenAsync(
        TestClock clock,
        TestActor actor,
        string qrToken,
        string deviceId,
        string? displayName)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateTabService(db, clock, actor)
            .OpenAsync(new OpenTabCommand(qrToken, deviceId, Guid.CreateVersion7(), displayName));
    }

    private async Task<IReadOnlyList<StoredEvent>> StreamAsync(TestClock clock, Guid tabId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TabEvents.AsNoTracking()
            .Where(e => e.TabId == tabId)
            .OrderBy(e => e.Sequence)
            .Select(e => new StoredEvent(e.Sequence, e.Type, e.PayloadJson))
            .ToListAsync();
    }

    /// <summary>The one join push written for this participant's join, as its payload.</summary>
    private async Task<JsonElement> JoinPushAsync(TestClock clock, Guid participantId)
    {
        await using var db = fixture.CreateContext(clock);
        var key = OutboxMessageTypes.KeyFor("participant", participantId, "join-requested");

        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == key);

        Assert.Equal(OutboxMessageTypes.JoinRequested, message.Type);

        using var payload = JsonDocument.Parse(message.PayloadJson);

        return payload.RootElement.Clone();
    }

    /// <summary>A diner account with one live phone set to <paramref name="locale"/>.</summary>
    private async Task<Guid> DinerWithPhoneAsync(TestClock clock, string locale)
    {
        await using var db = fixture.CreateContext(clock);

        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", locale);
        diner.ProveNumber(clock.UtcNow);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(
            diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Ios, locale, clock.UtcNow));

        await db.SaveChangesAsync();

        return diner.Id;
    }

    /// <summary>One pass of the dispatcher with the join request's handler, as the hosted loop runs it.</summary>
    private async Task<OutboxDispatcher.DispatchPass> DispatchAsync(TestClock clock, INotificationChannel channel)
    {
        await using var db = fixture.CreateContext(clock);

        return await new OutboxDispatcher(
                db,
                clock,
                new OutboxOptions(),
                [new JoinRequestedHandler(db, channel, NullLogger<JoinRequestedHandler>.Instance)],
                NullLogger<OutboxDispatcher>.Instance)
            .RunOnceAsync();
    }

    /// <summary>Whether this participant's join push has been dealt with - sent, or skipped as done.</summary>
    private async Task<bool> JoinPushSentAsync(TestClock clock, Guid participantId)
    {
        await using var db = fixture.CreateContext(clock);
        var key = OutboxMessageTypes.KeyFor("participant", participantId, "join-requested");

        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == key);

        return message.IsSent && message.LastError is null;
    }

    private async Task<IReadOnlyList<string>> JoinPushesForTabAsync(TestClock clock, Guid tabId)
    {
        await using var db = fixture.CreateContext(clock);
        var id = tabId.ToString();

        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.JoinRequested && m.PayloadJson.Contains(id))
            .Select(m => m.IdempotencyKey)
            .ToListAsync();
    }
}
