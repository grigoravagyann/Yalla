using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// A member of staff booking a table for a guest by hand - over the phone, or at the door (2026-10-31, "Ձեռքով
/// ամրագրում"). The booking is linked to whoever took it, and they follow whether the guest comes.
/// </summary>
/// <remarks>
/// <para>
/// The date and time are <b>local to the branch</b>, as on <see cref="CreateReservationCommand"/>. No channel: a
/// booking made here is <see cref="ReservationChannel.Staff"/>, with no diner account behind it and no number of its
/// own to key the per-number rules on - the venue's choice, which those rules neither count nor refuse.
/// </para>
/// <para>
/// No approval and no lead time: the person on the floor has judged the room. The table's evening still refuses
/// an overlap, a table just booked, one about to be, or a party sitting there - the staff pick another time or
/// table.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch, from the route.</param>
/// <param name="TableId">
/// The table, at a branch where tables are chosen (<see cref="BookingMode.ChooseTable"/>): any in-service table, pinned
/// or not. Null at a branch that books by time only - the waiter chooses the table when the party arrives.
/// </param>
/// <param name="LocalDate">The local calendar date at the branch.</param>
/// <param name="LocalTime">The local wall-clock start time at the branch.</param>
/// <param name="PartySize">How many are coming.</param>
/// <param name="GuestName">Who to ask for at the door.</param>
/// <param name="GuestPhone">The number the taker calls - required, it is what the follow-up is for.</param>
/// <param name="ClientCommandId">The device's own id for this booking: a retry answers the booking it made.</param>
/// <param name="Note">What the guest asked for - trimmed, blank means none, at most 500 characters.</param>
public sealed record StaffCreateReservationCommand(
    Guid BranchId,
    Guid? TableId,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    int PartySize,
    string GuestName,
    string GuestPhone,
    Guid ClientCommandId,
    string? Note = null);

/// <summary>A member of staff records that the guest said they are coming - over the phone, to the taker.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="ClientCommandId">The device's own id for this answer: a retry answers the booking as it stands.</param>
public sealed record MarkGuestComingCommand(Guid ReservationId, Guid ClientCommandId);

/// <summary>What the staff booking sheet asks: which start times on one day have a table for this party, by staff rules.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="LocalDate">The local date at the branch.</param>
/// <param name="PartySize">How many are coming.</param>
public sealed record StaffDaySlotsRequest(Guid BranchId, DateOnly LocalDate, int PartySize);

/// <summary>
/// One day at a branch for the staff booking sheet: every quarter of an hour, and for each the in-service tables that
/// seat the party and are free then - or, at a branch that books by time only, how many guests still fit.
/// </summary>
/// <remarks>
/// The public day-slots read (<see cref="BranchDaySlots"/>) with staff rules: every in-service table, pinned on a photo
/// or not, bookable from the app or not; no lead time (a time still ahead is offered); no diner-visibility gate. The
/// booking window, the opening hours and the table's evening are the same rules the staff booking is judged by, so a
/// table listed here is one <c>createStaffReservation</c> accepts at that time.
/// </remarks>
public sealed record StaffDaySlots
{
    public required Guid BranchId { get; init; }

    /// <summary>The local date asked about, as <c>YYYY-MM-DD</c>.</summary>
    public required DateOnly Date { get; init; }

    public required int PartySize { get; init; }

    /// <summary>How the branch books: 1 a table is chosen (each slot lists tables), 2 by time only (each slot says how many fit).</summary>
    public required BookingMode BookingMode { get; init; }

    /// <summary>Minutes between listed start times - fifteen.</summary>
    public required int StepMinutes { get; init; }

    /// <summary>
    /// Why the whole day is refused, when it is: shut all day (<c>OutsideOpeningHours</c>, no slots), already over
    /// (<c>LeadTimeTooShort</c>: every time on it has passed), or beyond the branch's booking window
    /// (<c>OutsideBookingWindow</c>). Null when at least one time could be judged.
    /// </summary>
    public ReservationRejectionReason? UnavailableReason { get; init; }

    /// <summary>The date's special opening day, when it is one; absent on an ordinary day.</summary>
    public BranchSettings.SpecialOpeningDayView? SpecialDay { get; init; }

    /// <summary>Every start time whose whole sitting fits one opening block, earliest first.</summary>
    public required IReadOnlyList<StaffDaySlot> Slots { get; init; }
}

/// <summary>One start time on the day for the staff sheet: the tables free for the party then, or how many guests fit.</summary>
public sealed record StaffDaySlot
{
    /// <summary>The local wall-clock start, <c>HH:mm</c> - what the chip says.</summary>
    public required string Time { get; init; }

    /// <summary>The same start as an instant.</summary>
    public required DateTime StartUtc { get; init; }

    /// <summary>At least one table takes the party then - or, by time only, the party fits.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Why nothing is free, when nothing is: <c>LeadTimeTooShort</c> for a time already past, <c>OutsideBookingWindow</c>,
    /// <c>TimeFull</c> at a branch that books by time only, else the commonest reason across the tables. Null when available.
    /// </summary>
    public ReservationRejectionReason? UnavailableReason { get; init; }

    /// <summary>At a branch that books by time only: how many more guests fit at the busiest moment of the sitting. Absent otherwise.</summary>
    public int? GuestsLeft { get; init; }

    /// <summary>
    /// The in-service tables that seat the party and whose evening allows this start - what the sheet offers as table
    /// chips. Empty when none, and always empty at a branch that books by time only.
    /// </summary>
    public required IReadOnlyList<StaffSlotTable> Tables { get; init; }
}

/// <summary>A table the staff sheet offers at one start.</summary>
/// <param name="TableId">The table.</param>
/// <param name="Label">Its label, as printed on the floor.</param>
/// <param name="Seats">How many it seats.</param>
/// <param name="RoomId">The room it is in, or null.</param>
/// <param name="RoomName">The room's name, or null.</param>
/// <param name="EarlierGuestMayStillBeThere">
/// The booking before it on the table may still be at the table at this start (the diner's read would ask that guest
/// first); the floor judges it. False when the table is free outright.
/// </param>
/// <param name="MustFreeByUtc">When a full sitting would run into the table's next booking: the time the party must have left by. Null otherwise.</param>
/// <param name="MustFreeByLocal"><paramref name="MustFreeByUtc"/> on the branch's wall clock, <c>HH:mm</c>.</param>
public sealed record StaffSlotTable(
    Guid TableId,
    string Label,
    int Seats,
    Guid? RoomId,
    string? RoomName,
    bool EarlierGuestMayStillBeThere,
    DateTime? MustFreeByUtc,
    string? MustFreeByLocal);

/// <summary>
/// A staff-taken booking whose guest should be called now: from thirty minutes before its start until its grace runs
/// out, while the guest has not said they are coming. The floor's "call the guest" card.
/// </summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="GuestName">Who booked.</param>
/// <param name="GuestPhone">The number to call. Empty for a platform admin.</param>
/// <param name="PartySize">How many.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="LocalDate">The branch's calendar date.</param>
/// <param name="LocalStartTime">The branch's wall-clock start.</param>
/// <param name="TableId">The table, or null for a booking by time only not yet seated.</param>
/// <param name="TableLabel">Its label, or null.</param>
/// <param name="Note">What the guest asked for, or null.</param>
/// <param name="TakenByStaffId">Who took the booking.</param>
/// <param name="TakenByName">Their name now.</param>
/// <param name="IsMine">The caller took it.</param>
public sealed record BookingToCall(
    Guid ReservationId,
    string Code,
    string GuestName,
    string GuestPhone,
    int PartySize,
    DateTime StartUtc,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    Guid? TableId,
    string? TableLabel,
    string? Note,
    Guid TakenByStaffId,
    string TakenByName,
    bool IsMine);

/// <summary>When the taker of a staff booking is told to call the guest, and the floor's to-call list starts showing it.</summary>
public static class BookingToCallRules
{
    /// <summary>Thirty minutes before the start - when every booking is asked "are you on your way?" too.</summary>
    public const int MinutesBefore = 30;

    /// <summary>When the taker is to be told: the start less <see cref="MinutesBefore"/>.</summary>
    public static DateTime CallAtUtc(DateTime startUtc) => startUtc.AddMinutes(-MinutesBefore);
}

/// <summary>
/// Bookings a member of staff takes for a guest, and the follow-up that is theirs: the sheet's day slots, the booking,
/// the list of guests to call before their time, and the guest's "coming" recorded over the phone.
/// </summary>
/// <remarks>
/// The floor's identity throughout: a waiter or above at the branch (<c>FloorStaffGuard</c>), from the stored row - an
/// owner or general manager at any branch of their venue, a platform admin anywhere (with the guests' numbers blank).
/// </remarks>
public interface IStaffBookingService
{
    /// <summary>
    /// Books for a guest as the acting staff member: <c>Confirmed</c> at once, linked to the taker, with no diner
    /// reminders and no manage link. A retry with the same <c>ClientCommandId</c> answers the booking it made.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this branch.</exception>
    /// <exception cref="ReservationRejectedException">The booking window, the opening hours or the table's own rules refused it.</exception>
    /// <exception cref="TableAlreadyBookedException">The table's evening refuses the time - overlap, just booked, next too soon.</exception>
    /// <exception cref="TableCurrentlyOccupiedException">A party sitting there now runs into the slot.</exception>
    /// <exception cref="TimeFullException">By time only: no room for that many then.</exception>
    /// <exception cref="ReservationLockTimeoutException">The lock could not be had in time. Retryable.</exception>
    Task<ReservationView> CreateAsync(StaffCreateReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>The sheet's day: which start times have a table for the party, by staff rules.</summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this branch.</exception>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<StaffDaySlots> GetDaySlotsAsync(StaffDaySlotsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The branch's staff-taken bookings whose guest should be called now: confirmed, from thirty minutes before the
    /// start until the start plus the branch's grace (or the extended hold, when later), the guest not yet said to be
    /// coming. The caller's own first, then by start.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this branch.</exception>
    Task<IReadOnlyList<BookingToCall>> ListToCallAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the guest said they are coming, as the acting staff member: the booking's "on my way" with who
    /// recorded it, from two hours before the start. A retry with the same <c>ClientCommandId</c>, or a second answer,
    /// answers the booking as it stands.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at the booking's branch.</exception>
    /// <exception cref="KeyNotFoundException">No such booking.</exception>
    /// <exception cref="OnMyWayNotOpenException">Not confirmed or seated, more than two hours before the start, or over.</exception>
    Task<ReservationView> MarkGuestComingAsync(MarkGuestComingCommand command, CancellationToken cancellationToken = default);
}
