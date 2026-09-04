using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The state machine at the entity level: which transitions exist and, more importantly, which
/// do not. These need no database, so they run everywhere.
/// </summary>
public class TableStatusTransitionTests
{
    // ------------------------------------------------------------------ valid transitions

    [Fact]
    public void Free_to_Occupied_is_allowed_for_a_walk_in_or_a_reservation()
    {
        var table = Free();
        var sessionId = Guid.CreateVersion7();

        table.Occupy(sessionId);

        Assert.Equal(TableStatus.Occupied, table.Status);
        Assert.Equal(sessionId, table.CurrentSessionId);
    }

    [Fact]
    public void Free_to_Held_is_allowed()
    {
        var table = Free();

        table.PlaceHold();

        Assert.Equal(TableStatus.Held, table.Status);
        Assert.Null(table.CurrentSessionId);
    }

    [Fact]
    public void Held_to_Free_is_allowed()
    {
        var table = Held();

        table.ReleaseHold();

        Assert.Equal(TableStatus.Free, table.Status);
    }

    [Fact]
    public void Held_to_Occupied_is_allowed()
    {
        var table = Held();

        table.Occupy(Guid.CreateVersion7());

        Assert.Equal(TableStatus.Occupied, table.Status);
    }

    [Fact]
    public void Occupied_to_Free_is_allowed_and_clears_the_session()
    {
        var table = Occupied();

        table.Vacate();

        Assert.Equal(TableStatus.Free, table.Status);
        Assert.Null(table.CurrentSessionId);
    }

    [Theory]
    [InlineData(TableStatus.Free)]
    [InlineData(TableStatus.Held)]
    public void An_empty_table_can_be_taken_out_of_service(TableStatus from)
    {
        var table = InState(from);

        table.MarkOutOfService();

        Assert.Equal(TableStatus.OutOfService, table.Status);
    }

    [Fact]
    public void OutOfService_to_Free_is_allowed()
    {
        var table = OutOfService();

        table.ReturnToService();

        Assert.Equal(TableStatus.Free, table.Status);
    }

    // ------------------------------------------------------------------ invalid transitions

    [Fact]
    public void An_occupied_table_cannot_be_marked_out_of_service()
    {
        var table = Occupied();

        // The whole point: a table with diners at it cannot be marked broken. Free it first,
        // otherwise the floor plan shows nobody sitting where somebody is sitting.
        var ex = Assert.Throws<InvalidTableTransitionException>(table.MarkOutOfService);

        Assert.Equal(TableStatus.Occupied, ex.FromStatus);
        Assert.Equal(TableStatus.OutOfService, ex.ToStatus);
        Assert.Equal(TableStatus.Occupied, table.Status);
    }

    [Fact]
    public void An_out_of_service_table_cannot_be_seated()
    {
        var table = OutOfService();

        Assert.Throws<InvalidTableTransitionException>(() => table.Occupy(Guid.CreateVersion7()));
    }

    [Fact]
    public void An_out_of_service_table_cannot_be_held()
    {
        var table = OutOfService();

        Assert.Throws<InvalidTableTransitionException>(table.PlaceHold);
    }

    [Fact]
    public void An_occupied_table_cannot_be_seated_again()
    {
        var table = Occupied();

        Assert.Throws<InvalidTableTransitionException>(() => table.Occupy(Guid.CreateVersion7()));
    }

    [Fact]
    public void An_occupied_table_cannot_be_held()
    {
        var table = Occupied();

        Assert.Throws<InvalidTableTransitionException>(table.PlaceHold);
    }

    [Theory]
    [InlineData(TableStatus.Free)]
    [InlineData(TableStatus.Occupied)]
    [InlineData(TableStatus.OutOfService)]
    public void Only_a_held_table_can_have_its_hold_released(TableStatus from)
    {
        var table = InState(from);

        Assert.Throws<InvalidTableTransitionException>(table.ReleaseHold);
    }

    [Theory]
    [InlineData(TableStatus.Free)]
    [InlineData(TableStatus.Held)]
    [InlineData(TableStatus.OutOfService)]
    public void Only_an_occupied_table_can_be_freed(TableStatus from)
    {
        var table = InState(from);

        Assert.Throws<InvalidTableTransitionException>(table.Vacate);
    }

    [Theory]
    [InlineData(TableStatus.Free)]
    [InlineData(TableStatus.Held)]
    [InlineData(TableStatus.Occupied)]
    public void Only_an_out_of_service_table_can_be_returned_to_service(TableStatus from)
    {
        var table = InState(from);

        Assert.Throws<InvalidTableTransitionException>(table.ReturnToService);
    }

    // ------------------------------------------------------------------ the transition table

    [Fact]
    public void The_transition_table_and_the_enum_agree_on_which_states_exist()
    {
        // Reserved was retired: if someone reintroduces it as a stored value this fails, which is
        // the point. Derived presentation lives in DerivedTableState instead.
        var stored = Enum.GetValues<TableStatus>();

        Assert.Equal(4, stored.Length);
        Assert.DoesNotContain("Reserved", stored.Select(s => s.ToString()));

        foreach (var status in stored)
        {
            Assert.NotEmpty(TableStatusTransitions.From(status));
        }
    }

    [Fact]
    public void Occupied_leads_only_to_Free()
    {
        Assert.Equal([TableStatus.Free], TableStatusTransitions.From(TableStatus.Occupied));
    }

    // ------------------------------------------------------------------ helpers

    private static DiningTable Free() => DiningTableTests.NewTable();

    private static DiningTable Held()
    {
        var table = Free();
        table.PlaceHold();
        return table;
    }

    private static DiningTable Occupied()
    {
        var table = Free();
        table.Occupy(Guid.CreateVersion7());
        return table;
    }

    private static DiningTable OutOfService()
    {
        var table = Free();
        table.MarkOutOfService();
        return table;
    }

    private static DiningTable InState(TableStatus status) => status switch
    {
        TableStatus.Free => Free(),
        TableStatus.Held => Held(),
        TableStatus.Occupied => Occupied(),
        TableStatus.OutOfService => OutOfService(),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown state."),
    };
}
