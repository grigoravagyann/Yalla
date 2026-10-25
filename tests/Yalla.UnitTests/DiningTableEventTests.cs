using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// What a table's transitions and a hand-over raise (<c>docs/domain-events.md</c>, tables): one event per change, after
/// it, carrying where the table came from, the reason as given and the command - and nothing for a refused one.
/// </summary>
public class DiningTableEventTests
{
    private static readonly DateTime At = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Seating_raises_TableSeated_with_the_sitting_the_booking_and_where_the_table_was()
    {
        var table = DiningTableTests.NewTable();
        table.PlaceHold();
        var sitting = TableSession.SeatWalkIn(table.BranchId, table.Id, 3, At);
        var command = Guid.CreateVersion7();

        table.Occupy(sitting, TableSeating.HeldParty, null, "they phoned", command, At);

        var seated = Assert.IsType<TableSeated>(Assert.Single(table.DequeueEvents()).Event);
        Assert.Same(table, seated.Table);
        Assert.Same(sitting, seated.Sitting);
        Assert.Equal(TableStatus.Held, seated.From);
        Assert.Equal(TableSeating.HeldParty, seated.How);
        Assert.Null(seated.Booking);
        Assert.Equal("they phoned", seated.Reason);
        Assert.Equal(command, seated.ClientCommandId);
        Assert.Equal(At, seated.OccurredAtUtc);
    }

    [Fact]
    public void Freeing_raises_TableFreed_saying_whether_the_tab_closed_or_what_is_still_owed()
    {
        var table = DiningTableTests.NewTable();
        var sitting = table.SeatAnyone();
        var tabId = Guid.CreateVersion7();

        table.Vacate(sitting.Id, null, tabId, null, null, Guid.CreateVersion7(), At);

        var closed = Assert.IsType<TableFreed>(Assert.Single(table.DequeueEvents()).Event);
        Assert.Equal(TableStatus.Occupied, closed.From);
        Assert.Equal(sitting.Id, closed.SittingId);
        Assert.True(closed.TabClosed);
        Assert.Null(closed.OutstandingAmd);
        Assert.Null(closed.Reason);

        table.SeatAnyone();
        table.Vacate(null, null, tabId, 1_320L, "they left", Guid.CreateVersion7(), At);

        var owing = Assert.IsType<TableFreed>(Assert.Single(table.DequeueEvents()).Event);
        Assert.False(owing.TabClosed);
        Assert.Equal(1_320L, owing.OutstandingAmd);
        Assert.Equal("they left", owing.Reason);

        table.SeatAnyone();
        table.Vacate(null, null, null, null, null, Guid.CreateVersion7(), At);

        Assert.False(Assert.IsType<TableFreed>(Assert.Single(table.DequeueEvents()).Event).TabClosed);
    }

    [Fact]
    public void Holding_releasing_breaking_and_mending_each_raise_their_own_event()
    {
        var table = DiningTableTests.NewTable();
        var booking = Guid.CreateVersion7();

        table.PlaceHold(booking, null, Guid.CreateVersion7(), At);
        var held = Assert.IsType<TableHeld>(Assert.Single(table.DequeueEvents()).Event);
        Assert.Equal(TableStatus.Free, held.From);
        Assert.Equal(booking, held.ForReservationId);

        table.ReleaseHold("no-show", Guid.CreateVersion7(), At);
        Assert.Equal(TableStatus.Held, Assert.IsType<TableHoldReleased>(Assert.Single(table.DequeueEvents()).Event).From);

        table.MarkOutOfService(2, "wobbly leg", Guid.CreateVersion7(), At);
        var broken = Assert.IsType<TableTakenOutOfService>(Assert.Single(table.DequeueEvents()).Event);
        Assert.Equal(2, broken.StrandedBookings);
        Assert.Equal("wobbly leg", broken.Reason);

        table.ReturnToService(null, Guid.CreateVersion7(), At);
        Assert.Equal(
            TableStatus.OutOfService, Assert.IsType<TableReturnedToService>(Assert.Single(table.DequeueEvents()).Event).From);
    }

    [Fact]
    public void A_hold_kept_for_longer_raises_TableHoldExtended_and_changes_nothing()
    {
        var table = DiningTableTests.NewTable();
        table.PlaceHold();
        var booking = Guid.CreateVersion7();

        table.NoteHoldExtended(booking, At.AddMinutes(10), null, Guid.CreateVersion7(), At);

        var extended = Assert.IsType<TableHoldExtended>(Assert.Single(table.DequeueEvents()).Event);
        Assert.Equal(booking, extended.ReservationId);
        Assert.Equal(At.AddMinutes(10), extended.HoldExpiresAtUtc);
        Assert.Equal(TableStatus.Held, table.Status);
    }

    [Fact]
    public void A_refused_transition_raises_nothing()
    {
        var table = DiningTableTests.NewTable();
        table.SeatAnyone();

        Assert.Throws<InvalidTableTransitionException>(
            () => table.Occupy(
                TableSession.SeatWalkIn(table.BranchId, table.Id, 2, At), TableSeating.WalkIn, null, null, Guid.CreateVersion7(), At));
        Assert.Throws<InvalidTableTransitionException>(() => table.PlaceHold(null, null, Guid.CreateVersion7(), At));
        Assert.Throws<InvalidTableTransitionException>(() => table.ReleaseHold(null, Guid.CreateVersion7(), At));
        Assert.Throws<InvalidTableTransitionException>(() => table.MarkOutOfService(0, null, Guid.CreateVersion7(), At));
        Assert.Throws<InvalidTableTransitionException>(() => table.ReturnToService(null, Guid.CreateVersion7(), At));

        table.Vacate();
        Assert.Throws<InvalidTableTransitionException>(
            () => table.Vacate(null, null, null, null, null, Guid.CreateVersion7(), At));

        Assert.Empty(table.DequeueEvents());
    }

    [Fact]
    public void A_hand_over_raises_TableTransferred_naming_who_it_was_and_a_refused_one_raises_nothing()
    {
        var lilit = Guid.CreateVersion7();
        var aram = Guid.CreateVersion7();
        var tabId = Guid.CreateVersion7();
        var sitting = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, At, lilit);

        sitting.TransferServer(aram, tabId, At.AddMinutes(5));

        var transferred = Assert.IsType<TableTransferred>(Assert.Single(sitting.DequeueEvents()).Event);
        Assert.Same(sitting, transferred.Sitting);
        Assert.Equal(tabId, transferred.TabId);
        Assert.Equal(lilit, transferred.FromStaffId);
        Assert.Equal(aram, transferred.ToStaffId);
        Assert.Equal(At.AddMinutes(5), transferred.OccurredAtUtc);

        Assert.Throws<DomainStateException>(() => sitting.TransferServer(aram, tabId, At.AddMinutes(6)));
        Assert.Empty(sitting.DequeueEvents());
    }
}
