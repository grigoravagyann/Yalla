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
        int walkInHoldbackMinutes = 30)
    {
        WalkInHoldbackMinutes = NotNegative(walkInHoldbackMinutes, nameof(walkInHoldbackMinutes));

        TurnTimeMinutes = Positive(turnTimeMinutes, nameof(turnTimeMinutes));
        BufferMinutes = NotNegative(bufferMinutes, nameof(bufferMinutes));
        GraceMinutes = NotNegative(graceMinutes, nameof(graceMinutes));
        LateNudgeAfterMinutes = NotNegative(lateNudgeAfterMinutes, nameof(lateNudgeAfterMinutes));
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
    /// lingers over one coffee, a restaurant sits a tighter 90-minute cover.
    /// </summary>
    public static ReservationPolicy DefaultFor(VenueType venueType) => new(
        turnTimeMinutes: venueType == VenueType.Cafe ? 120 : 90,
        bufferMinutes: 15,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: 30,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: 120,
        autoConfirm: true,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: 2,
        approvalRequiredAbovePartySize: 8,
        walkInHoldbackMinutes: 30);

    private static int Positive(int value, string paramName) =>
        value <= 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must be greater than zero.")
            : value;

    private static int NotNegative(int value, string paramName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must not be negative.")
            : value;
}
