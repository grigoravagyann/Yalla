namespace Yalla.Domain.Occupancy;

/// <summary>
/// Base for every refusal of a booking request.
/// </summary>
/// <remarks>
/// <para>
/// One derived type per rule, each with its own <see cref="Code"/> and its own
/// <see cref="Details"/>. The alternative - a single exception carrying a message - forces every
/// client to either show server prose or match on English, and both are how a booking screen ends
/// up saying "invalid booking" to somebody who only needed to pick a different table.
/// </para>
/// <para>
/// The base exists so the API maps the whole family in one place and answers with the derived
/// type's own code, rather than growing a mapper entry per rule. Adding a rule is then a new class
/// and a new constant, and nothing else changes.
/// </para>
/// <para>
/// These are all <b>422</b>: the request was understood and is semantically wrong. Losing a race
/// for a table is not one of these - that is <c>TableAlreadyBookedException</c> and a 409, because
/// the request was fine and the world moved.
/// </para>
/// </remarks>
public abstract class ReservationRejectedException : Exception
{
    protected ReservationRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>Which rule refused it.</summary>
    public abstract ReservationRejectionReason Reason { get; }

    /// <summary>The stable slug the client branches on.</summary>
    public abstract string Code { get; }

    /// <summary>
    /// The numbers behind the refusal, so the client can say "this table seats 4" rather than
    /// repeating the server's sentence.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object?> Details =>
        new Dictionary<string, object?> { ["reason"] = Reason.ToString() };

    /// <summary>Builds <see cref="Details"/> with the reason already in it.</summary>
    protected Dictionary<string, object?> DetailsWith(params (string Key, object? Value)[] values)
    {
        var details = new Dictionary<string, object?> { ["reason"] = Reason.ToString() };

        foreach (var (key, value) in values)
        {
            details[key] = value;
        }

        return details;
    }
}

/// <summary>The slot starts sooner than the branch's minimum lead time.</summary>
public sealed class LeadTimeTooShortException(DateTime startUtc, DateTime earliestStartUtc, int minLeadMinutes)
    : ReservationRejectedException(
        $"That table can only be booked {minLeadMinutes} minutes ahead; the earliest slot now is "
        + $"{earliestStartUtc:HH:mm} UTC.")
{
    public const string ErrorCode = "reservation-lead-time-too-short";

    public DateTime StartUtc { get; } = startUtc;

    public DateTime EarliestStartUtc { get; } = earliestStartUtc;

    public int MinLeadMinutes { get; } = minLeadMinutes;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.LeadTimeTooShort;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("requestedStartUtc", StartUtc),
        ("earliestStartUtc", EarliestStartUtc),
        ("minLeadMinutes", MinLeadMinutes));
}

/// <summary>The date is further ahead than the branch takes bookings.</summary>
public sealed class OutsideBookingWindowException(DateOnly localDate, DateOnly lastBookableDate, int bookingWindowDays)
    : ReservationRejectedException(
        $"This branch takes bookings up to {bookingWindowDays} days ahead, so no later than "
        + $"{lastBookableDate:yyyy-MM-dd}.")
{
    public const string ErrorCode = "reservation-outside-booking-window";

    public DateOnly LocalDate { get; } = localDate;

    public DateOnly LastBookableDate { get; } = lastBookableDate;

    public int BookingWindowDays { get; } = bookingWindowDays;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.OutsideBookingWindow;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("requestedDate", LocalDate.ToString("yyyy-MM-dd")),
        ("lastBookableDate", LastBookableDate.ToString("yyyy-MM-dd")),
        ("bookingWindowDays", BookingWindowDays));
}

/// <summary>The sitting does not fit inside a single opening block for the branch's local day.</summary>
public sealed class OutsideOpeningHoursException(DateOnly localDate, TimeOnly localStart, TimeOnly localEnd)
    : ReservationRejectedException(
        $"The branch is not open for a {localStart:HH\\:mm}-{localEnd:HH\\:mm} sitting on {localDate:yyyy-MM-dd}.")
{
    public const string ErrorCode = "reservation-outside-opening-hours";

    public DateOnly LocalDate { get; } = localDate;

    public TimeOnly LocalStart { get; } = localStart;

    public TimeOnly LocalEnd { get; } = localEnd;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.OutsideOpeningHours;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("localDate", LocalDate.ToString("yyyy-MM-dd")),
        ("localStartTime", LocalStart.ToString("HH\\:mm")),
        ("localEndTime", LocalEnd.ToString("HH\\:mm")));
}

/// <summary>More guests than the table has seats.</summary>
public sealed class PartyExceedsTableCapacityException(Guid tableId, string tableLabel, int partySize, int seats)
    : ReservationRejectedException($"Table {tableLabel} seats {seats}; the party is {partySize}.")
{
    public const string ErrorCode = "reservation-party-exceeds-capacity";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public int PartySize { get; } = partySize;

    public int Seats { get; } = seats;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.PartyExceedsCapacity;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("tableId", TableId), ("tableLabel", TableLabel), ("partySize", PartySize), ("seats", Seats));
}

/// <summary>The party would leave more seats empty than the branch allows.</summary>
public sealed class SeatOverhangExceededException(
    Guid tableId,
    string tableLabel,
    int partySize,
    int seats,
    int maxSeatOverhang)
    : ReservationRejectedException(
        $"Table {tableLabel} seats {seats} and would leave {seats - partySize} empty; this branch "
        + $"allows at most {maxSeatOverhang}.")
{
    public const string ErrorCode = "reservation-seat-overhang-exceeded";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public int PartySize { get; } = partySize;

    public int Seats { get; } = seats;

    public int MaxSeatOverhang { get; } = maxSeatOverhang;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.SeatOverhangExceeded;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("tableId", TableId),
        ("tableLabel", TableLabel),
        ("partySize", PartySize),
        ("seats", Seats),
        ("seatsLeftEmpty", Seats - PartySize),
        ("maxSeatOverhang", MaxSeatOverhang));
}

/// <summary>The table never takes bookings - a bar stool, a counter seat.</summary>
public sealed class TableNotBookableException(Guid tableId, string tableLabel)
    : ReservationRejectedException($"Table {tableLabel} does not take bookings.")
{
    public const string ErrorCode = "reservation-table-not-bookable";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.TableNotBookable;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details =>
        DetailsWith(("tableId", TableId), ("tableLabel", TableLabel));
}

/// <summary>The table is withdrawn from service.</summary>
public sealed class TableOutOfServiceException(Guid tableId, string tableLabel)
    : ReservationRejectedException($"Table {tableLabel} is out of service.")
{
    public const string ErrorCode = "reservation-table-out-of-service";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.TableOutOfService;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details =>
        DetailsWith(("tableId", TableId), ("tableLabel", TableLabel));
}

/// <summary>
/// The wall-clock time the diner picked does not exist in the branch's zone.
/// </summary>
/// <remarks>
/// The hour a spring-forward skips. Armenia does not currently observe daylight saving, but the
/// branch's zone is a setting and the next customer may be in one that does - so this is answered
/// with a rule rather than left to a <see cref="ArgumentException"/> escaping from
/// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> as a 500.
/// </remarks>
public sealed class LocalTimeDoesNotExistException(DateOnly localDate, TimeOnly localTime, string timeZoneId)
    : ReservationRejectedException(
        $"{localDate:yyyy-MM-dd} {localTime:HH\\:mm} does not exist in {timeZoneId}: the clocks go "
        + "forward over it. Pick a time either side of the change.")
{
    public const string ErrorCode = "reservation-local-time-does-not-exist";

    public DateOnly LocalDate { get; } = localDate;

    public TimeOnly LocalTime { get; } = localTime;

    public string TimeZoneId { get; } = timeZoneId;

    public override ReservationRejectionReason Reason => ReservationRejectionReason.LocalTimeDoesNotExist;

    public override string Code => ErrorCode;

    public override IReadOnlyDictionary<string, object?> Details => DetailsWith(
        ("localDate", LocalDate.ToString("yyyy-MM-dd")),
        ("localTime", LocalTime.ToString("HH\\:mm")),
        ("timeZoneId", TimeZoneId));
}
