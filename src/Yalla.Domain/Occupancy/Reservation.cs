using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// A booking: a promise that one specific table is held for one party over one interval.
/// </summary>
/// <remarks>
/// <para>
/// A reservation is an <b>interval</b>, not a point in time. <see cref="EndUtc"/> is derived at
/// creation from the branch's <see cref="ReservationPolicy.TurnTimeMinutes"/>. Without an end,
/// two bookings on the same table cannot be tested for overlap at all - the schema would permit
/// double-booking and no amount of application code could detect it.
/// </para>
/// <para>
/// The branch's <see cref="ReservationPolicy.BufferMinutes"/> is applied when <i>checking</i>
/// overlaps, not baked into <see cref="EndUtc"/>: the interval stored here is the interval the
/// diner booked and sees, while the turnaround padding is an operational concern that the owner
/// can change tomorrow without rewriting history.
/// </para>
/// <para>
/// A reservation may never become a tab - a no-show produces no ordering at all - and a tab
/// usually has no reservation. The two meet, if they meet, at <see cref="TableSession"/>.
/// </para>
/// </remarks>
public sealed class Reservation : Entity
{
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DiningTableId { get; private set; }

    public DiningTable DiningTable { get; private set; } = null!;

    /// <summary>
    /// The diner's account, when they booked from the app. Null for a phone booking taken by
    /// staff. No navigation property: identity is a later module, so this is an opaque reference.
    /// </summary>
    public Guid? DinerUserId { get; private set; }

    public string GuestName { get; private set; } = null!;

    public string GuestPhone { get; private set; } = null!;

    public int PartySize { get; private set; }

    /// <summary>Start of the booked interval, UTC.</summary>
    public DateTime StartUtc { get; private set; }

    /// <summary>
    /// End of the booked interval, UTC. Derived from the branch turn time at creation.
    /// </summary>
    public DateTime EndUtc { get; private set; }

    /// <summary>
    /// The local calendar date of the booking, as the diner reads it off their confirmation.
    /// Stored as wall-clock alongside <see cref="StartUtc"/> rather than recomputed, so a change
    /// to the branch time zone can never move an existing booking on the diner's screen.
    /// </summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>The local wall-clock start time, e.g. 19:30. See <see cref="LocalDate"/>.</summary>
    public TimeOnly LocalStartTime { get; private set; }

    public ReservationStatus Status { get; private set; }

    /// <summary>Short human-readable code the diner quotes at the door. Unique.</summary>
    public string Code { get; private set; } = null!;

    public DateTime? ConfirmedAtUtc { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>
    /// While a diner is completing a booking the table is held briefly; past this instant the
    /// hold lapses and the table returns to <see cref="TableStatus.Free"/>.
    /// </summary>
    public DateTime? HoldExpiresAtUtc { get; private set; }

    /// <summary>
    /// How many times staff have already extended grace for this late party, so "just five more
    /// minutes" cannot be granted indefinitely.
    /// </summary>
    public int GraceExtensionsUsed { get; private set; }

    /// <summary>Advisory hint from the diner. Does not affect <see cref="EndUtc"/>.</summary>
    public StayHint? StayHint { get; private set; }

    /// <summary>
    /// Optimistic concurrency token: seating, cancelling and releasing a late booking all race
    /// with each other across the diner and staff apps.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private Reservation()
    {
    }

    private Reservation(
        Guid branchId,
        Guid diningTableId,
        DateTime startUtc,
        DateTime endUtc,
        DateOnly localDate,
        TimeOnly localStartTime,
        int partySize,
        string guestName,
        string guestPhone,
        string code,
        ReservationStatus status,
        Guid? dinerUserId,
        StayHint? stayHint,
        DateTime? holdExpiresAtUtc)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        StartUtc = Guard.NotLocalTime(startUtc, nameof(startUtc));
        EndUtc = Guard.NotLocalTime(endUtc, nameof(endUtc));

        if (endUtc <= startUtc)
        {
            throw new ArgumentException("A reservation must end after it starts.", nameof(endUtc));
        }

        LocalDate = localDate;
        LocalStartTime = localStartTime;
        PartySize = Guard.Positive(partySize, nameof(partySize));
        GuestName = Guard.NotBlank(guestName, nameof(guestName), FieldLengths.PersonName);
        GuestPhone = Guard.NotBlank(guestPhone, nameof(guestPhone), FieldLengths.Phone);
        Code = Guard.NotBlank(code, nameof(code), FieldLengths.ReservationCode).ToUpperInvariant();
        Status = status;
        DinerUserId = dinerUserId;
        StayHint = stayHint is null ? null : Guard.Defined(stayHint.Value, nameof(stayHint));
        HoldExpiresAtUtc = holdExpiresAtUtc is null
            ? null
            : Guard.NotLocalTime(holdExpiresAtUtc.Value, nameof(holdExpiresAtUtc));
    }

    /// <summary>
    /// Books a table. <paramref name="policy"/> supplies the turn time that fixes
    /// <see cref="EndUtc"/>, which is why a reservation cannot be created without knowing which
    /// branch it belongs to.
    /// </summary>
    /// <remarks>
    /// <c>initialStatus</c> is <see cref="ReservationStatus.Confirmed"/>, or
    /// <see cref="ReservationStatus.PendingApproval"/> when the branch does not auto-confirm or
    /// the party is over its approval threshold. Deciding between the two is the caller's job; a
    /// reservation simply refuses to start life in any other state.
    /// </remarks>
    public static Reservation Create(
        Guid branchId,
        Guid diningTableId,
        DateTime startUtc,
        DateOnly localDate,
        TimeOnly localStartTime,
        int partySize,
        string guestName,
        string guestPhone,
        string code,
        ReservationPolicy policy,
        ReservationStatus initialStatus = ReservationStatus.Confirmed,
        Guid? dinerUserId = null,
        StayHint? stayHint = null,
        DateTime? holdExpiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (initialStatus is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialStatus),
                initialStatus,
                "A new reservation must start as Confirmed or PendingApproval.");
        }

        return new Reservation(
            branchId,
            diningTableId,
            startUtc,
            startUtc.AddMinutes(policy.TurnTimeMinutes),
            localDate,
            localStartTime,
            partySize,
            guestName,
            guestPhone,
            code,
            initialStatus,
            dinerUserId,
            stayHint,
            holdExpiresAtUtc);
    }
}
