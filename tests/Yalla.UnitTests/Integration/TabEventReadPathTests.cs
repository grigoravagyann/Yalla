using Microsoft.EntityFrameworkCore;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What <c>TabBillingQuery.GetEventsAsync</c> reads to answer a page, by who is asking.
/// </summary>
/// <remarks>
/// <para>
/// Every phone at a table polls the stream every three seconds, so what one poll costs is multiplied
/// by every phone at every table. The reader's own row decides the audience first; only a reader
/// the stream is redacted for, on a page with something on it, pays for reading the whole tab.
/// These tests count the tab reads through a wrapper around the real <see cref="ITabQuery"/>.
/// </para>
/// <para>
/// And a branch below the Paid tier answers every page the same way, empty or not - it used to be
/// refused only when there was something on the page, because only then was the tab read.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabEventReadPathTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 18, 19, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task A_reader_who_may_see_everything_gets_the_stream_as_stored_without_the_tab_being_read()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        var (billing, reads) = Billing(world);

        var page = await billing.GetEventsAsync(world.TabId, world.HostId, afterSequence: 0L, limit: 0);

        Assert.Equal(0, reads.Count);
        Assert.NotEmpty(page.Events);

        // As stored: the price of the guest's order, which a restricted reader would not get.
        var placed = page.Events.Last(e => e.Type == TabEventType.OrderPlaced);
        Assert.Equal(TestMenu.KhachapuriAmd, placed.Payload.GetProperty("lines")[0].GetProperty("unitPriceAmd").GetInt64());
    }

    [SkippableFact]
    public async Task A_poll_with_nothing_new_reads_no_tab_whoever_is_reading()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        var (billing, reads) = Billing(world);

        var all = await billing.GetEventsAsync(world.TabId, world.HostId, afterSequence: 0L, limit: 0);

        foreach (var reader in new[] { world.HostId, world.GuestId })
        {
            var page = await billing.GetEventsAsync(world.TabId, reader, all.MaxSequence, limit: 0);

            Assert.Empty(page.Events);
            Assert.Equal(all.MaxSequence, page.MaxSequence);
        }

        Assert.Equal(0, reads.Count);
    }

    [SkippableFact]
    public async Task A_reader_without_the_total_pays_for_one_tab_read_and_gets_the_stream_redacted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        var (billing, reads) = Billing(world);

        var page = await billing.GetEventsAsync(world.TabId, world.GuestId, afterSequence: 0L, limit: 0);

        Assert.Equal(1, reads.Count);

        // The host's coffee is not theirs; their own khachapuri is, price and all.
        var placed = page.Events.Where(e => e.Type == TabEventType.OrderPlaced).ToList();
        Assert.Equal(0, placed[0].Payload.GetProperty("lines").GetArrayLength());
        Assert.Equal(TestMenu.KhachapuriAmd, placed[1].Payload.GetProperty("lines")[0].GetProperty("unitPriceAmd").GetInt64());
    }

    [SkippableFact]
    public async Task A_branch_below_the_paid_tier_refuses_an_empty_page_and_a_full_one_alike()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        var (billing, reads) = Billing(world);

        var all = await billing.GetEventsAsync(world.TabId, world.HostId, afterSequence: 0L, limit: 0);

        await using (var db = fixture.CreateContext(world.Clock))
        {
            var branch = await db.Branches.FirstAsync(b => b.Id == world.Branch.BranchId);
            branch.SetSubscriptionTier(SubscriptionTier.Free);
            await db.SaveChangesAsync();
        }

        foreach (var reader in new[] { world.HostId, world.GuestId })
        {
            foreach (var after in new[] { 0L, all.MaxSequence })
            {
                var refused = await Assert.ThrowsAsync<FeatureNotEnabledException>(
                    () => billing.GetEventsAsync(world.TabId, reader, after, limit: 0));

                Assert.Equal(world.Branch.BranchId, refused.BranchId);
            }
        }

        // Refused before anything else was read.
        Assert.Equal(0, reads.Count);
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(TestClock Clock, TestBranch Branch, Guid TabId, Guid HostId, Guid GuestId);

    /// <summary>Counts the tab reads the events query makes, and makes them for real.</summary>
    private sealed class CountingTabQuery(ITabQuery inner) : ITabQuery
    {
        public int Count { get; private set; }

        public Task<TabView?> GetForParticipantAsync(Guid tabId, Guid participantId, CancellationToken cancellationToken = default)
        {
            Count++;

            return inner.GetForParticipantAsync(tabId, participantId, cancellationToken);
        }

        public Task<TabStaffView?> GetForStaffAsync(Guid tabId, CancellationToken cancellationToken = default) =>
            inner.GetForStaffAsync(tabId, cancellationToken);
    }

    private (TabBillingQuery Billing, CountingTabQuery Reads) Billing(World world)
    {
        var db = fixture.CreateContext(world.Clock);
        var reads = new CountingTabQuery(new TabQuery(db));

        return (fixture.CreateBillingQuery(db, world.Clock, TestActor.Participant(world.GuestId), reads), reads);
    }

    /// <summary>A host who hid the total, an approved guest, a coffee for the host and a khachapuri for the guest.</summary>
    private async Task<World> ArrangeAsync()
    {
        var clock = new TestClock(Now);

        TestBranch branch;
        TestMenu menu;
        string qr;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            qr = await db.DiningTables
                .Where(t => t.Id == branch.FirstTableId)
                .Select(t => t.QrToken)
                .FirstAsync();
        }

        Guid tabId;
        Guid hostId;
        Guid guestId;

        await using (var tabDb = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(tabDb, clock, new TestActor(ActorType.Diner, null, null, null));

            var opened = await tabs.OpenAsync(new OpenTabCommand(
                qr, "phone-host", Guid.CreateVersion7(), "Aram", HideTotalFromGuests: true));
            var joined = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7(), "Nune"));

            await tabs.ApproveParticipantAsync(
                opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);

            tabId = opened.Tab.TabId;
            hostId = opened.Tab.Me.ParticipantId;
            guestId = joined.Tab.Me.ParticipantId;
        }

        foreach (var (who, item) in new[] { (hostId, menu.Coffee), (guestId, menu.Khachapuri) })
        {
            await using var orderDb = fixture.CreateContext(clock);

            await fixture.CreateOrderService(orderDb, clock, TestActor.Participant(who))
                .PlaceOrderAsync(new PlaceOrderCommand(tabId, [new OrderItemInput(item, 1)], Guid.CreateVersion7()));
        }

        return new World(clock, branch, tabId, hostId, guestId);
    }
}
