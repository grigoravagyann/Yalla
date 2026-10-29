using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// One day at a branch, answered every quarter of an hour: which start times have a table for this party.
/// </summary>
/// <remarks>
/// <para>
/// The time picker's read. Without it the app offered every half hour of the opening hours and let
/// the diner find out on the room screen - or at the confirm button - that nothing was free. Now it
/// strikes those times before anybody taps them.
/// </para>
/// <para>
/// <b>Never a second opinion.</b> Each listed time is judged by the same rules, over the same rows,
/// as <see cref="BranchAvailability"/> for that time: a slot listed as available is one where the
/// single-slot read offers at least <see cref="DaySlot.FreeTableCount"/> tables, and a struck slot
/// is one where it offers none. A test holds the two together for every listed time.
/// </para>
/// <para>
/// Like the single-slot read it is anonymous and carries no guest names and no money.
/// </para>
/// </remarks>
public sealed record BranchDaySlots
{
    public required Guid BranchId { get; init; }

    /// <summary>The local date asked about, as <c>YYYY-MM-DD</c>.</summary>
    public required DateOnly Date { get; init; }

    public required int PartySize { get; init; }

    /// <summary>Minutes between listed start times. Always <see cref="DaySlotStep.Minutes"/> - fifteen.</summary>
    public required int StepMinutes { get; init; }

    /// <summary>
    /// Why the whole day is refused, when it is: the branch is shut all day
    /// (<c>OutsideOpeningHours</c>, and no slots), the day is past or every time left today is too
    /// soon (<c>LeadTimeTooShort</c>), or it is further out than the branch books
    /// (<c>OutsideBookingWindow</c>). Null when at least one time could be judged on its tables.
    /// </summary>
    /// <remarks>
    /// Said once at the top, so the picker can say "we are closed on Mondays" instead of drawing a
    /// day of struck chips.
    /// </remarks>
    public ReservationRejectionReason? UnavailableReason { get; init; }

    /// <summary>
    /// The date's special opening day - a holiday, a closure, different hours - when it is one; absent on an
    /// ordinary day. The slots already follow it: a closed one lists no times of its own.
    /// </summary>
    /// <remarks>So the picker can say "Closed for New Year" rather than only "closed".</remarks>
    public BranchSettings.SpecialOpeningDayView? SpecialDay { get; init; }

    /// <summary>
    /// Every start time on the day whose whole sitting fits one opening block, earliest first. A time
    /// the branch is shut for is left out rather than struck - it was never on offer.
    /// </summary>
    public required IReadOnlyList<DaySlot> Slots { get; init; }
}

/// <summary>One start time on the day, and whether a table is free for it.</summary>
public sealed record DaySlot
{
    /// <summary>The local wall-clock start, <c>HH:mm</c> - what the chip says.</summary>
    public required string Time { get; init; }

    /// <summary>The same start as an instant.</summary>
    public required DateTime StartUtc { get; init; }

    /// <summary>
    /// Whether at least one table can be booked for this party at this time: a bookable table pinned
    /// on a photo of its room that seats them, within the branch's seat-overhang limit, that the
    /// table's evening allows - free outright (<see cref="FreeTableCount"/>), or once the guest before
    /// agrees to leave in time (<see cref="RequestableTableCount"/>).
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// True when the only tables at this time are ones the guest before has to agree to free first:
    /// show the time as available, labelled "the guest before you will be asked" (or, tonight, the
    /// floor's short-notice wait). <see cref="RequiresApproval"/> is true with it.
    /// </summary>
    public bool IsRequestable { get; init; }

    /// <summary>How many tables at this time can be had once the guest before agrees to leave in time.</summary>
    public int RequestableTableCount { get; init; }

    /// <summary>
    /// When every table on offer at this time has to be free again before a full sitting is over -
    /// another booking follows it - the latest of those times; absent when at least one table gives a
    /// full sitting. "Until 21:25". The table the diner picks says its own, which they accept before
    /// booking.
    /// </summary>
    public DateTime? MustFreeByUtc { get; init; }

    /// <summary><see cref="MustFreeByUtc"/> on the branch's wall clock, <c>HH:mm</c>.</summary>
    public string? MustFreeByLocal { get; init; }

    /// <summary>
    /// True when a booking made at this time will land waiting for approval: the time is at short
    /// notice (today at the branch, or under six hours away - the floor decides, within fifteen
    /// minutes), the branch approves every booking by hand, or the party is over its threshold. Asked
    /// per time, so on tomorrow's picker the first few hours can say yes and the evening no. The same
    /// answer every table in the single-slot read gives.
    /// </summary>
    public required bool RequiresApproval { get; init; }

    /// <summary>
    /// How many tables the single-slot read offers at this time with nobody to ask first. Zero when
    /// struck, and when the only tables are requestable ones.
    /// </summary>
    public required int FreeTableCount { get; init; }

    /// <summary>
    /// At a branch that books by time only: how many more guests fit at the busiest moment of this time's
    /// sitting. Absent at a branch where diners choose a table, which answers in tables instead.
    /// </summary>
    public int? GuestsLeft { get; init; }

    /// <summary>
    /// Why nothing is free, when nothing is. <c>LeadTimeTooShort</c> or <c>OutsideBookingWindow</c>
    /// when the time itself is refused; otherwise the tables' reason - when tables that fit the party
    /// are taken, the commonest of <c>TableAlreadyBooked</c>, <c>TableCurrentlyOccupied</c>,
    /// <c>TableJustBooked</c>, <c>NextBookingTooSoon</c> and <c>EarlierGuestBeingAsked</c>; else the
    /// commonest reason across the tables, e.g. <c>PartyExceedsCapacity</c> when no table seats them.
    /// Null when available.
    /// </summary>
    public ReservationRejectionReason? UnavailableReason { get; init; }
}

/// <summary>The time picker's grid.</summary>
public static class DaySlotStep
{
    /// <summary>
    /// Every quarter of an hour from midnight - 00, 15, 30 and 45 - the minutes the diner's wheel and
    /// the public page's list offer. It was every half hour until the booking-times change.
    /// </summary>
    public const int Minutes = 15;
}
