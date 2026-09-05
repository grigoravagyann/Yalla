using Yalla.Application.Tabs;
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
    /// A voided line is nobody's item and counts toward no total - the same rule the entity applies.
    /// </summary>
    [Fact]
    public void A_voided_line_is_not_shown_and_not_counted()
    {
        var tab = Tab() with
        {
            Lines =
            [
                new TabLineSnapshot(Guid.CreateVersion7(), AniId, "Cancelled tea", 2_400L, 1, false, IsVoided: true, []),
            ],
        };

        var view = TabProjection.Project(tab, AniId)!;

        Assert.Empty(view.MyLines);
        Assert.Equal(0L, view.MyItemsSubtotalAmd);
    }

    private static TabSnapshot Tab(bool aniSeesTotal = true) =>
        new(
            TabId: Guid.CreateVersion7(),
            BranchId: Guid.CreateVersion7(),
            DiningTableId: Guid.CreateVersion7(),
            TableLabel: "7",
            Status: TabStatus.Open,
            SettlementMode: SettlementMode.AnyonePaysAnyAmount,
            SettlementModeLockedAtUtc: null,
            HideTotalFromGuests: !aniSeesTotal,
            HostParticipantId: HostId,
            OpenedAtUtc: Opened,
            ClosedAtUtc: null,
            SubtotalAmd: 9_000L,
            ServiceChargeAmd: 900L,
            TotalAmd: 9_900L,
            PaidAmd: 0L,
            RemainingAmd: 9_900L,
            Participants:
            [
                new TabParticipantSnapshot(HostId, "Host", ParticipantRole.Host, ParticipantStatus.Approved, true, true, true, Opened),
                new TabParticipantSnapshot(AniId, "Ani", ParticipantRole.Guest, ParticipantStatus.Approved, true, aniSeesTotal, false, Opened.AddMinutes(2)),
                new TabParticipantSnapshot(PendingId, "Guest 3", ParticipantRole.Guest, ParticipantStatus.PendingApproval, true, false, false, Opened.AddMinutes(5)),
            ],
            Lines:
            [
                new TabLineSnapshot(Guid.CreateVersion7(), HostId, "Coffee", 2_500L, 1, false, false, []),
                new TabLineSnapshot(Guid.CreateVersion7(), AniId, "Tea", 2_400L, 1, false, false, []),
            ]);
}
