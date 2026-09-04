using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// The complete set of legal physical transitions, in one readable place.
/// </summary>
/// <remarks>
/// <para>
/// Kept as data next to the named transition methods on <see cref="DiningTable"/> so the whole
/// machine can be read at a glance and so a reviewer can see what is <i>missing</i> - which is
/// the part that matters. There is no path into <see cref="TableStatus.OutOfService"/> from
/// <see cref="TableStatus.Occupied"/>: a table with diners at it cannot be marked broken, it has
/// to be freed first.
/// </para>
/// <para>
/// Callers do not use this to drive changes. It exists to validate the named methods, because a
/// generic <c>SetStatus(status)</c> is how illegal states get in.
/// </para>
/// </remarks>
public static class TableStatusTransitions
{
    private static readonly IReadOnlyDictionary<TableStatus, TableStatus[]> AllowedTargets =
        new Dictionary<TableStatus, TableStatus[]>
        {
            // SeatWalkIn / SeatReservation, HoldForLateParty, MarkOutOfService
            [TableStatus.Free] = [TableStatus.Occupied, TableStatus.Held, TableStatus.OutOfService],

            // SeatHeldParty, ReleaseHold, MarkOutOfService
            [TableStatus.Held] = [TableStatus.Occupied, TableStatus.Free, TableStatus.OutOfService],

            // FreeTable. Note: no OutOfService - free it first.
            [TableStatus.Occupied] = [TableStatus.Free],

            // ReturnToService
            [TableStatus.OutOfService] = [TableStatus.Free],
        };

    public static bool IsAllowed(TableStatus from, TableStatus to) =>
        AllowedTargets.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>The states a table can reach from <paramref name="from"/>. Empty for an unknown state.</summary>
    public static IReadOnlyCollection<TableStatus> From(TableStatus from) =>
        AllowedTargets.TryGetValue(from, out var targets) ? targets : [];
}
