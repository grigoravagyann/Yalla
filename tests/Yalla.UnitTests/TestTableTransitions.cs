using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// A table's transitions with everything a test does not care about filled in - no reason, a fresh command id, a fixed
/// instant - and the event each one raises dropped (<c>docs/domain-events.md</c>).
/// </summary>
/// <remarks>
/// For tests about the state machine's rules and for seeders that build a floor straight to the rows over a bare
/// context: what they set up is history, told nowhere, and a bare context refuses to save an event. A test about what a
/// transition tells the rest of the system drives it through the state machine (<c>TableStateService</c>), or calls the
/// domain method itself and reads <c>DequeueEvents()</c> (<c>DiningTableEventTests</c>).
/// </remarks>
internal static class TestTableTransitions
{
    private static readonly DateTime At = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Seats <paramref name="sitting"/>, a walk-in as far as the event would have said.</summary>
    public static void Occupy(this DiningTable table, TableSession sitting) =>
        Dropping(table, () => table.Occupy(sitting, TableSeating.WalkIn, null, null, Guid.CreateVersion7(), sitting.SeatedAtUtc));

    /// <summary>Seats a party of two nobody booked, and answers with their sitting.</summary>
    public static TableSession SeatAnyone(this DiningTable table)
    {
        var sitting = TableSession.SeatWalkIn(table.BranchId, table.Id, 2, At);
        table.Occupy(sitting);

        return sitting;
    }

    public static void PlaceHold(this DiningTable table) =>
        Dropping(table, () => table.PlaceHold(null, null, Guid.CreateVersion7(), At));

    public static void ReleaseHold(this DiningTable table) =>
        Dropping(table, () => table.ReleaseHold(null, Guid.CreateVersion7(), At));

    public static void Vacate(this DiningTable table) =>
        Dropping(table, () => table.Vacate(null, null, null, null, null, Guid.CreateVersion7(), At));

    public static void MarkOutOfService(this DiningTable table) =>
        Dropping(table, () => table.MarkOutOfService(0, null, Guid.CreateVersion7(), At));

    public static void ReturnToService(this DiningTable table) =>
        Dropping(table, () => table.ReturnToService(null, Guid.CreateVersion7(), At));

    private static void Dropping(DiningTable table, Action transition)
    {
        transition();
        table.DequeueEvents();
    }
}
