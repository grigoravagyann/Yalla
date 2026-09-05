using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Menus;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Opening, joining and the permission model against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// A real database, because the guarantees under test are the database's: the filtered unique
/// index on open sessions per table and the unique index on tab per session are what make two
/// simultaneous scans converge on one tab, and the in-memory provider enforces neither.
/// </para>
/// <para>
/// Each "request" that must not see stale state gets its own context, as it would in production
/// where every HTTP request has a fresh scoped one. A tracked <c>Tab</c> in one context does not
/// learn that staff closed it through another.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabServiceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A phone with no account behind it. The common case, and the one the product lives on.</summary>
    private static TestActor Anonymous => new(ActorType.Diner, null, null, null);

    // ------------------------------------------------------------ 1. free table

    [SkippableFact]
    public async Task Scanning_a_free_table_opens_a_session_and_a_tab_and_makes_the_scanner_host()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);
        var commandId = Guid.CreateVersion7();

        var result = await service.OpenAsync(new OpenTabCommand(qr, "phone-a", commandId, "Aram"));

        Assert.Equal(TabOpenOutcome.OpenedNewSession, result.Outcome);
        Assert.False(result.WasReplay);
        Assert.Equal(ParticipantRole.Host, result.Tab.Me.Role);
        Assert.Equal(ParticipantStatus.Approved, result.Tab.Me.Status);
        Assert.Equal(result.Tab.Me.ParticipantId, result.Tab.HostParticipantId);
        Assert.True(result.Tab.Me.CanOrderNow);
        Assert.True(result.Tab.Me.CanPay);
        Assert.True(result.Tab.TableTotalVisible);
        Assert.Equal("Aram", result.Tab.Me.DisplayName);
        Assert.NotNull(result.Token);

        await using var verify = fixture.CreateContext(clock);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == branch.FirstTableId);
        Assert.Equal(TableStatus.Occupied, table.Status);

        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == branch.FirstTableId);
        Assert.Equal(TableSessionSource.WalkIn, session.Source);
        Assert.Null(session.ClosedAtUtc);
        Assert.Equal(result.Tab.TabId, session.TabId);

        var tab = await verify.Tabs.AsNoTracking().SingleAsync(t => t.DiningTableId == branch.FirstTableId);
        Assert.Equal(session.Id, tab.TableSessionId);
        Assert.Equal(TabStatus.Open, tab.Status);
        Assert.Equal(commandId, tab.ClientCommandId);

        // The audit row, written by the state machine and carrying the scan's own command id.
        var audit = Assert.Single(
            await verify.TableStateChanges.AsNoTracking().Where(c => c.DiningTableId == branch.FirstTableId).ToListAsync());
        Assert.Equal(TableStatus.Free, audit.FromStatus);
        Assert.Equal(TableStatus.Occupied, audit.ToStatus);
        Assert.Equal(commandId, audit.ClientCommandId);
        Assert.Equal(session.Id, audit.TableSessionId);
    }

    // ------------------------------------------------------------ 2. table already has a tab

    [SkippableFact]
    public async Task Scanning_a_table_with_an_open_tab_joins_it_pending_rather_than_opening_a_second()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var first = await service.OpenAsync(new OpenTabCommand(qr, "phone-a", Guid.CreateVersion7(), "Aram"));

        await using var secondDb = fixture.CreateContext(clock);
        var second = await fixture.CreateTabService(secondDb, clock, Anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-b", Guid.CreateVersion7(), "Nare"));

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, second.Outcome);
        Assert.Equal(first.Tab.TabId, second.Tab.TabId);
        Assert.Equal(ParticipantRole.Guest, second.Tab.Me.Role);
        Assert.Equal(ParticipantStatus.PendingApproval, second.Tab.Me.Status);
        Assert.False(second.Tab.Me.CanOrderNow);
        Assert.False(second.Tab.Me.CanPay);

        // Pending: their own row and nothing else.
        var onlyThem = Assert.Single(second.Tab.Participants);
        Assert.Equal(second.Tab.Me.ParticipantId, onlyThem.ParticipantId);
        Assert.False(second.Tab.TableTotalVisible);
        Assert.Null(second.Tab.TableTotal);

        await using var verify = fixture.CreateContext(clock);
        var tableId = branch.FirstTableId;

        Assert.Equal(1, await verify.Tabs.CountAsync(t => t.DiningTableId == tableId));
        Assert.Equal(1, await verify.TableSessions.CountAsync(s => s.DiningTableId == tableId));

        var participants = await verify.TabParticipants.AsNoTracking().Where(p => p.TabId == first.Tab.TabId).ToListAsync();
        Assert.Equal(2, participants.Count);
        Assert.Single(participants, p => p.Role == ParticipantRole.Host);
        Assert.Single(participants, p => p.Status == ParticipantStatus.PendingApproval);

        // The table was already occupied, so no second state change and no second audit row.
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.DiningTableId == tableId));
    }

    // ------------------------------------------------------------ 3. seated party, no tab yet

    [SkippableFact]
    public async Task Scanning_a_seated_table_with_no_tab_attaches_a_tab_to_that_session()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var tableId = branch.FirstTableId;

        // A waiter sat the party down. Or they arrived on a booking - the session is the same.
        var seated = await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 3, Guid.CreateVersion7()));

        await using var scanDb = fixture.CreateContext(clock);
        var result = await fixture.CreateTabService(scanDb, clock, Anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-a", Guid.CreateVersion7()));

        Assert.Equal(TabOpenOutcome.OpenedOnExistingSession, result.Outcome);
        Assert.Equal(ParticipantRole.Host, result.Tab.Me.Role);
        Assert.Equal(ParticipantStatus.Approved, result.Tab.Me.Status);

        await using var verify = fixture.CreateContext(clock);

        var tab = await verify.Tabs.AsNoTracking().SingleAsync(t => t.DiningTableId == tableId);
        Assert.Equal(seated.TableSessionId, tab.TableSessionId);

        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId);
        Assert.Equal(tab.Id, session.TabId);
        Assert.Equal(branch.WaiterId, session.SeatedByStaffId);

        // Only the waiter's seating changed table state; the scan did not.
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.DiningTableId == tableId));
    }

    [SkippableFact]
    public async Task Scanning_a_table_that_is_out_of_service_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;

        var table = await db.DiningTables.FirstAsync(t => t.Id == branch.FirstTableId);
        table.MarkOutOfService();
        await db.SaveChangesAsync();

        await using var scanDb = fixture.CreateContext(clock);
        var service = fixture.CreateTabService(scanDb, clock, Anonymous);

        var refused = await Assert.ThrowsAsync<DomainStateException>(
            () => service.OpenAsync(new OpenTabCommand(qr, "phone-a", Guid.CreateVersion7())));

        Assert.Contains("out of service", refused.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.Tabs.CountAsync(t => t.DiningTableId == branch.FirstTableId));
    }

    // ------------------------------------------------------------ 4. two phones at once

    /// <summary>
    /// Two separate contexts, two connections, two real commits racing. No mocked exception: the
    /// loser is whichever the database refuses, and the assertion is only about the outcome.
    /// </summary>
    [SkippableFact]
    public async Task Two_simultaneous_scans_of_a_free_table_produce_exactly_one_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var tableId = branch.FirstTableId;

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);
        var phoneA = fixture.CreateTabService(dbA, clock, Anonymous);
        var phoneB = fixture.CreateTabService(dbB, clock, Anonymous);

        var results = await Task.WhenAll(
            phoneA.OpenAsync(new OpenTabCommand(qr, "phone-a", Guid.CreateVersion7(), "Aram")),
            phoneB.OpenAsync(new OpenTabCommand(qr, "phone-b", Guid.CreateVersion7(), "Nare")));

        // Both landed on the same tab: one opened it, the other is pending on it.
        Assert.Equal(results[0].Tab.TabId, results[1].Tab.TabId);

        var loser = Assert.Single(results, r => r.Outcome == TabOpenOutcome.JoinedExistingTab);
        var winner = Assert.Single(results, r => r.Outcome != TabOpenOutcome.JoinedExistingTab);

        Assert.Equal(ParticipantRole.Host, winner.Tab.Me.Role);
        Assert.Equal(ParticipantStatus.Approved, winner.Tab.Me.Status);
        Assert.Equal(ParticipantRole.Guest, loser.Tab.Me.Role);
        Assert.Equal(ParticipantStatus.PendingApproval, loser.Tab.Me.Status);
        Assert.NotEqual(winner.Tab.Me.ParticipantId, loser.Tab.Me.ParticipantId);

        await using var verify = fixture.CreateContext(clock);

        Assert.Equal(1, await verify.Tabs.CountAsync(t => t.DiningTableId == tableId));
        Assert.Equal(1, await verify.TableSessions.CountAsync(s => s.DiningTableId == tableId));
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.DiningTableId == tableId));

        var participants = await verify.TabParticipants.AsNoTracking()
            .Where(p => p.TabId == results[0].Tab.TabId).ToListAsync();

        Assert.Equal(2, participants.Count);
        Assert.Single(participants, p => p.Role == ParticipantRole.Host && p.Status == ParticipantStatus.Approved);
        Assert.Single(participants, p => p.Role == ParticipantRole.Guest && p.Status == ParticipantStatus.PendingApproval);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);
        Assert.Equal(TableStatus.Occupied, table.Status);
    }

    // ------------------------------------------------------------ 5. idempotency

    [SkippableFact]
    public async Task Replaying_the_same_clientCommandId_returns_the_same_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var commandId = Guid.CreateVersion7();

        var first = await fixture.CreateTabService(db, clock, Anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-a", commandId, "Aram"));

        await using var retryDb = fixture.CreateContext(clock);
        var replay = await fixture.CreateTabService(retryDb, clock, Anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-a", commandId, "Aram"));

        Assert.True(replay.WasReplay);
        Assert.Equal(first.Tab.TabId, replay.Tab.TabId);
        Assert.Equal(first.Tab.Me.ParticipantId, replay.Tab.Me.ParticipantId);
        Assert.Equal(ParticipantRole.Host, replay.Tab.Me.Role);
        Assert.Equal(TabOpenOutcome.OpenedNewSession, replay.Outcome);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.Tabs.CountAsync(t => t.DiningTableId == branch.FirstTableId));
        Assert.Equal(1, await verify.TabParticipants.CountAsync(p => p.TabId == first.Tab.TabId));
    }

    // ------------------------------------------------------------ 6. join tokens

    [SkippableFact]
    public async Task An_expired_join_token_is_rejected_and_a_refreshed_one_works()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, _, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var host = fixture.CreateTabService(db, clock, Anonymous);

        var opened = await host.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));
        var tabId = opened.Tab.TabId;
        var hostId = opened.Tab.Me.ParticipantId;

        var invitation = await host.CreateJoinTokenAsync(tabId, hostId);

        Assert.Equal(clock.UtcNow.AddMinutes(30), invitation.ExpiresAtUtc);
        Assert.Equal(tabId, invitation.TabId);

        // One token, two ways to hand it over: the share link carries the very same value.
        Assert.Contains(invitation.Token, invitation.ShareUrl, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(31));

        await using (var lateDb = fixture.CreateContext(clock))
        {
            var late = fixture.CreateTabService(lateDb, clock, Anonymous);

            await Assert.ThrowsAsync<AuthenticationFailedException>(
                () => late.JoinAsync(new JoinTabCommand(invitation.Token, "phone-late", "Nare")));
        }

        // The host refreshes. The old value is gone for good; the new one lets the friend in.
        var refreshed = await host.CreateJoinTokenAsync(tabId, hostId);
        Assert.NotEqual(invitation.Token, refreshed.Token);
        Assert.Equal(clock.UtcNow.AddMinutes(30), refreshed.ExpiresAtUtc);

        await using var joinDb = fixture.CreateContext(clock);
        var joined = await fixture.CreateTabService(joinDb, clock, Anonymous)
            .JoinAsync(new JoinTabCommand(refreshed.Token, "phone-late", "Nare"));

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, joined.Outcome);
        Assert.Equal(tabId, joined.Tab.TabId);
        Assert.Equal(ParticipantStatus.PendingApproval, joined.Tab.Me.Status);
        Assert.Equal("Nare", joined.Tab.Me.DisplayName);
    }

    [SkippableFact]
    public async Task Only_the_host_can_invite()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, _, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var opened = await service.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7()));
        var guest = await service.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7()));

        var refused = await Assert.ThrowsAsync<TabPermissionException>(
            () => service.CreateJoinTokenAsync(opened.Tab.TabId, guest.Tab.Me.ParticipantId));

        Assert.Equal("the host of this tab", refused.Requirement);
    }

    // ------------------------------------------------------------ 7. pending sees nothing

    [SkippableFact]
    public async Task A_pending_participant_cannot_order_see_the_total_or_see_anyone_elses_items()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var opened = await service.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));
        var pending = await service.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7(), "Nare"));
        var tabId = opened.Tab.TabId;
        var hostId = opened.Tab.Me.ParticipantId;
        var pendingId = pending.Tab.Me.ParticipantId;

        Assert.Equal(ParticipantStatus.PendingApproval, pending.Tab.Me.Status);

        // The host has ordered a coffee and the tab has a real, non-zero total.
        await TabTestData.AddOrderLineAsync(fixture, clock, branch, tabId, hostId, "Coffee", 2_500L);
        await TabTestData.SetTotalsAsync(fixture, clock, tabId, subtotalAmd: 2_500L, serviceChargeAmd: 250L);

        await using var verify = fixture.CreateContext(clock);
        var query = fixture.CreateTabQuery(verify);

        var view = (await query.GetForParticipantAsync(tabId, pendingId))!;

        // Cannot order.
        Assert.False(view.Me.CanOrderNow);

        // Cannot see the table total - absent, not zero.
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.TableTotal);

        // Cannot see the host's coffee.
        Assert.Null(view.TableLines);
        Assert.Empty(view.MyLines);
        Assert.Equal(0L, view.MyItemsSubtotalAmd);

        // Cannot see who else is here.
        var onlyThem = Assert.Single(view.Participants);
        Assert.Equal(pendingId, onlyThem.ParticipantId);

        // The rule the ordering endpoints will enforce says the same thing, from the same source.
        var access = (await new AuthorizationQueries(verify).GetTabParticipantAccessAsync(tabId, pendingId))!;
        Assert.False(TabPermissions.MayOrder(access.ParticipantStatus, access.CanOrder, access.TabStatus));
        Assert.False(TabPermissions.MaySeeTableTotal(access.ParticipantStatus, access.CanSeeTableTotal));

        // Whereas the host sees everything, so the gaps above are the flags and not missing data.
        var hostView = (await query.GetForParticipantAsync(tabId, hostId))!;
        Assert.True(hostView.TableTotalVisible);
        Assert.Equal(2_750L, hostView.TableTotal!.TotalAmd);
        Assert.Single(hostView.TableLines!);
        Assert.Equal(2, hostView.Participants.Count);
    }

    // ------------------------------------------------------------ 8. CanPay implies CanSeeTableTotal

    [SkippableFact]
    public async Task Setting_CanPay_true_while_CanSeeTableTotal_is_false_is_rejected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, _, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var (tabId, hostId, guestId) = await TabTestData.OpenWithApprovedGuestAsync(service, qr);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetPermissionsAsync(
                tabId, hostId, guestId, new SetParticipantPermissionsCommand(CanOrder: true, CanSeeTableTotal: false, CanPay: true)));

        Assert.Contains("see the table total", refused.Message, StringComparison.OrdinalIgnoreCase);

        // Refused, not corrected: the row is exactly as it was.
        await using (var verify = fixture.CreateContext(clock))
        {
            var guest = await verify.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == guestId);
            Assert.False(guest.CanPay);
            Assert.True(guest.CanSeeTableTotal);
        }

        // The consistent combination is accepted.
        var granted = await service.SetPermissionsAsync(
            tabId, hostId, guestId, new SetParticipantPermissionsCommand(CanOrder: true, CanSeeTableTotal: true, CanPay: true));

        Assert.True(granted.CanPay);
        Assert.True(granted.CanSeeTableTotal);

        // And a guest cannot set anyone's flags, including the host's.
        await Assert.ThrowsAsync<TabPermissionException>(
            () => service.SetPermissionsAsync(
                tabId, guestId, hostId, new SetParticipantPermissionsCommand(CanOrder: false, CanSeeTableTotal: true, CanPay: true)));
    }

    // ------------------------------------------------------------ 9. own items, no aggregate

    [SkippableFact]
    public async Task A_participant_without_CanSeeTableTotal_gets_their_own_items_and_no_aggregate_at_all()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        // The host is treating and does not want the table reading the total.
        var (tabId, hostId, guestId) = await TabTestData.OpenWithApprovedGuestAsync(service, qr, hideTotalFromGuests: true);

        await TabTestData.AddOrderLineAsync(fixture, clock, branch, tabId, hostId, "Coffee", 2_500L);
        await TabTestData.AddOrderLineAsync(fixture, clock, branch, tabId, guestId, "Tea", 2_400L);
        await TabTestData.SetTotalsAsync(fixture, clock, tabId, subtotalAmd: 4_900L, serviceChargeAmd: 490L);

        await using var verify = fixture.CreateContext(clock);
        var query = fixture.CreateTabQuery(verify);

        var guestView = (await query.GetForParticipantAsync(tabId, guestId))!;

        Assert.Equal(ParticipantStatus.Approved, guestView.Me.Status);
        Assert.False(guestView.Me.CanSeeTableTotal);
        Assert.True(guestView.Me.CanOrderNow);

        // Their own items, always.
        var mine = Assert.Single(guestView.MyLines);
        Assert.Equal("Tea", mine.Name);
        Assert.Equal(2_400L, mine.LineTotalAmd);
        Assert.Equal(2_400L, guestView.MyItemsSubtotalAmd);

        // No table aggregate at all. The tab's total is 5,390 - and none of it is here, not even as zero.
        Assert.False(guestView.TableTotalVisible);
        Assert.Null(guestView.TableTotal);
        Assert.Null(guestView.TableLines);

        // Approved, so they may see who is at the table - just not what the others owe.
        Assert.Equal(2, guestView.Participants.Count);

        var hostView = (await query.GetForParticipantAsync(tabId, hostId))!;
        Assert.True(hostView.TableTotalVisible);
        Assert.Equal(5_390L, hostView.TableTotal!.TotalAmd);
        Assert.Equal(2, hostView.TableLines!.Count);
    }

    // ------------------------------------------------------------ 10. removal keeps the records

    [SkippableFact]
    public async Task A_removed_participants_items_and_payments_still_exist()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var (tabId, hostId, guestId) = await TabTestData.OpenWithApprovedGuestAsync(service, qr);

        var lineId = await TabTestData.AddOrderLineAsync(fixture, clock, branch, tabId, guestId, "Tea", 2_400L);

        Guid paymentId;
        await using (var pay = fixture.CreateContext(clock))
        {
            var reserved = Payment.Reserve(tabId, 1_000L, PaymentMethod.Cash, clock.UtcNow, guestId);
            pay.Payments.Add(reserved);
            await pay.SaveChangesAsync();
            paymentId = reserved.Id;
        }

        var removed = await service.RemoveParticipantAsync(tabId, hostId, guestId);
        Assert.Equal(ParticipantStatus.Removed, removed.Status);

        await using var verify = fixture.CreateContext(clock);

        // A status change, never a delete.
        var row = await verify.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == guestId);
        Assert.Equal(ParticipantStatus.Removed, row.Status);
        Assert.Equal(clock.UtcNow, row.RemovedAtUtc);

        var line = await verify.TabOrderLines.AsNoTracking().Include(l => l.TabOrder).FirstAsync(l => l.Id == lineId);
        Assert.Equal(guestId, line.TabOrder.PlacedByParticipantId);

        var payment = await verify.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId);
        Assert.Equal(guestId, payment.TabParticipantId);
        Assert.Equal(1_000L, payment.AmountAmd);

        // Out of the roster, though, and no longer able to read the tab.
        var hostView = (await fixture.CreateTabQuery(verify).GetForParticipantAsync(tabId, hostId))!;
        Assert.DoesNotContain(hostView.Participants, p => p.ParticipantId == guestId);
        Assert.False(TabPermissions.MayReadTab(row.Status));

        // And the host cannot be removed from their own tab.
        await Assert.ThrowsAsync<DomainStateException>(() => service.RemoveParticipantAsync(tabId, hostId, hostId));
    }

    // ------------------------------------------------------------ 11. reassigning the host

    [SkippableFact]
    public async Task Staff_can_reassign_the_host_and_a_participant_cannot()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var diner = fixture.CreateTabService(db, clock, Anonymous);

        var (tabId, hostId, guestId) = await TabTestData.OpenWithApprovedGuestAsync(diner, qr);

        // A participant - even the host - cannot.
        await Assert.ThrowsAsync<StaffPermissionException>(() => diner.ReassignHostAsync(tabId, guestId));

        await using var staffDb = fixture.CreateContext(clock);
        var waiter = fixture.CreateTabService(staffDb, clock, TestActor.Waiter(branch.WaiterId));

        var staffView = await waiter.ReassignHostAsync(tabId, guestId);

        Assert.Equal(guestId, staffView.HostParticipantId);

        // Staff see everyone on the table.
        Assert.Equal(2, staffView.Participants.Count);
        Assert.Single(staffView.Participants, p => p.ParticipantId == guestId && p.Role == ParticipantRole.Host);
        Assert.Single(staffView.Participants, p => p.ParticipantId == hostId && p.Role == ParticipantRole.Guest);

        await using var verify = fixture.CreateContext(clock);

        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);
        Assert.Equal(guestId, tab.HostParticipantId);

        var newHost = await verify.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == guestId);
        Assert.Equal(ParticipantRole.Host, newHost.Role);
        Assert.True(newHost.CanPay);
        Assert.True(newHost.CanSeeTableTotal);

        var oldHost = await verify.TabParticipants.AsNoTracking().FirstAsync(p => p.Id == hostId);
        Assert.Equal(ParticipantRole.Guest, oldHost.Role);
        Assert.Equal(ParticipantStatus.Approved, oldHost.Status);

        // A pending participant cannot take the role.
        await using var laterDb = fixture.CreateContext(clock);
        var pending = await fixture.CreateTabService(laterDb, clock, Anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-late", Guid.CreateVersion7()));

        await using var staffDb2 = fixture.CreateContext(clock);
        await Assert.ThrowsAsync<DomainStateException>(
            () => fixture.CreateTabService(staffDb2, clock, TestActor.Waiter(branch.WaiterId))
                .ReassignHostAsync(tabId, pending.Tab.Me.ParticipantId));
    }

    // ------------------------------------------------------------ 12. settlement mode lock

    [SkippableFact]
    public async Task Settlement_mode_is_changeable_before_the_first_payment_and_locked_after()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, _, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var opened = await service.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7()));
        var tabId = opened.Tab.TabId;
        var hostId = opened.Tab.Me.ParticipantId;

        Assert.Equal(SettlementMode.AnyonePaysAnyAmount, opened.Tab.SettlementMode);

        var changed = await service.SetSettlementModeAsync(tabId, hostId, SettlementMode.EveryonePaysOwnItems);
        Assert.Equal(SettlementMode.EveryonePaysOwnItems, changed.SettlementMode);
        Assert.False(changed.SettlementModeLocked);

        // A payment lands. Reserved is enough: the hold was computed under the current split.
        await using (var pay = fixture.CreateContext(clock))
        {
            pay.Payments.Add(Payment.Reserve(tabId, 1_000L, PaymentMethod.Idram, clock.UtcNow, hostId));
            await pay.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        var lockedAt = clock.UtcNow;

        await using var afterDb = fixture.CreateContext(clock);
        var after = fixture.CreateTabService(afterDb, clock, Anonymous);

        await Assert.ThrowsAsync<DomainStateException>(
            () => after.SetSettlementModeAsync(tabId, hostId, SettlementMode.HostPaysEverything));

        await using var verify = fixture.CreateContext(clock);
        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);

        Assert.Equal(SettlementMode.EveryonePaysOwnItems, tab.SettlementMode);
        Assert.Equal(lockedAt, tab.SettlementModeLockedAtUtc);

        // The lock is visible to the participant, so the app can stop offering the choice.
        var view = (await fixture.CreateTabQuery(verify).GetForParticipantAsync(tabId, hostId))!;
        Assert.True(view.SettlementModeLocked);
    }

    // ------------------------------------------------------------ 13. closing

    [SkippableFact]
    public async Task After_closing_joining_and_ordering_are_both_rejected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var host = fixture.CreateTabService(db, clock, Anonymous);

        var opened = await host.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7()));
        var tabId = opened.Tab.TabId;
        var hostId = opened.Tab.Me.ParticipantId;
        var invitation = await host.CreateJoinTokenAsync(tabId, hostId);

        Assert.True(opened.Tab.Me.CanOrderNow);

        await using (var staffDb = fixture.CreateContext(clock))
        {
            var closing = await fixture.CreateTabService(staffDb, clock, TestActor.Waiter(branch.WaiterId))
                .BeginClosingAsync(tabId);

            Assert.Equal(TabStatus.Closing, closing.Status);
        }

        await using var afterDb = fixture.CreateContext(clock);
        var after = fixture.CreateTabService(afterDb, clock, Anonymous);

        // Joining by invitation: the live token was revoked when the tab began closing.
        await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => after.JoinAsync(new JoinTabCommand(invitation.Token, "phone-late")));

        // Joining by scanning the table: the tab takes nobody new.
        var refusedScan = await Assert.ThrowsAsync<DomainStateException>(
            () => after.OpenAsync(new OpenTabCommand(qr, "phone-stranger", Guid.CreateVersion7())));

        Assert.Contains("settled", refusedScan.Message, StringComparison.OrdinalIgnoreCase);

        // And the host cannot issue a fresh invitation.
        await Assert.ThrowsAsync<DomainStateException>(() => after.CreateJoinTokenAsync(tabId, hostId));

        // Ordering: the same rule the ordering endpoints will enforce, and what the app is told.
        var access = (await new AuthorizationQueries(afterDb).GetTabParticipantAccessAsync(tabId, hostId))!;
        Assert.Equal(TabStatus.Closing, access.TabStatus);
        Assert.False(TabPermissions.MayOrder(access.ParticipantStatus, access.CanOrder, access.TabStatus));

        var view = (await fixture.CreateTabQuery(afterDb).GetForParticipantAsync(tabId, hostId))!;
        Assert.Equal(TabStatus.Closing, view.Status);
        Assert.False(view.Me.CanOrderNow);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.TabParticipants.CountAsync(p => p.TabId == tabId));
        Assert.True(await verify.TabJoinTokens.AllAsync(t => t.TabId != tabId || t.RevokedAtUtc != null));
    }

    // ------------------------------------------------------------ display name

    [SkippableFact]
    public async Task A_participant_can_name_themself_and_the_host_sees_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, _, qr) = await ArrangeAsync();
        await using var ownedDb = db;
        var service = fixture.CreateTabService(db, clock, Anonymous);

        var (tabId, hostId, guestId) = await TabTestData.OpenWithApprovedGuestAsync(service, qr);

        var renamed = await service.SetDisplayNameAsync(tabId, guestId, "Nare");
        Assert.Equal("Nare", renamed.DisplayName);

        await using var verify = fixture.CreateContext(clock);
        var hostView = (await fixture.CreateTabQuery(verify).GetForParticipantAsync(tabId, hostId))!;
        Assert.Contains(hostView.Participants, p => p.ParticipantId == guestId && p.DisplayName == "Nare");
    }

    // ------------------------------------------------------------ arrange

    private async Task<(YallaDbContext Db, TestClock Clock, TestBranch Branch, string QrToken)> ArrangeAsync()
    {
        var clock = new TestClock(Now);
        var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var qr = await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        return (db, clock, branch, qr);
    }
}

/// <summary>Fixture steps the tab tests share, over their own short-lived contexts.</summary>
internal static class TabTestData
{
    /// <summary>The host scans, a guest scans and lands pending, the host approves them.</summary>
    public static async Task<(Guid TabId, Guid HostId, Guid GuestId)> OpenWithApprovedGuestAsync(
        TabService service,
        string qrToken,
        bool hideTotalFromGuests = false)
    {
        var opened = await service.OpenAsync(new OpenTabCommand(
            qrToken, "phone-host", Guid.CreateVersion7(), "Aram", HideTotalFromGuests: hideTotalFromGuests));

        var joined = await service.OpenAsync(new OpenTabCommand(qrToken, "phone-guest", Guid.CreateVersion7(), "Guest"));

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, joined.Outcome);

        var approved = await service.ApproveParticipantAsync(
            opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);

        Assert.Equal(ParticipantStatus.Approved, approved.Status);

        return (opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);
    }

    /// <summary>
    /// One order line placed by a participant. Ordering is the next task; this writes the rows it
    /// will write, so the visibility rules have something to hide.
    /// </summary>
    public static async Task<Guid> AddOrderLineAsync(
        SqlServerFixture fixture,
        TestClock clock,
        TestBranch branch,
        Guid tabId,
        Guid participantId,
        string name,
        long priceAmd)
    {
        await using var db = fixture.CreateContext(clock);

        var category = new MenuCategory(branch.BranchId, "Drinks " + Guid.NewGuid().ToString("N")[..8], 0);
        var item = new MenuItem(
            category.Id, name, $"A {name.ToLowerInvariant()}", priceAmd,
            "https://cdn.example.test/drink.jpg", "water", "none", "250 ml", 3);

        db.MenuCategories.Add(category);
        db.MenuItems.Add(item);

        var order = TabOrder.PlacedByDiner(tabId, participantId, clock.UtcNow);
        var line = order.AddLine(item.Id, name, priceAmd, quantity: 1);
        db.TabOrders.Add(order);

        await db.SaveChangesAsync();

        return line.Id;
    }

    /// <summary>Server-computed totals, so a hidden total is a real number and not a zero.</summary>
    public static async Task SetTotalsAsync(
        SqlServerFixture fixture,
        TestClock clock,
        Guid tabId,
        long subtotalAmd,
        long serviceChargeAmd,
        long paidAmd = 0L)
    {
        await using var db = fixture.CreateContext(clock);
        var tab = await db.Tabs.FirstAsync(t => t.Id == tabId);
        tab.ApplyComputedTotals(subtotalAmd, serviceChargeAmd, paidAmd);
        await db.SaveChangesAsync();
    }
}
