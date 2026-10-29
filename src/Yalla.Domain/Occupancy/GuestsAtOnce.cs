using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// How many booked guests a branch that books by time only (<see cref="BookingMode.TimeOnly"/>) has at
/// once, and whether one more party fits (2026-10-29).
/// </summary>
/// <remarks>
/// <para>
/// Counted over each booking's sitting, not per clock slot: a party at 19:00 for ninety minutes is in
/// the room at 20:00, and two parties who never overlap never add up. The answer for a new sitting is
/// the busiest moment inside it - the peak, found by sweeping the starts and ends that fall within.
/// </para>
/// <para>
/// Every live booking made by time only counts - waiting, confirmed or seated - including one a waiter
/// has since given a table, because the limit is about the room, not about which table they got.
/// </para>
/// </remarks>
public static class GuestsAtOnce
{
    /// <summary>The statuses that hold a place in the room.</summary>
    public static readonly IReadOnlyList<ReservationStatus> Holding =
        [ReservationStatus.PendingApproval, ReservationStatus.Confirmed, ReservationStatus.Seated];

    /// <summary>The most guests in the room at any moment of <paramref name="window"/>.</summary>
    public static int PeakWithin(IEnumerable<(BookedInterval Sitting, int Guests)> bookings, BookedInterval window)
    {
        ArgumentNullException.ThrowIfNull(bookings);

        var changes = new List<(DateTime At, int Delta)>();

        foreach (var (sitting, guests) in bookings)
        {
            if (sitting.StartUtc >= window.EndUtc || sitting.EndUtc <= window.StartUtc)
            {
                continue;
            }

            changes.Add((sitting.StartUtc > window.StartUtc ? sitting.StartUtc : window.StartUtc, guests));
            changes.Add((sitting.EndUtc < window.EndUtc ? sitting.EndUtc : window.EndUtc, -guests));
        }

        // Ends before starts at the same instant: a table turned at 20:00 is free for the 20:00 party.
        var peak = 0;
        var now = 0;

        foreach (var (_, delta) in changes.OrderBy(c => c.At).ThenBy(c => c.Delta))
        {
            now += delta;
            peak = Math.Max(peak, now);
        }

        return peak;
    }

    /// <summary>How many more guests fit at the busiest moment of <paramref name="window"/>; never negative.</summary>
    public static int LeftWithin(
        IEnumerable<(BookedInterval Sitting, int Guests)> bookings,
        BookedInterval window,
        int capacity) =>
        Math.Max(0, capacity - PeakWithin(bookings, window));
}

/// <summary>
/// A booking by time only for more guests than the branch has room for then: 409 <c>time-full</c>, with
/// <c>context</c> <c>branchId</c>, <c>partySize</c> and <c>guestsLeft</c> - how many it could still take.
/// </summary>
public sealed class TimeFullException(Guid branchId, int partySize, int guestsLeft)
    : DomainStateException(
        guestsLeft == 0
            ? "There is no room left at that time. Pick another time."
            : $"There is room for {guestsLeft} more at that time, not {partySize}. Pick another time or a smaller party.")
{
    public const string ErrorCode = "time-full";

    public Guid BranchId { get; } = branchId;

    public int PartySize { get; } = partySize;

    public int GuestsLeft { get; } = guestsLeft;
}
