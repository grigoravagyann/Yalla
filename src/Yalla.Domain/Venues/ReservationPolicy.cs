using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// Every reservation rule for one branch, as data.
/// </summary>
/// <remarks>
/// This is an owned entity persisted as extra columns on the Branches table: a branch always has
/// exactly one policy and it is never queried on its own. Each value ships with a sensible
/// default (see <see cref="DefaultFor"/>) that the owner can override per branch. None of these
/// numbers is ever a constant in code, because "how long do we hold a table?" has a different
/// answer in a breakfast cafe and a tasting-menu restaurant.
/// </remarks>
public sealed class ReservationPolicy
{
    /// <summary>How long a party is expected to keep the table. Derives <c>Reservation.EndUtc</c>.</summary>
    public int TurnTimeMinutes { get; private set; }

    /// <summary>
    /// Turnaround padding applied around a booking when testing for overlaps. Deliberately not
    /// baked into <c>EndUtc</c>, so the interval the diner sees stays the interval they booked.
    /// </summary>
    public int BufferMinutes { get; private set; }

    /// <summary>How long past the start time a late party keeps its table before it may be released.</summary>
    public int GraceMinutes { get; private set; }

    /// <summary>How long past the start time before the diner is nudged to confirm they are coming.</summary>
    public int LateNudgeAfterMinutes { get; private set; }

    /// <summary>
    /// How long before a booking the diner is reminded of it.
    /// </summary>
    /// <remarks>
    /// Three hours by default: late enough that the day's plans are settled, early enough that
    /// cancelling still gives the venue time to sell the table. The reminder is the point of the
    /// whole feature - <b>cancelling has to be easier than not showing up</b> - so this is the number
    /// that decides whether the no-show rate moves.
    /// </remarks>
    public int ReminderHoursBefore { get; private set; }

    /// <summary>Length of one extension staff may grant to a late party.</summary>
    public int GraceExtensionMinutes { get; private set; }

    /// <summary>How far ahead of a slot a booking must be made.</summary>
    public int MinLeadMinutes { get; private set; }

    /// <summary>How many days into the future the branch takes bookings.</summary>
    public int BookingWindowDays { get; private set; }

    /// <summary>How long before the start time a diner may still cancel without penalty.</summary>
    public int CancellationDeadlineMinutes { get; private set; }

    /// <summary>
    /// When false, bookings land as <see cref="ReservationStatus.PendingApproval"/> for staff
    /// to accept or decline.
    /// </summary>
    public bool AutoConfirm { get; private set; }

    /// <summary>Service charge added to a tab, as a percentage. Mapped as <c>decimal(5,2)</c>.</summary>
    public decimal ServiceChargePercent { get; private set; }

    /// <summary>Whether menu prices already contain VAT.</summary>
    public bool PricesIncludeVat { get; private set; }

    /// <summary>
    /// How many seats a party may leave empty, so a couple cannot book the eight-seater.
    /// Null means no limit.
    /// </summary>
    public int? MaxSeatOverhang { get; private set; }

    /// <summary>Party size above which a booking needs staff approval. Null means never.</summary>
    public int? ApprovalRequiredAbovePartySize { get; private set; }

    /// <summary>
    /// How close to a confirmed booking a walk-in may still be seated at that table before staff
    /// are warned.
    /// </summary>
    /// <remarks>
    /// A warning, never a block. The waiter can see that the party is two people who want a quick
    /// coffee, and that the booking is forty minutes away; the system cannot. What it can do is
    /// make sure nobody seats them by accident.
    /// </remarks>
    public int WalkInHoldbackMinutes { get; private set; }

    /// <summary>
    /// Whether a booking whose booker never proved their phone number waits for staff approval
    /// (<c>ApprovalTrigger.UnverifiedPhone</c>). On by default.
    /// </summary>
    /// <remarks>
    /// Booking without a proved number is allowed (decided 2026-10-20) - Telegram is the only proof, and
    /// not everybody has it - but such a number is worth nothing to a reminder or a no-show record, so
    /// by default the venue looks at the booking before promising the table. A venue happy to take them
    /// on trust switches this off; the booking is then flagged and nothing more.
    /// </remarks>
    public bool UnverifiedBookingsNeedApproval { get; private set; } = true;

    /// <summary>
    /// The largest party a booking with an unproved number may be for. Six by default: a family table,
    /// where a phantom booking for twelve would empty a room. <b>Zero</b>: the branch takes no booking
    /// without a verified number (<see cref="AcceptsUnverifiedBookings"/>).
    /// </summary>
    public int UnverifiedMaxPartySize { get; private set; } = DefaultUnverifiedMaxPartySize;

    /// <summary>A new branch lets an unproved number book for at most this many.</summary>
    public const int DefaultUnverifiedMaxPartySize = 6;

    /// <summary>Whether a booker with no proved number may book here at all: the cap above zero.</summary>
    public bool AcceptsUnverifiedBookings => UnverifiedMaxPartySize > 0;

    /// <summary>
    /// Whether a tab opened by a QR scan with no proved phone number behind it waits for a waiter to admit it
    /// before anybody on it can order (<c>TabAdmission</c>). On by default - the user's decision of 2026-10-20:
    /// a photographed code can be scanned from anywhere, and the waiter can see who is sitting there.
    /// </summary>
    /// <remarks>
    /// Off, a scan opens a tab that orders at once, as every scan did before admission
    /// (<c>TabAdmissionReason.NotRequired</c>). A switch per branch because admission needs a tablet that
    /// can admit: a branch whose floor runs an older tablet build turns it off until the new one is on its
    /// devices.
    /// </remarks>
    public bool UnverifiedScansNeedAdmission { get; private set; } = true;

    private ReservationPolicy()
    {
    }

    public ReservationPolicy(
        int turnTimeMinutes,
        int bufferMinutes,
        int graceMinutes,
        int lateNudgeAfterMinutes,
        int graceExtensionMinutes,
        int minLeadMinutes,
        int bookingWindowDays,
        int cancellationDeadlineMinutes,
        bool autoConfirm,
        decimal serviceChargePercent,
        bool pricesIncludeVat,
        int? maxSeatOverhang,
        int? approvalRequiredAbovePartySize,
        int walkInHoldbackMinutes = 30,
        int reminderHoursBefore = 3,
        bool unverifiedBookingsNeedApproval = true,
        int unverifiedMaxPartySize = DefaultUnverifiedMaxPartySize,
        bool unverifiedScansNeedAdmission = true)
    {
        UnverifiedBookingsNeedApproval = unverifiedBookingsNeedApproval;
        UnverifiedScansNeedAdmission = unverifiedScansNeedAdmission;
        UnverifiedMaxPartySize = NotNegative(unverifiedMaxPartySize, nameof(unverifiedMaxPartySize));

        WalkInHoldbackMinutes = NotNegative(walkInHoldbackMinutes, nameof(walkInHoldbackMinutes));

        TurnTimeMinutes = Positive(turnTimeMinutes, nameof(turnTimeMinutes));
        BufferMinutes = NotNegative(bufferMinutes, nameof(bufferMinutes));
        GraceMinutes = NotNegative(graceMinutes, nameof(graceMinutes));
        LateNudgeAfterMinutes = NotNegative(lateNudgeAfterMinutes, nameof(lateNudgeAfterMinutes));
        ReminderHoursBefore = NotNegative(reminderHoursBefore, nameof(reminderHoursBefore));
        GraceExtensionMinutes = NotNegative(graceExtensionMinutes, nameof(graceExtensionMinutes));
        MinLeadMinutes = NotNegative(minLeadMinutes, nameof(minLeadMinutes));
        BookingWindowDays = Positive(bookingWindowDays, nameof(bookingWindowDays));
        CancellationDeadlineMinutes = NotNegative(cancellationDeadlineMinutes, nameof(cancellationDeadlineMinutes));
        AutoConfirm = autoConfirm;
        PricesIncludeVat = pricesIncludeVat;

        if (serviceChargePercent < 0m || serviceChargePercent > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceChargePercent),
                serviceChargePercent,
                "Service charge must be between 0 and 100 percent.");
        }

        ServiceChargePercent = decimal.Round(serviceChargePercent, 2);

        if (maxSeatOverhang is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSeatOverhang), maxSeatOverhang, "Seat overhang must not be negative.");
        }

        MaxSeatOverhang = maxSeatOverhang;

        if (approvalRequiredAbovePartySize is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approvalRequiredAbovePartySize),
                approvalRequiredAbovePartySize,
                "The approval threshold must be at least one guest.");
        }

        ApprovalRequiredAbovePartySize = approvalRequiredAbovePartySize;
    }

    /// <summary>
    /// The policy a new branch ships with. Only the turn time varies by venue type: a cafe guest
    /// lingers over one coffee and a bar table over a round or two, so both get two hours; a
    /// restaurant sits a tighter 90-minute cover.
    /// </summary>
    /// <remarks>
    /// Bookable from <see cref="DefaultMinLeadMinutes"/> minutes ahead, with
    /// <see cref="DefaultBufferMinutes"/> minutes of clearing time between two parties: the times a
    /// diner books tonight. Both were longer (30 and 15) until the booking-times change, whose
    /// migration moved every branch still on those two old defaults to these - see
    /// <c>docs/reservations.md</c>.
    /// </remarks>
    public static ReservationPolicy DefaultFor(VenueType venueType) => new(
        turnTimeMinutes: venueType switch
        {
            VenueType.Cafe => 120,
            VenueType.Bar => 120,
            _ => 90,
        },
        bufferMinutes: DefaultBufferMinutes,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        reminderHoursBefore: 3,
        graceExtensionMinutes: 10,
        minLeadMinutes: DefaultMinLeadMinutes,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: 120,
        autoConfirm: true,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: 2,
        approvalRequiredAbovePartySize: 8,
        walkInHoldbackMinutes: 30);

    /// <summary>A new branch takes bookings from this many minutes ahead.</summary>
    public const int DefaultMinLeadMinutes = 10;

    /// <summary>A new branch clears a table in this many minutes between two parties.</summary>
    public const int DefaultBufferMinutes = 5;

    private static int Positive(int value, string paramName) =>
        value <= 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must be greater than zero.")
            : value;

    private static int NotNegative(int value, string paramName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must not be negative.")
            : value;
}
