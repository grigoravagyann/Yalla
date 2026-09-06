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
    /// Whether the cancellation arrived past the branch's <c>CancellationDeadlineMinutes</c>.
    /// </summary>
    /// <remarks>
    /// A late cancellation is never refused - it is far better than a no-show, and a diner who
    /// cannot cancel simply does not turn up - but the venue lost the slot too late to resell it,
    /// so the fact is recorded. Stored rather than derived because the deadline is a setting: an
    /// owner who shortens it next month must not retroactively make last month's cancellations
    /// late.
    /// </remarks>
    public bool CancelledAfterDeadline { get; private set; }

    /// <summary>
    /// The caller's own id for the command that created this booking. Unique across the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same guarantee as <c>TableStateChange.ClientCommandId</c>, for the same reason and by
    /// the same mechanism: a diner on a patchy mobile connection taps Book, sees nothing happen,
    /// and taps again. Without this the second tap is a second table for the same party at the
    /// same time - and the venue loses a cover it could have sold.
    /// </para>
    /// <para>
    /// The unique index is the real guarantee, not a check-then-insert in the service, because two
    /// retries can race each other and a check-then-insert lets both through.
    /// </para>
    /// </remarks>
    public Guid ClientCommandId { get; private set; }

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
    /// Where the booking was made from. Self-reported; see <see cref="ReservationChannel"/>.
    /// </summary>
    /// <remarks>
    /// A reporting column and nothing else - no rule reads it, and no booking is refused because of
    /// it. It is here because somebody who booked from the public page has no app and therefore no
    /// push channel, so the reminder that the whole no-show story rests on cannot reach them, and
    /// how often that happens is the number that decides whether an SMS channel is worth paying for.
    /// </remarks>
    public ReservationChannel Channel { get; private set; }

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
        DateTime? holdExpiresAtUtc,
        Guid clientCommandId,
        ReservationChannel channel)
        : base(Guid.CreateVersion7())
    {
        ClientCommandId = Guard.NotEmpty(clientCommandId, nameof(clientCommandId));
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
        Channel = Guard.Defined(channel, nameof(channel));
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
        DateTime? holdExpiresAtUtc = null,
        Guid? clientCommandId = null,
        ReservationChannel channel = ReservationChannel.Unknown)
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
            holdExpiresAtUtc,
            // A booking taken over the phone by a waiter has no client to generate one, and a
            // booking with no key still needs the column to be unique. Minting one here keeps the
            // index honest without forcing every caller to invent an id it will never replay.
            clientCommandId ?? Guid.CreateVersion7(),
            channel);
    }

    /// <summary>
    /// Whether the party is late, computed rather than stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is why <see cref="ReservationStatus"/> has no <c>Late</c> member: the answer is a pure
    /// function of the clock and the branch's <see cref="ReservationPolicy.GraceMinutes"/>, so
    /// there is nothing for a timer to keep in sync and nothing to go stale after a cancellation.
    /// </para>
    /// <para>
    /// Only a <see cref="ReservationStatus.Confirmed"/> booking can be late. A seated party has
    /// arrived, and a cancelled or no-show booking is finished being interesting.
    /// </para>
    /// <para>
    /// This answers "should the UI show this as late?". It does <b>not</b> decide whether to send
    /// the late-nudge push - that is a scheduled action needing an outbox and a delivery record,
    /// and is deliberately not built here.
    /// </para>
    /// </remarks>
    public bool IsLateAt(DateTime nowUtc, int graceMinutes) =>
        Status == ReservationStatus.Confirmed && nowUtc > StartUtc.AddMinutes(graceMinutes);

    /// <summary>How far past the start time the party is, or null when they are not late yet.</summary>
    public TimeSpan? LatenessAt(DateTime nowUtc, int graceMinutes) =>
        IsLateAt(nowUtc, graceMinutes) ? nowUtc - StartUtc : null;

    /// <summary>
    /// "We are five minutes away." Pushes the hold out by the branch's extension, <b>once</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GraceExtensionsUsed"/> has been an orphan column since Prompt 1 and this is what
    /// finally reads it. Once, because "just five more minutes" granted repeatedly is how a table
    /// stays held all evening for somebody who is not coming - and the venue loses the cover without
    /// ever making a decision.
    /// </para>
    /// <para>
    /// Extends from whichever is later: the current hold, or now. A diner who taps the nudge after
    /// the hold already lapsed gets a full extension from this moment rather than one measured from
    /// a deadline that has passed.
    /// </para>
    /// </remarks>
    /// <exception cref="DomainStateException">
    /// The extension was already used, or the booking is not in a state that can hold a table.
    /// </exception>
    public void ExtendHold(DateTime nowUtc, int extensionMinutes)
    {
        if (Status != ReservationStatus.Confirmed)
        {
            throw new DomainStateException(
                $"Only a confirmed booking can have its table held; {Code} is {Status}.");
        }

        if (GraceExtensionsUsed > 0)
        {
            throw new DomainStateException(
                "This booking has already had its one extension. The table is being held for other "
                + "guests too, so a waiter decides what happens next.");
        }

        if (extensionMinutes <= 0)
        {
            throw new DomainStateException(
                "This branch does not offer hold extensions. Speak to the venue.");
        }

        var from = HoldExpiresAtUtc is { } current && current > nowUtc ? current : nowUtc;

        HoldExpiresAtUtc = from.AddMinutes(extensionMinutes);
        GraceExtensionsUsed++;
    }

    /// <summary>
    /// The party arrived and was seated. Called in the same transaction as the table transition
    /// and the session insert, so a seated table and an unseated booking cannot coexist.
    /// </summary>
    public void MarkSeated()
    {
        if (Status != ReservationStatus.Confirmed)
        {
            throw new DomainStateException(
                $"Only a confirmed reservation can be seated; {Code} is {Status}.");
        }

        Status = ReservationStatus.Seated;
    }

    /// <summary>The session this booking produced has closed normally.</summary>
    public void MarkCompleted()
    {
        if (Status != ReservationStatus.Seated)
        {
            throw new DomainStateException(
                $"Only a seated reservation can be completed; {Code} is {Status}.");
        }

        Status = ReservationStatus.Completed;
    }

    /// <summary>Staff accepted a booking that needed approval.</summary>
    public void Confirm(DateTime confirmedAtUtc)
    {
        if (Status != ReservationStatus.PendingApproval)
        {
            throw new DomainStateException(
                $"Only a pending reservation can be confirmed; {Code} is {Status}.");
        }

        Status = ReservationStatus.Confirmed;
        ConfirmedAtUtc = Guard.NotLocalTime(confirmedAtUtc, nameof(confirmedAtUtc));
        HoldExpiresAtUtc = null;
    }

    /// <summary>
    /// Re-rolls the door code after the unique index rejected the first one.
    /// </summary>
    /// <remarks>
    /// For the insert-time collision retry only, which is why it refuses a booking that has moved
    /// past its initial status: by then the code has been sent to the diner and printed on the
    /// staff app's list, and changing it turns a booking nobody can find into somebody's problem
    /// at the door.
    /// </remarks>
    public void ReplaceCode(string code)
    {
        if (Status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            throw new DomainStateException(
                $"The code of a {Status} reservation cannot be changed; the diner already has it.");
        }

        Code = Guard.NotBlank(code, nameof(code), FieldLengths.ReservationCode).ToUpperInvariant();
    }

    /// <summary>
    /// The diner cancelled.
    /// </summary>
    /// <remarks>
    /// <paramref name="afterDeadline"/> is decided by the caller from the branch's
    /// <c>CancellationDeadlineMinutes</c> and recorded, never used to refuse. Refusing a late
    /// cancellation converts it into a no-show, which costs the venue the same slot and also the
    /// chance to resell it.
    /// </remarks>
    public void CancelByDiner(DateTime atUtc, string? reason, bool afterDeadline) =>
        Cancel(ReservationStatus.CancelledByDiner, atUtc, reason, afterDeadline);

    /// <summary>The venue cancelled a booking it had already accepted.</summary>
    public void CancelByVenue(DateTime atUtc, string? reason) =>
        Cancel(ReservationStatus.CancelledByVenue, atUtc, reason, afterDeadline: false);

    /// <summary>
    /// Staff declined a booking that was waiting for approval.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CancelByVenue"/> only in what it will accept: a rejection applies
    /// to a booking that was never promised, so refusing one that is already
    /// <see cref="ReservationStatus.Confirmed"/> stops a manager quietly un-promising a table the
    /// diner has already been told is theirs.
    /// </remarks>
    public void Reject(DateTime atUtc, string? reason)
    {
        if (Status != ReservationStatus.PendingApproval)
        {
            throw new DomainStateException(
                $"Only a pending reservation can be rejected; {Code} is {Status}.");
        }

        Cancel(ReservationStatus.CancelledByVenue, atUtc, reason, afterDeadline: false);
    }

    /// <summary>
    /// Grace ran out and nobody came.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="IsLateAt"/>: lateness is derived and needs no row written,
    /// but declaring a no-show is an action somebody takes, and it is what the rolling no-show
    /// count later reads.
    /// </remarks>
    public void MarkNoShow(DateTime atUtc)
    {
        if (Status != ReservationStatus.Confirmed)
        {
            throw new DomainStateException(
                $"Only a confirmed reservation can be recorded as a no-show; {Code} is {Status}.");
        }

        Status = ReservationStatus.NoShow;
        CancelledAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        HoldExpiresAtUtc = null;
    }

    private void Cancel(ReservationStatus to, DateTime atUtc, string? reason, bool afterDeadline)
    {
        if (Status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            // A seated party cannot cancel - they are at the table - and a booking that is
            // already cancelled, completed or a no-show is finished being interesting.
            throw new DomainStateException(
                $"Only a pending or confirmed reservation can be cancelled; {Code} is {Status}.");
        }

        Status = to;
        CancelledAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        CancellationReason = Guard.OptionalText(reason, nameof(reason), FieldLengths.Reason);
        CancelledAfterDeadline = afterDeadline;
        HoldExpiresAtUtc = null;
    }
}
