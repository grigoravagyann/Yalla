using Yalla.Application.Media;
using Yalla.Application.Tabs;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests;

/// <summary>
/// The single projection that builds a participant's view of a tab, tested directly.
/// </summary>
/// <remarks>
/// These are the visibility rules from <c>docs/tabs.md</c>, exercised as a pure function over
/// in-memory records - no database, no HTTP. That is deliberate: the rule that must not leak is
/// "a guest without the total sees their own items and no table aggregate", and the cleanest place
/// to prove it holds is the one function that decides it.
/// </remarks>
public class TabProjectionTests
{
    private static readonly DateTime Opened = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid HostId = Guid.CreateVersion7();
    private static readonly Guid AniId = Guid.CreateVersion7();
    private static readonly Guid PendingId = Guid.CreateVersion7();

    [Fact]
    public void The_host_sees_the_table_total_and_every_line()
    {
        var view = TabProjection.Project(Tab(), HostId)!;

        Assert.True(view.TableTotalVisible);
        Assert.NotNull(view.TableTotal);
        Assert.Equal(9_900L, view.TableTotal!.TotalAmd);
        Assert.NotNull(view.TableLines);
        Assert.Equal(2, view.TableLines!.Count);
    }

    /// <summary>
    /// Test case 9. A guest without <c>CanSeeTableTotal</c> gets their own lines and <b>no table
    /// aggregate at all</b> - not a zero a client could render as free.
    /// </summary>
    [Fact]
    public void A_guest_without_the_total_gets_their_own_items_and_no_aggregate()
    {
        // Ani ordered the tea (2,400); the coffee (2,500) is the host's.
        var view = TabProjection.Project(Tab(aniSeesTotal: false), AniId)!;

        // Own item is present.
        var mine = Assert.Single(view.MyLines);
        Assert.Equal("Tea", mine.Name);
        Assert.Equal(2_400L, view.MyItemsSubtotalAmd);

        // The table aggregate is ABSENT, not zero and not null-as-free.
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.TableTotal);

        // And she cannot see the host's coffee.
        Assert.Null(view.TableLines);
    }

    [Fact]
    public void A_guest_allowed_the_total_sees_the_aggregate_and_everyone_elses_items()
    {
        var view = TabProjection.Project(Tab(aniSeesTotal: true), AniId)!;

        Assert.True(view.TableTotalVisible);
        Assert.Equal(9_900L, view.TableTotal!.TotalAmd);
        Assert.Equal(2, view.TableLines!.Count);

        // Own items are still their own, whichever flag is set.
        Assert.Single(view.MyLines);
        Assert.Equal(2_400L, view.MyItemsSubtotalAmd);
    }

    /// <summary>Test case 7, the read half: a pending participant sees only their own state.</summary>
    [Fact]
    public void A_pending_participant_sees_only_themselves()
    {
        var view = TabProjection.Project(Tab(), PendingId)!;

        // No table total - approval is required for it, whatever the flag says.
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.TableTotal);

        // The roster is just them: they cannot read who else is at the table.
        var only = Assert.Single(view.Participants);
        Assert.Equal(PendingId, only.ParticipantId);

        // They have ordered nothing, and their own subtotal is a real zero, not a hidden total.
        Assert.Empty(view.MyLines);
        Assert.Equal(0L, view.MyItemsSubtotalAmd);
        Assert.False(view.Me.CanOrderNow);
    }

    [Fact]
    public void Someone_not_on_the_tab_gets_nothing()
    {
        Assert.Null(TabProjection.Project(Tab(), Guid.CreateVersion7()));
    }

    [Fact]
    public void A_pending_participant_may_order_only_once_approved_and_while_open()
    {
        Assert.False(TabPermissions.MayOrder(ParticipantStatus.PendingApproval, canOrder: true, TabStatus.Open));
        Assert.True(TabPermissions.MayOrder(ParticipantStatus.Approved, canOrder: true, TabStatus.Open));
        Assert.False(TabPermissions.MayOrder(ParticipantStatus.Approved, canOrder: false, TabStatus.Open));

        // Test case 13, the ordering half: closing shuts ordering for everyone.
        Assert.False(TabPermissions.MayOrder(ParticipantStatus.Approved, canOrder: true, TabStatus.Closing));
    }

    /// <summary>
    /// <b>Test 6, as a unit.</b> A voided line stays on the diner's bill, labelled with the reason,
    /// and counts toward no total.
    /// </summary>
    /// <remarks>
    /// This test asserted the opposite until Prompt 11 - that the line was <i>absent</i> - which is
    /// what the code did and the reverse of what Prompt 8 specified and what the endpoint's own
    /// documentation claimed. It is the clearest example of a test written against the
    /// implementation rather than the specification: a missing field could never fail it.
    /// <para>
    /// A total that drops with no visible cause is the fastest way to make somebody distrust the
    /// app, and the person who then has to explain it is a waiter standing at the table.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_voided_line_stays_visible_with_its_reason_and_counts_toward_nothing()
    {
        var voidedAt = Opened.AddMinutes(20);

        var tab = Tab() with
        {
            Lines =
            [
                Line(AniId, "Cancelled tea", 2_400L)
                    with { IsVoided = true, VoidedAtUtc = voidedAt, VoidReason = "wrong order" },
            ],
        };

        var view = TabProjection.Project(tab, AniId)!;

        var line = Assert.Single(view.MyLines);

        Assert.True(line.IsVoided);
        Assert.Equal("wrong order", line.VoidReason);
        Assert.Equal(voidedAt, line.VoidedAtUtc);

        // Visible, and worth nothing. Both halves matter: the diner sees the coffee was removed,
        // and no total moves because of it.
        Assert.Equal(0L, line.LineTotalAmd);
        Assert.Equal(0L, view.MyItemsSubtotalAmd);
    }

    /// <summary>
    /// <b>Test 7, as a unit.</b> A comp appears on the diner's bill with the manager's reason.
    /// </summary>
    [Fact]
    public void An_adjustment_is_on_the_view_with_the_reason_the_manager_typed()
    {
        var tab = Tab() with
        {
            Adjustments =
            [
                new TabAdjustmentSnapshot(
                    Guid.CreateVersion7(),
                    TabOrderLineId: null,
                    AdjustmentKind.Comp,
                    Percent: null,
                    AmountAmd: 2_400L,
                    ReductionAmd: 2_400L,
                    Reason: "sorry about the wait",
                    CreatedAtUtc: Opened.AddMinutes(30),
                    IsVoided: false),
            ],
        };

        var adjustment = Assert.Single(TabProjection.Project(tab, AniId)!.Adjustments);

        Assert.Equal(AdjustmentKind.Comp, adjustment.Kind);
        Assert.Equal("sorry about the wait", adjustment.Reason);
        Assert.Equal(2_400L, adjustment.ReductionAmd);
    }

    /// <summary>
    /// <b>Test 8, as a unit.</b> The service charge percentage reaches a guest with the total
    /// hidden, and no aggregate does.
    /// </summary>
    /// <remarks>
    /// The percentage is a fact about the venue rather than an aggregate. A diner who can see their
    /// own items must be able to work out what they will be charged on them, and Prompt 8 requires
    /// the bill to state it from the first item - but it lived on the reservation policy behind
    /// <c>ManagerOrAbove</c>, where no diner could ever read it.
    /// </remarks>
    [Fact]
    public void The_service_charge_percentage_reaches_a_guest_who_cannot_see_the_total()
    {
        var view = TabProjection.Project(Tab(aniSeesTotal: false), AniId)!;

        Assert.Equal(10m, view.ServiceChargePercent);

        // And the aggregate is still absent, which is the rule that must not have been loosened to
        // get the percentage through.
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.TableTotal);
        Assert.Null(view.TableLines);
    }

    /// <summary>
    /// <b>Test 11, as a unit.</b> The split badge counts who was there when the line was ordered.
    /// </summary>
    /// <remarks>
    /// Computed from the current roster it would silently re-split every existing line each time
    /// somebody new scanned the code - so the bottle poured for two would start reading as split
    /// three ways the moment a third person sat down.
    /// </remarks>
    [Fact]
    public void A_shared_lines_split_count_is_the_snapshot_and_not_the_current_roster()
    {
        var tab = Tab() with
        {
            Lines =
            [
                Line(HostId, "Areni red, bottle", 9_500L)
                    with { IsShared = true, SharedWithParticipantIds = [HostId, AniId] },
            ],
        };

        // Three people are on the tab now; two were there when the bottle was ordered.
        Assert.Equal(3, tab.Participants.Count);

        var line = Assert.Single(TabProjection.Project(tab, AniId)!.MyLines);

        Assert.True(line.IsShared);
        Assert.Equal(2, line.SharedWithCount);
    }

    /// <summary>
    /// <b>Tests 9, 10 and 12, as units.</b> The fields the client needed and had to make a second
    /// call for, or could not get at all.
    /// </summary>
    [Fact]
    public void The_view_carries_the_time_zone_the_stream_position_and_each_lines_order_status()
    {
        var tab = Tab() with
        {
            MaxEventSequence = 42L,
            Lines = [Line(AniId, "Khachapuri", 3_200L) with { OrderStatus = TabOrderStatus.InKitchen }],
        };

        var view = TabProjection.Project(tab, AniId)!;

        // Every instant on this response renders in the branch's zone, never the device's.
        Assert.Equal("Asia/Yerevan", view.TimeZoneId);

        // Where the client stands, without a second call to /events purely to ask.
        Assert.Equal(42L, view.MaxSequence);

        var line = Assert.Single(view.MyLines);

        // The diner watches their own dish move along the rail.
        Assert.Equal(TabOrderStatus.InKitchen, line.OrderStatus);

        // And the three ids and the note the client needs to group a round and link to the menu.
        Assert.NotEqual(Guid.Empty, line.OrderId);
        Assert.NotEqual(Guid.Empty, line.MenuItemId);
        Assert.Equal("no onions", line.Note);
    }

    // ------------------------------------------------------------ tips

    /// <summary>
    /// Tips are money the table handed over, so they reach exactly who the table total reaches - and
    /// staff, always - and they stay outside the totals either way.
    /// </summary>
    [Fact]
    public void Tips_go_where_the_table_total_goes_and_never_into_it()
    {
        var tab = Tab(aniSeesTotal: false) with { PaidAmd = 9_900L, RemainingAmd = 0L, TipsAmd = 1_500L };

        var host = TabProjection.Project(tab, HostId)!;
        Assert.Equal(1_500L, host.TipsAmd);
        Assert.Equal(9_900L, host.TableTotal!.PaidAmd);
        Assert.Equal(0L, host.TableTotal.RemainingAmd);

        // Hidden with the total: absent, not zero.
        Assert.Null(TabProjection.Project(tab, AniId)!.TipsAmd);
        Assert.Null(TabProjection.Project(tab, PendingId)!.TipsAmd);

        var staff = TabProjection.ProjectForStaff(tab);
        Assert.Equal(1_500L, staff.TipsAmd);
        Assert.Equal(9_900L, staff.Totals.PaidAmd);
    }

    // ------------------------------------------------------------ faces on the roster

    [Fact]
    public void A_participants_photo_rides_on_the_roster_me_and_the_staff_list_and_a_pending_viewer_sees_only_their_own()
    {
        var aniPhoto = Photo();
        var pendingPhoto = Photo();

        var tab = Tab() with
        {
            Participants =
            [
                .. Tab().Participants.Select(p => p.ParticipantId == AniId
                    ? p with { Photo = aniPhoto }
                    : p.ParticipantId == PendingId ? p with { Photo = pendingPhoto } : p),
            ],
        };

        // The host sees Ani's face and the waiting guest's, and has none of their own.
        var host = TabProjection.Project(tab, HostId)!;
        Assert.Null(host.Me.Photo);
        Assert.Equal(aniPhoto, host.Participants.Single(p => p.ParticipantId == AniId).Photo);
        Assert.Equal(pendingPhoto, host.Participants.Single(p => p.ParticipantId == PendingId).Photo);
        Assert.Null(host.Participants.Single(p => p.ParticipantId == HostId).Photo);

        // Ani sees her own on "me".
        Assert.Equal(aniPhoto, TabProjection.Project(tab, AniId)!.Me.Photo);

        // The pending guest sees one face: their own. Nobody else's row, so nobody else's picture.
        var pending = TabProjection.Project(tab, PendingId)!;
        var only = Assert.Single(pending.Participants);
        Assert.Equal(pendingPhoto, only.Photo);
        Assert.Equal(pendingPhoto, pending.Me.Photo);

        // Staff see every phone on the table, with its face.
        var staff = TabProjection.ProjectForStaff(tab);
        Assert.Equal(aniPhoto, staff.Participants.Single(p => p.ParticipantId == AniId).Photo);
    }

    [Fact]
    public void A_removed_participant_takes_their_photo_off_the_roster_with_them()
    {
        var tab = Tab() with
        {
            Participants =
            [
                .. Tab().Participants.Select(p => p.ParticipantId == AniId
                    ? p with { Status = ParticipantStatus.Removed, Photo = Photo() }
                    : p),
            ],
        };

        Assert.DoesNotContain(TabProjection.Project(tab, HostId)!.Participants, p => p.ParticipantId == AniId);
        Assert.DoesNotContain(TabProjection.ProjectForStaff(tab).Participants, p => p.ParticipantId == AniId);
    }

    // ------------------------------------------------------------ whose line it is

    /// <summary>
    /// A waiter keyed Ani's order in for her. It is on her bill - <c>/shares</c> charges her for it -
    /// so it is on her own lines and in her own subtotal, though no phone placed it.
    /// </summary>
    [Fact]
    public void A_waiters_order_on_a_guests_behalf_is_on_their_own_lines_and_subtotal()
    {
        var soup = Line(placedBy: null, "Spas", 1_800L) with { OnBehalfOfParticipantId = AniId };
        var tab = Tab(aniSeesTotal: false) with { Lines = [.. Tab().Lines, soup] };

        var view = TabProjection.Project(tab, AniId)!;

        var line = Assert.Single(view.MyLines, l => l.LineId == soup.LineId);
        Assert.Null(line.PlacedByParticipantId);
        Assert.Equal(AniId, line.OnBehalfOfParticipantId);
        Assert.Equal(2_400L + 1_800L, view.MyItemsSubtotalAmd);

        // And it is nobody else's.
        Assert.DoesNotContain(TabProjection.Project(tab, HostId)!.MyLines, l => l.LineId == soup.LineId);
    }

    /// <summary>
    /// A line a waiter keyed in for the table splits across whoever was approved at the time, like a
    /// shared bottle, and <c>/shares</c> charges each of them a part. For a reader allowed the total
    /// it is on their own lines when they were at the table for it - listed, flagged, and kept out of
    /// the own-items subtotal.
    /// </summary>
    [Fact]
    public void A_table_attributed_line_is_on_the_own_lines_of_a_reader_with_the_total_who_was_there()
    {
        var (tab, bread, jug) = TableAttributed(aniSeesTotal: true);

        var view = TabProjection.Project(tab, AniId)!;

        // The bread went on before she was let on.
        Assert.DoesNotContain(view.MyLines, l => l.LineId == bread.LineId);

        var line = Assert.Single(view.MyLines, l => l.LineId == jug.LineId);
        Assert.True(line.IsTableAttributed);
        Assert.False(line.IsShared);
        Assert.Equal(2, line.SharedWithCount);

        // Her tea alone: the jug is split, not hers whole.
        Assert.Equal(2_400L, view.MyItemsSubtotalAmd);
    }

    /// <summary>
    /// <b>Without the total, no table-attributed line is itemised.</b> A waiter who keys everything in
    /// for nobody in particular makes those lines the whole bill, and itemising the ones a guest was
    /// at the table for would hand them the total line by line. She is still charged her part - on
    /// <c>/shares</c>, as money - and a comp off one of those lines follows the line off her bill.
    /// A shared bottle she is on stays, as it always has.
    /// </summary>
    [Fact]
    public void A_table_attributed_line_is_not_itemised_for_a_reader_without_the_total()
    {
        var (plain, _, jug) = TableAttributed(aniSeesTotal: false);
        var wine = Line(HostId, "Areni red, bottle", 9_500L) with { IsShared = true, SharedWithParticipantIds = [HostId, AniId] };
        var offTheJug = Adjustment(jug.LineId, percent: null, reductionAmd: 100L);
        var tab = plain with { Lines = [.. plain.Lines, wine], Adjustments = [offTheJug] };

        var view = TabProjection.Project(tab, AniId)!;

        Assert.DoesNotContain(view.MyLines, l => l.IsTableAttributed);
        Assert.Contains(view.MyLines, l => l.LineId == wine.LineId);
        Assert.Empty(view.Adjustments);

        // Charged for it all the same - the arithmetic's question, not the bill's.
        Assert.True(jug.IsChargedTo(AniId));
        Assert.False(TabProjection.IsOwnLine(jug, AniId, tableTotalVisible: false));
        Assert.True(TabProjection.IsOwnLine(jug, AniId, tableTotalVisible: true));

        // The pending stranger was not at the table for either, and would not be shown them anyway.
        Assert.DoesNotContain(TabProjection.Project(tab, PendingId)!.MyLines, l => l.IsTableAttributed);

        // The host keeps them, on their own lines and the table's.
        var host = TabProjection.Project(tab, HostId)!;
        Assert.Equal(2, host.MyLines.Count(l => l.IsTableAttributed));
        Assert.Single(host.Adjustments);
    }

    private static (TabSnapshot Tab, TabLineSnapshot Bread, TabLineSnapshot Jug) TableAttributed(bool aniSeesTotal)
    {
        var bread = Line(placedBy: null, "Lavash", 800L) with
        {
            IsTableAttributed = true,
            SharedWithParticipantIds = [HostId],
        };
        var jug = Line(placedBy: null, "Water, jug", 600L) with
        {
            IsTableAttributed = true,
            SharedWithParticipantIds = [HostId, AniId],
        };

        return (Tab(aniSeesTotal) with { Lines = [.. Tab().Lines, bread, jug] }, bread, jug);
    }

    /// <summary>
    /// The projection's own lines and the split agree, for everyone at the table and either way the
    /// host set the total: each person's <c>myItemsSubtotalAmd</c> is exactly the own-items figure
    /// <see cref="TabBilling"/> charges them, and every split line they are charged a part of is on
    /// their own lines - except a table-attributed one for a reader without the total, which is the
    /// one place the bill and the arithmetic deliberately differ.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Own_items_are_what_the_split_charges_each_person_for(bool aniSeesTotal)
    {
        var tab = Tab(aniSeesTotal) with
        {
            Lines =
            [
                .. Tab().Lines,
                Line(placedBy: null, "Spas", 1_800L) with { OnBehalfOfParticipantId = AniId },
                Line(placedBy: null, "Khorovats", 6_500L) with { OnBehalfOfParticipantId = HostId },
                Line(placedBy: null, "Lavash", 800L) with { IsTableAttributed = true, SharedWithParticipantIds = [HostId] },
                Line(placedBy: null, "Water, jug", 600L) with { IsTableAttributed = true, SharedWithParticipantIds = [HostId, AniId] },
                Line(HostId, "Areni red, bottle", 9_500L) with { IsShared = true, SharedWithParticipantIds = [HostId, AniId] },
            ],
        };

        var bill = TabBilling.Compute(
            [
                .. tab.Lines.Select(l => new BillingLine(
                    l.LineId,
                    l.IsSplitAcrossParticipants ? null : l.OwningParticipantId,
                    l.UnitPriceAmd,
                    l.Quantity,
                    l.IsVoided,
                    l.IsSplitAcrossParticipants,
                    l.SharedWithParticipantIds)),
            ],
            [],
            [.. tab.Participants.Select(p => new BillingParticipant(p.ParticipantId, p.ParticipantId == HostId, p.Status))],
            tab.ServiceChargePercentSnapshot,
            paidAmd: 0L);

        foreach (var participant in tab.Participants)
        {
            var view = TabProjection.Project(tab, participant.ParticipantId)!;
            var share = bill.Shares.Single(s => s.ParticipantId == participant.ParticipantId);

            Assert.Equal(share.OwnItemsAmd, view.MyItemsSubtotalAmd);

            var charged = tab.Lines
                .Where(l => l.IsSplitAcrossParticipants && l.IsChargedTo(participant.ParticipantId))
                .Where(l => view.TableTotalVisible || !l.IsTableAttributed)
                .Select(l => l.LineId)
                .Order();

            Assert.Equal(charged, view.MyLines.Where(l => l.IsShared || l.IsTableAttributed).Select(l => l.LineId).Order());
        }
    }

    // ------------------------------------------------------------ comps and discounts

    /// <summary>
    /// Without the total, only the adjustments on her own lines, with what they took off. One off
    /// the whole tab or off the host's coffee is the table's money: a percentage off the whole tab
    /// beside what it took off <i>is</i> the subtotal. Dropped rather than shown without amounts,
    /// because the diner app draws every adjustment it is given with what it took off.
    /// </summary>
    [Fact]
    public void A_guest_without_the_total_sees_only_the_adjustments_on_their_own_lines()
    {
        var plain = Tab(aniSeesTotal: false);
        var coffee = plain.Lines.Single(l => l.Name == "Coffee");
        var tea = plain.Lines.Single(l => l.Name == "Tea");

        var offTheTab = Adjustment(lineId: null, percent: 10m, reductionAmd: 490L);
        var offTheCoffee = Adjustment(coffee.LineId, percent: null, reductionAmd: 500L);
        var offTheTea = Adjustment(tea.LineId, percent: null, reductionAmd: 400L);

        var tab = plain with { Adjustments = [offTheTab, offTheCoffee, offTheTea] };

        var hers = Assert.Single(TabProjection.Project(tab, AniId)!.Adjustments);
        Assert.Equal(offTheTea.AdjustmentId, hers.AdjustmentId);
        Assert.Equal(400L, hers.ReductionAmd);
        Assert.Equal(400L, hers.AmountAmd);

        // The host sees all three, and the pending stranger none.
        Assert.Equal(3, TabProjection.Project(tab, HostId)!.Adjustments.Count);
        Assert.Empty(TabProjection.Project(tab, PendingId)!.Adjustments);

        // Allowed the total, Ani sees all three too.
        Assert.Equal(3, TabProjection.Project(tab with { HideTotalFromGuests = false, Participants = Tab(aniSeesTotal: true).Participants }, AniId)!.Adjustments.Count);
    }

    private static TabAdjustmentSnapshot Adjustment(Guid? lineId, decimal? percent, long reductionAmd) =>
        new(
            Guid.CreateVersion7(),
            lineId,
            AdjustmentKind.Comp,
            percent,
            AmountAmd: percent is null ? reductionAmd : null,
            ReductionAmd: reductionAmd,
            Reason: "sorry about the wait",
            CreatedAtUtc: Opened.AddMinutes(30),
            IsVoided: false);

    private static PhotoView Photo() =>
        PhotoView.From(Guid.CreateVersion7(), isExternallyHosted: false, "t", "c", "f", width: 640, height: 640);

    /// <summary>One ordinary line. Overridden per test with a <c>with</c> expression.</summary>
    private static TabLineSnapshot Line(Guid? placedBy, string name, long unitPriceAmd) =>
        new(
            LineId: Guid.CreateVersion7(),
            OrderId: Guid.CreateVersion7(),
            MenuItemId: Guid.CreateVersion7(),
            PlacedByParticipantId: placedBy,
            Name: name,
            UnitPriceAmd: unitPriceAmd,
            Quantity: 1,
            Note: "no onions",
            OrderStatus: TabOrderStatus.New,
            IsShared: false,
            IsVoided: false,
            VoidedAtUtc: null,
            VoidReason: null,
            SharedWithParticipantIds: []);

    private static TabSnapshot Tab(bool aniSeesTotal = true) =>
        new(
            TabId: Guid.CreateVersion7(),
            BranchId: Guid.CreateVersion7(),
            VenueName: "Ararat",
            BranchName: "Opera",
            DiningTableId: Guid.CreateVersion7(),
            TableLabel: "7",
            TimeZoneId: "Asia/Yerevan",
            Status: TabStatus.Open,
            SettlementMode: SettlementMode.AnyonePaysAnyAmount,
            SettlementModeLockedAtUtc: null,
            HideTotalFromGuests: !aniSeesTotal,
            HostParticipantId: HostId,
            ServiceChargePercentSnapshot: 10m,
            OpenedAtUtc: Opened,
            ClosedAtUtc: null,
            SubtotalAmd: 9_000L,
            ServiceChargeAmd: 900L,
            TotalAmd: 9_900L,
            PaidAmd: 0L,
            RemainingAmd: 9_900L,
            MaxEventSequence: 3L,
            Participants:
            [
                new TabParticipantSnapshot(HostId, "Host", ParticipantRole.Host, ParticipantStatus.Approved, true, true, true, Opened),
                new TabParticipantSnapshot(AniId, "Ani", ParticipantRole.Guest, ParticipantStatus.Approved, true, aniSeesTotal, false, Opened.AddMinutes(2)),
                new TabParticipantSnapshot(PendingId, "Guest 3", ParticipantRole.Guest, ParticipantStatus.PendingApproval, true, false, false, Opened.AddMinutes(5)),
            ],
            Lines:
            [
                Line(HostId, "Coffee", 2_500L),
                Line(AniId, "Tea", 2_400L),
            ],
            Adjustments: []);
}
