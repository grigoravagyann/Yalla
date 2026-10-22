using Yalla.Domain.Common;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// A guest blocked from booking for a while - three no-shows in three days (<see cref="NoShowBlockRules"/>).
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the proved <b>account</b> and its proved <b>number</b>: a booking by either is refused until
/// <see cref="BlockedUntilUtc"/>, so deleting the account and proving the number again on a new one does not
/// escape it. Only bookings the account itself made with its proved number can start one - nothing a stranger
/// could type in. Written by <c>ReservationService.ReleaseAsync</c> under the account's lock, in the transaction
/// that records the third no-show.
/// </para>
/// <para>
/// Never deleted by the application: it is the record of why the guest's bookings were cancelled. A platform
/// admin lifts one by setting <see cref="LiftedAtUtc"/> (see <c>docs/reservations.md</c>, "No-show block"); a lift
/// does not bring back the bookings it cancelled.
/// </para>
/// </remarks>
public sealed class BookingBlock : Entity
{
    private BookingBlock()
    {
    }

    /// <summary>A block for no-shows, from <paramref name="atUtc"/> for <see cref="NoShowBlockRules.BlockDays"/> days.</summary>
    public BookingBlock(Guid dinerUserId, string? phoneE164, DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));
        PhoneE164 = Guard.OptionalText(phoneE164, nameof(phoneE164), FieldLengths.Phone);
        BlockedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        BlockedUntilUtc = atUtc.AddDays(NoShowBlockRules.BlockDays);
        Reason = BookingBlockReason.NoShows;
        StampCreatedAt(atUtc);
    }

    /// <summary>The account blocked.</summary>
    public Guid DinerUserId { get; private set; }

    /// <summary>Its proved number when the block began (canonical E.164), also refused; null when it had none.</summary>
    public string? PhoneE164 { get; private set; }

    /// <summary>When the block began.</summary>
    public DateTime BlockedAtUtc { get; private set; }

    /// <summary>When the guest may book again.</summary>
    public DateTime BlockedUntilUtc { get; private set; }

    /// <summary>Why.</summary>
    public BookingBlockReason Reason { get; private set; }

    /// <summary>When it was lifted early, or null.</summary>
    public DateTime? LiftedAtUtc { get; private set; }

    /// <summary>Why it was lifted early, or null.</summary>
    public BookingBlockLiftCause? LiftedBecause { get; private set; }

    /// <summary>Who lifted it: the platform admin, or the staff member who withdrew a no-show.</summary>
    public Guid? LiftedByStaffId { get; private set; }

    /// <summary>What they gave as the reason.</summary>
    public string? LiftReason { get; private set; }

    /// <summary>When it stopped refusing anything: lifted, or ended.</summary>
    public DateTime EndedAtUtc => LiftedAtUtc ?? BlockedUntilUtc;

    /// <summary>
    /// Lifts it now. Nothing it cancelled comes back. Lifting a block already lifted changes nothing.
    /// </summary>
    /// <returns>Whether this call lifted it.</returns>
    public bool Lift(DateTime atUtc, BookingBlockLiftCause because, Guid byStaffId, string? reason)
    {
        if (LiftedAtUtc is not null)
        {
            return false;
        }

        LiftedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        LiftedBecause = Guard.Defined(because, nameof(because));
        LiftedByStaffId = Guard.NotEmpty(byStaffId, nameof(byStaffId));
        LiftReason = Guard.OptionalText(reason, nameof(reason), FieldLengths.Reason);

        return true;
    }

    /// <summary>Whether it refuses a booking at <paramref name="nowUtc"/>.</summary>
    public bool IsInForceAt(DateTime nowUtc) => LiftedAtUtc is null && BlockedUntilUtc > nowUtc;
}

/// <summary>Why a block was lifted before it ended. Persisted as its number.</summary>
public enum BookingBlockLiftCause
{
    /// <summary>A platform admin lifted it, with a reason (audited <c>booking-block.lift</c>).</summary>
    PlatformAdmin = 1,

    /// <summary>Staff withdrew a no-show, and fewer than three were left to hold it.</summary>
    NoShowUndone = 2,
}

/// <summary>
/// The no-show cannot be withdrawn any more: its service day ended (05:00 the next morning, branch time).
/// 409 <c>no-show-undo-window-closed</c>, <c>context.undoableUntilUtc</c>.
/// </summary>
public sealed class NoShowUndoWindowClosedException(DateTime undoableUntilUtc)
    : DomainStateException(
        "This no-show can no longer be withdrawn: that service day is over. Ask Yalla support if it was a mistake.")
{
    public const string ErrorCode = "no-show-undo-window-closed";

    /// <summary>When the window closed.</summary>
    public DateTime UndoableUntilUtc { get; } = undoableUntilUtc;
}

/// <summary>
/// A booking a no-show block cancelled cannot be approved or rejected: 409 <c>reservation-cancelled-by-block</c>,
/// <c>context.reservationId</c> and <c>cancelledAtUtc</c> - not the generic "already answered".
/// </summary>
public sealed class ReservationCancelledByBlockException(Guid reservationId, DateTime? cancelledAtUtc)
    : DomainStateException(
        "This booking was cancelled automatically: its guest missed three bookings in three days and cannot book for a week.")
{
    public const string ErrorCode = "reservation-cancelled-by-block";

    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>When the block cancelled it.</summary>
    public DateTime? CancelledAtUtc { get; } = cancelledAtUtc;
}

/// <summary>Why a guest is blocked from booking. Persisted as its number.</summary>
public enum BookingBlockReason
{
    /// <summary>Three no-shows within three days - see <see cref="NoShowBlockRules"/>.</summary>
    NoShows = 1,
}

/// <summary>
/// Why the platform, not the guest nor the venue, cancelled a booking. Stored on the booking
/// (<c>Reservation.CancelledBecause</c>) beside the status, which is <c>CancelledByVenue</c> - it was not the
/// guest's choice, and it never counts in a guest record. Persisted as its number.
/// </summary>
public enum CancellationCause
{
    /// <summary>The guest was blocked for three no-shows in three days, and every booking not yet started went.</summary>
    NoShowBlock = 1,
}

/// <summary>
/// The guest is blocked from booking until <see cref="BlockedUntilUtc"/> - three no-shows in three days.
/// 403 <c>booking-blocked-no-shows</c>, <c>context.blockedUntilUtc</c>.
/// </summary>
/// <remarks>
/// Also the refusal of a takeaway order while the guest is blocked (a takeaway order not collected counts toward the
/// block): the same code and context, so the app says the same thing at the takeaway checkout as at a booking.
/// </remarks>
public sealed class BookingBlockedForNoShowsException(DateTime blockedUntilUtc, string? message = null)
    : Exception(
        message
        ?? $"You can't book until {blockedUntilUtc:yyyy-MM-dd HH:mm} UTC: three of your bookings in three days were "
        + "missed without cancelling.")
{
    public const string ErrorCode = "booking-blocked-no-shows";

    /// <summary>The sentence for a takeaway order refused by the block.</summary>
    public static string TakeawayMessage(DateTime blockedUntilUtc) =>
        $"You can't order until {blockedUntilUtc:yyyy-MM-dd HH:mm} UTC: three of your bookings or takeaway orders in "
        + "three days were missed.";

    /// <summary>When the guest may book again.</summary>
    public DateTime BlockedUntilUtc { get; } = blockedUntilUtc;
}
