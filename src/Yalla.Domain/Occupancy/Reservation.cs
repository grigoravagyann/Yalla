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
    /// End of the booked interval, UTC. Derived from the branch turn time at creation - or the time the
    /// guest promised to free the table by, when that is sooner (<see cref="PromisedFreeByUtc"/>).
    /// </summary>
    public DateTime EndUtc { get; private set; }

    /// <summary>
    /// The time the guest promised to have left the table by, or null for a booking that made no such
    /// promise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways to make one (see <see cref="TableSequencing"/>): booking a table whose next booking a
    /// full sitting would run into - the diner accepts "I'll free the table by 21:25" before sending -
    /// and saying yes when a later guest asks for the table (<see cref="TableShareRequest"/>). Either
    /// way <see cref="EndUtc"/> is cut to it when it is sooner, so every rule that reads the interval
    /// reads the promise, and staff see it on the awaiting list and the floor.
    /// </para>
    /// <para>
    /// Kept even when it is no sooner than the booked end: a guest who said they would be gone by 19:40
    /// is somebody the floor can hold to it, and a later request is not asked of them again.
    /// </para>
    /// </remarks>
    public DateTime? PromisedFreeByUtc { get; private set; }

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

    /// <summary>
    /// Why the booking went to <see cref="ReservationStatus.PendingApproval"/> - the reason it was
    /// given then - or null for a booking that never waited, and for one from before the column.
    /// </summary>
    /// <remarks>
    /// Stored, not worked out again from the clock or the branch's policy at read time: a booking made
    /// at short notice at two in the afternoon is still the short-notice booking at nine, and a policy
    /// edited since cannot rename the reason the diner was shown. Kept after the decision, as the
    /// record of why it waited; a move that sends the booking back for approval replaces it.
    /// </remarks>
    public ApprovalTrigger? ApprovalTrigger { get; private set; }

    /// <summary>
    /// When the chance to answer this booking runs out - fifteen minutes at short notice (see
    /// <see cref="ShortNoticeRule"/>), two hours for a wait on the guest before it on the table, asked in
    /// the app or by the floor (see <see cref="TableShareRequest"/>) - or null for a booking with no
    /// deadline and for every booking that is no longer waiting.
    /// </summary>
    /// <remarks>
    /// Stored, so the sweep that declines lapsed requests reads one indexed column rather than
    /// replaying the rule over every pending booking. Cleared the moment the booking stops waiting,
    /// whichever way it stops, so the sweep's index holds only the requests still open.
    /// </remarks>
    public DateTime? ApprovalExpiresAtUtc { get; private set; }

    /// <summary>
    /// Why the booking - or the change to it that was waiting - was declined: by a member of staff, or
    /// because nobody answered in time. Null for one that was not declined.
    /// </summary>
    /// <remarks>
    /// On a <see cref="ReservationStatus.CancelledByVenue"/> booking it is why the booking ended. On a
    /// confirmed one it is why the last change asked for did not go through and the booking went back
    /// to the slot it had (see <see cref="KeepConfirmedSlot"/>). A new change clears it.
    /// </remarks>
    public DeclineReason? DeclineReason { get; private set; }

    /// <summary>
    /// The confirmed slot this booking had before a change sent it back for approval - its table -
    /// or null when no change is waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A declined change must not cost the diner the booking they had.</b> Moving tonight's
    /// confirmed 19:00 to 21:00 sends it to the floor (short notice); if the floor says no, or does
    /// not answer, the diner still has their 19:00 - so the confirmed slot is kept here, all seven
    /// columns of it, from the moment the change starts waiting until it is decided.
    /// </para>
    /// <para>
    /// Set when a <see cref="ReservationStatus.Confirmed"/> booking is moved into
    /// <see cref="ReservationStatus.PendingApproval"/>, and kept through further moves while it waits -
    /// it is the last slot anybody said yes to. Cleared when the change is approved, when the booking
    /// goes back to it, and when the booking ends.
    /// </para>
    /// </remarks>
    public Guid? PreviousDiningTableId { get; private set; }

    /// <summary>The confirmed slot's start. See <see cref="PreviousDiningTableId"/>.</summary>
    public DateTime? PreviousStartUtc { get; private set; }

    /// <summary>The confirmed slot's end, as it was fixed then. See <see cref="PreviousDiningTableId"/>.</summary>
    public DateTime? PreviousEndUtc { get; private set; }

    /// <summary>The confirmed slot's local date. See <see cref="PreviousDiningTableId"/>.</summary>
    public DateOnly? PreviousLocalDate { get; private set; }

    /// <summary>The confirmed slot's local start time. See <see cref="PreviousDiningTableId"/>.</summary>
    public TimeOnly? PreviousLocalStartTime { get; private set; }

    /// <summary>The party size that was confirmed. See <see cref="PreviousDiningTableId"/>.</summary>
    public int? PreviousPartySize { get; private set; }

    /// <summary>When the confirmed slot was confirmed. See <see cref="PreviousDiningTableId"/>.</summary>
    public DateTime? PreviousConfirmedAtUtc { get; private set; }

    /// <summary>
    /// The confirmed slot's promise to free the table, when it had one. See <see cref="PreviousDiningTableId"/>
    /// and <see cref="PromisedFreeByUtc"/>.
    /// </summary>
    public DateTime? PreviousPromisedFreeByUtc { get; private set; }

    /// <summary>Whether a change is waiting with a confirmed slot behind it to go back to.</summary>
    public bool HasConfirmedSlotToReturnTo =>
        PreviousDiningTableId is not null && PreviousStartUtc is not null && PreviousEndUtc is not null;

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
    /// What the diner asked the venue for when booking - "window table", "a high chair" - or null.
    /// </summary>
    /// <remarks>
    /// Sent to the venue: it is on the staff booking list and the approval queue, as well as on the
    /// diner's own bookings and the manage link. Trimmed; blank is stored as null; at most
    /// <see cref="FieldLengths.Reason"/> characters (K9).
    /// </remarks>
    public string? Note { get; private set; }

    /// <summary>A booking note as it is stored: trimmed, blank as null.</summary>
    /// <exception cref="FieldValidationException">Longer than <see cref="FieldLengths.Reason"/>, naming <c>note</c>.</exception>
    public static string? NormaliseNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var trimmed = note.Trim();

        return trimmed.Length > FieldLengths.Reason
            ? throw new FieldValidationException(new FieldViolation(
                "note",
                $"A note to the venue is at most {FieldLengths.Reason} characters.",
                FieldBounds.Max,
                Max: FieldLengths.Reason,
                Value: trimmed.Length))
            : trimmed;
    }

    /// <summary>
    /// How long past the end of the booking a manage link keeps working.
    /// </summary>
    /// <remarks>
    /// Not zero. Somebody who booked on Friday opens the link on Sunday to check what time they
    /// actually went, or to show a companion the code - and an error page is a worse answer than
    /// "this booking is finished". Short, because the link is a bearer capability that will sit in
    /// a WhatsApp thread forever and there is no reason for it to stay live once nobody could act
    /// on it.
    /// </remarks>
    public const int ManageTokenGraceDays = 7;

    /// <summary>
    /// Hex SHA-256 of the manage token, or null for a booking made before the column existed and
    /// for one made by a channel that has no use for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hashed, like a refresh handle and an enrolment code.</b> The token is 256 bits from a
    /// CSPRNG, so guessing it is already impossible and the only job left is making a database
    /// dump useless - which is exactly the split <c>Secrets</c> documents, and exactly why a fast
    /// unsalted digest is right here: the row is looked up <i>by</i> this hash, which a salted one
    /// could not do.
    /// </para>
    /// <para>
    /// It matters more here than for a refresh handle. This one is a bearer capability that lives
    /// in a URL, and that URL gets pasted into WhatsApp, left in browser history and read by
    /// whoever picks the phone up.
    /// </para>
    /// </remarks>
    public string? ManageTokenHash { get; private set; }

    /// <summary>When the manage link stops working: the end of the booking plus the grace.</summary>
    /// <remarks>
    /// Derived rather than stored. The end of the booking is already on the row, the grace is a
    /// constant, and a stored copy is one more thing that can disagree with the booking it is
    /// about after somebody moves it.
    /// </remarks>
    public DateTime ManageTokenExpiresAtUtc => EndUtc.AddDays(ManageTokenGraceDays);

    /// <summary>Whether the manage link is still live at <paramref name="nowUtc"/>.</summary>
    public bool ManageTokenIsLiveAt(DateTime nowUtc) =>
        ManageTokenHash is not null && nowUtc <= ManageTokenExpiresAtUtc;

    /// <summary>
    /// Attaches the manage token's hash at creation. Once only.
    /// </summary>
    /// <remarks>
    /// Refusing to replace it is what stops a second call quietly invalidating a link a diner has
    /// already been sent. Rotating one is not a thing this product does: there is no way to deliver
    /// a replacement to somebody who has no app.
    /// </remarks>
    public void AttachManageToken(string tokenHash)
    {
        if (ManageTokenHash is not null)
        {
            throw new DomainStateException(
                $"Booking {Code} already has a manage token; issuing a second would break the first link.");
        }

        ManageTokenHash = Guard.NotBlank(tokenHash, nameof(tokenHash), FieldLengths.TokenHash);
    }

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
    /// <see cref="ReservationStatus.PendingApproval"/> when the booking is at short notice, the
    /// branch does not auto-confirm, the party is over its approval threshold or the diner is over the
    /// no-show threshold. Deciding between the two is the caller's job, and so is the reason - stored
    /// as <paramref name="approvalTrigger"/> - and the answer window, if there is one; a reservation
    /// simply refuses to start life in any other state, or with a window and no wait.
    /// <para>
    /// <paramref name="promisedFreeByUtc"/> is the time the diner accepted to free the table by, when
    /// the table's next booking would cut a full sitting short (<see cref="TableSequencing"/>): the
    /// booking ends then rather than a turn time after its start.
    /// </para>
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
        ReservationChannel channel = ReservationChannel.Unknown,
        string? note = null,
        ApprovalTrigger? approvalTrigger = null,
        DateTime? approvalExpiresAtUtc = null,
        DateTime? promisedFreeByUtc = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (initialStatus is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialStatus),
                initialStatus,
                "A new reservation must start as Confirmed or PendingApproval.");
        }

        if (initialStatus == ReservationStatus.Confirmed && approvalTrigger is not null)
        {
            throw new ArgumentException(
                "A booking confirmed on the spot has no reason to wait for approval.", nameof(approvalTrigger));
        }

        var checkedNote = NormaliseNote(note);
        var naturalEnd = startUtc.AddMinutes(policy.TurnTimeMinutes);

        if (promisedFreeByUtc is { } freeBy
            && (Guard.NotLocalTime(freeBy, nameof(promisedFreeByUtc)) <= startUtc || freeBy > naturalEnd))
        {
            throw new ArgumentOutOfRangeException(
                nameof(promisedFreeByUtc),
                promisedFreeByUtc,
                "A time to free the table by must come after the start and no later than a full sitting.");
        }

        var reservation = new Reservation(
            branchId,
            diningTableId,
            startUtc,
            promisedFreeByUtc ?? naturalEnd,
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

        reservation.Note = checkedNote;
        reservation.PromisedFreeByUtc = promisedFreeByUtc;
        reservation.AwaitDecision(initialStatus, approvalTrigger, approvalExpiresAtUtc);

        return reservation;
    }

    /// <summary>
    /// The guest says they will have left the table by <paramref name="freeByUtc"/> - yes to a later
    /// guest's request for it (see <see cref="TableShareRequest"/>).
    /// </summary>
    /// <remarks>
    /// The booking's end is cut to the promise when the promise is sooner, so every rule that reads the
    /// interval reads it, and never lengthened: a booking ending 19:30 whose guest promises to be gone
    /// by 19:40 still ends at 19:30, with the promise kept on the row for the floor - and so that the
    /// guest is not asked again. Of two promises the sooner stands.
    /// </remarks>
    /// <exception cref="DomainStateException">The booking no longer holds the table.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The time is not after the booking's start.</exception>
    public void PromiseToFreeBy(DateTime freeByUtc)
    {
        if (!ReservationOverlap.Blocks(Status))
        {
            throw new DomainStateException(
                $"Booking {Code} is {Status}; it holds no table to promise to free.");
        }

        if (Guard.NotLocalTime(freeByUtc, nameof(freeByUtc)) <= StartUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(freeByUtc), freeByUtc, "A time to free the table by must come after the booking starts.");
        }

        var promise = PromisedFreeByUtc is { } earlier && earlier < freeByUtc ? earlier : freeByUtc;

        PromisedFreeByUtc = promise;

        if (promise < EndUtc)
        {
            EndUtc = promise;
        }
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
    /// When the table starts being the party's to sit at: the branch's walk-in holdback before the
    /// start.
    /// </summary>
    /// <remarks>
    /// Not a number of its own. It is the moment the branch starts holding the table back from
    /// walk-ins for this booking - <see cref="SessionOccupancy.HoldbackBeginsAtUtc"/>, the same instant
    /// that starts warning a waiter who seats somebody else here - so the venue keeping the table for
    /// the party and the party being allowed to take it cannot disagree.
    /// </remarks>
    public DateTime TableIsTheirsFromUtc(int walkInHoldbackMinutes) =>
        SessionOccupancy.HoldbackBeginsAtUtc(StartUtc, walkInHoldbackMinutes);

    /// <summary>
    /// Refuses, with the reason, unless this booking's table is the party's to sit at now - the rule
    /// behind opening a tab with the booking code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seated</b> passes whatever the clock says. The venue has already put the party at the
    /// table, and the sitting holds it until staff free it - which completes the booking.
    /// </para>
    /// <para>
    /// <b>Confirmed</b> passes from <see cref="TableIsTheirsFromUtc"/> until <see cref="EndUtc"/>,
    /// which is the interval the table is held for the party. Lateness does not end it: past the grace
    /// a waiter <i>may</i> release the table, and until one does it is still theirs.
    /// </para>
    /// <para>
    /// Everything else is not expecting its party. <b>Completed</b> has ended; <b>PendingApproval</b>,
    /// both cancellations and <b>NoShow</b> are not active.
    /// </para>
    /// </remarks>
    /// <exception cref="BookingTabRefusedException">
    /// <c>booking-too-early</c>, <c>booking-ended</c> or <c>booking-not-active</c>.
    /// </exception>
    public void RequireTableIsTheirsAt(DateTime nowUtc, int walkInHoldbackMinutes)
    {
        var earliestUtc = TableIsTheirsFromUtc(walkInHoldbackMinutes);

        switch (Status)
        {
            case ReservationStatus.Seated:
                return;

            case ReservationStatus.Confirmed:
                break;

            case ReservationStatus.Completed:
                throw new BookingTabRefusedException(
                    BookingTabRefusedException.Ended,
                    this,
                    earliestUtc,
                    $"Booking {Code} is finished. To order at the table now, scan the code on it.");

            case ReservationStatus.PendingApproval:
                throw new BookingTabRefusedException(
                    BookingTabRefusedException.NotActive,
                    this,
                    earliestUtc,
                    $"Booking {Code} is still waiting for the venue to confirm it, so there is no table to "
                    + "open a tab on yet. Ask a member of staff.");

            case ReservationStatus.NoShow:
                throw new BookingTabRefusedException(
                    BookingTabRefusedException.NotActive,
                    this,
                    earliestUtc,
                    $"Booking {Code} was released when nobody arrived. Ask a member of staff for a table.");

            default:
                throw new BookingTabRefusedException(
                    BookingTabRefusedException.NotActive,
                    this,
                    earliestUtc,
                    $"Booking {Code} was cancelled. Ask a member of staff for a table.");
        }

        if (nowUtc < earliestUtc)
        {
            throw new BookingTabRefusedException(
                BookingTabRefusedException.TooEarly,
                this,
                earliestUtc,
                walkInHoldbackMinutes > 0
                    ? $"It is too early to open the tab for booking {Code}: the table is kept for you from "
                      + $"{walkInHoldbackMinutes} minutes before it starts. Try again then."
                    : $"It is too early to open the tab for booking {Code}: the table is yours from the "
                      + "time it starts. Try again then.");
        }

        if (nowUtc >= EndUtc)
        {
            throw new BookingTabRefusedException(
                BookingTabRefusedException.Ended,
                this,
                earliestUtc,
                $"Booking {Code} has ended. To order at the table now, scan the code on it.");
        }
    }

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
    /// <exception cref="HoldExtensionRefusedException">
    /// The booking has not started or is not confirmed (<c>hold-not-active</c>), its one extension is
    /// spent (<c>hold-already-extended</c>), or the branch offers none (<c>extensions-not-offered</c>).
    /// </exception>
    public void ExtendHold(DateTime nowUtc, int extensionMinutes)
    {
        if (Status != ReservationStatus.Confirmed)
        {
            throw new HoldExtensionRefusedException(
                HoldExtensionRefusedException.NotActive,
                Id,
                StartUtc,
                $"Only a confirmed booking can have its table held; {Code} is {Status}.");
        }

        // Before "already used": a branch that offers none has nothing to have used, and telling
        // that diner they have already let the venue know is the mistake this order avoids.
        if (extensionMinutes <= 0)
        {
            throw new HoldExtensionRefusedException(
                HoldExtensionRefusedException.NotOffered,
                Id,
                StartUtc,
                "This branch does not offer hold extensions. Speak to the venue.");
        }

        if (GraceExtensionsUsed > 0)
        {
            throw new HoldExtensionRefusedException(
                HoldExtensionRefusedException.AlreadyExtended,
                Id,
                StartUtc,
                "This booking has already had its one extension. The table is being held for other "
                + "guests too, so a waiter decides what happens next.");
        }

        // Nothing is held before the booking starts, so there is nothing to keep. The late nudge
        // that carries this button fires after the start; the app also offered it on every
        // confirmed booking, and a tap days early spent the one extension and told the floor about
        // a table nobody was holding.
        if (nowUtc < StartUtc)
        {
            throw new HoldExtensionRefusedException(
                HoldExtensionRefusedException.NotActive,
                Id,
                StartUtc,
                "This booking has not started yet, so there is no table being held to keep. Ask once "
                + "you are running late.");
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
    /// <exception cref="ReservationApprovalExpiredException">
    /// The answer window has closed, the booking was already declined for nobody answering, or it has
    /// started - approving a booking that is already under way promises a table nobody kept.
    /// </exception>
    /// <exception cref="DomainStateException">The booking is not waiting for approval.</exception>
    public void Confirm(DateTime confirmedAtUtc)
    {
        RequireStillAnswerable(confirmedAtUtc, approving: true);

        if (Status != ReservationStatus.PendingApproval)
        {
            throw new DomainStateException(
                $"Only a pending reservation can be confirmed; {Code} is {Status}.");
        }

        Status = ReservationStatus.Confirmed;
        ConfirmedAtUtc = Guard.NotLocalTime(confirmedAtUtc, nameof(confirmedAtUtc));
        HoldExpiresAtUtc = null;
        ApprovalExpiresAtUtc = null;

        // The change is accepted, so there is nothing to go back to.
        ForgetConfirmedSlot();
    }

    /// <summary>
    /// The waiting change is declined, and the booking goes back to the confirmed slot it had - same
    /// table, start, end, party and confirmation - as a <see cref="ReservationStatus.Confirmed"/>
    /// booking, recording why the change did not go through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller has already made sure the slot is still free, under its table's lock: another diner
    /// may have booked it after this one moved away, and then there is nothing to go back to and the
    /// booking is declined instead (<see cref="Reject"/>, <see cref="DeclineForNoAnswer"/>).
    /// </para>
    /// <para>
    /// The same deadline rules as declining: a member of staff only while the window is open, the
    /// sweep only once it has run out.
    /// </para>
    /// </remarks>
    /// <param name="atUtc">Now.</param>
    /// <param name="because">Who declined the change: staff, or nobody in time.</param>
    /// <exception cref="ReservationApprovalExpiredException">A staff decline after the window closed.</exception>
    /// <exception cref="DomainStateException">
    /// Not a waiting change of a confirmed booking, or - for no answer - its window has not run out.
    /// </exception>
    public void KeepConfirmedSlot(DateTime atUtc, DeclineReason because)
    {
        if (IsForNoAnswer(because))
        {
            if (!ApprovalHasLapsedAt(atUtc))
            {
                throw new DomainStateException(
                    $"Booking {Code} is {Status} and its answer window has not run out; it cannot be declined for no answer.");
            }
        }
        else
        {
            RequireStillAnswerable(atUtc, approving: false);
        }

        if (Status != ReservationStatus.PendingApproval || !HasConfirmedSlotToReturnTo)
        {
            throw new DomainStateException(
                $"Booking {Code} is {Status} with no confirmed slot to go back to.");
        }

        DiningTableId = PreviousDiningTableId!.Value;
        StartUtc = PreviousStartUtc!.Value;
        EndUtc = PreviousEndUtc!.Value;
        LocalDate = PreviousLocalDate ?? LocalDate;
        LocalStartTime = PreviousLocalStartTime ?? LocalStartTime;
        PartySize = PreviousPartySize ?? PartySize;
        ConfirmedAtUtc = PreviousConfirmedAtUtc;
        PromisedFreeByUtc = PreviousPromisedFreeByUtc;
        Status = ReservationStatus.Confirmed;
        HoldExpiresAtUtc = null;
        ApprovalExpiresAtUtc = null;
        DeclineReason = Guard.Defined(because, nameof(because));

        ForgetConfirmedSlot();
    }

    /// <summary>
    /// A booking still waiting is now waiting for another reason - the question it asked the guest before
    /// it went away, and something else still needs a human - with that reason's deadline, or none.
    /// </summary>
    /// <exception cref="DomainStateException">The booking is not waiting.</exception>
    public void WaitInsteadFor(ApprovalTrigger trigger, DateTime? approvalExpiresAtUtc)
    {
        if (Status != ReservationStatus.PendingApproval)
        {
            throw new DomainStateException($"Booking {Code} is {Status}; only a waiting booking waits for a reason.");
        }

        AwaitDecision(Status, trigger, approvalExpiresAtUtc);
    }

    /// <summary>
    /// Whether a decline for this reason is the clock's - nobody answered in time - rather than
    /// somebody's no. The clock's declines happen only once the answer window has run out; somebody's
    /// only while it is open.
    /// </summary>
    public static bool IsForNoAnswer(DeclineReason because) =>
        because is Enums.DeclineReason.NoAnswer or Enums.DeclineReason.EarlierGuestNoAnswer;

    /// <summary>
    /// Refuses with <see cref="ReservationApprovalExpiredException"/> once a waiting booking's answer
    /// window has run out - for a change of a booking whose short-notice request nobody answered.
    /// </summary>
    /// <remarks>
    /// Past the deadline the request is the sweep's to decline. A move then would hand the diner a
    /// fresh request and the floor a fresh fifteen minutes for one nobody answered, which is the
    /// countdown restarting itself.
    /// </remarks>
    public void RequireAnswerWindowOpenAt(DateTime nowUtc)
    {
        if (ApprovalHasLapsedAt(nowUtc))
        {
            throw new ReservationApprovalExpiredException(
                Id, Code, Status, StartUtc, ApprovalExpiresAtUtc,
                $"The venue's time to answer booking {Code} has run out, so it can no longer be changed. "
                + "It is being declined; you can book again.");
        }
    }

    /// <summary>
    /// Whether this booking's answer window has run out at <paramref name="nowUtc"/> with nobody having
    /// answered: still pending, and past <see cref="ApprovalExpiresAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// The deadline instant itself is past it, here and in the sweep's query alike, so a waiter tapping
    /// Approve on the last second of the countdown and the sweep running on that second agree.
    /// </remarks>
    public bool ApprovalHasLapsedAt(DateTime nowUtc) =>
        Status == ReservationStatus.PendingApproval
        && ApprovalExpiresAtUtc is { } deadline
        && nowUtc >= deadline;

    /// <summary>
    /// Nobody at the venue answered in time: the booking is declined for them, with
    /// <see cref="DeclineReason"/> <see cref="Enums.DeclineReason.NoAnswer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep's transition, and nobody else's. It ends the booking exactly as a staff decline does -
    /// <see cref="ReservationStatus.CancelledByVenue"/>, the slot open to anybody else - but records
    /// that it was the clock, so the diner is told the truth: the venue did not turn them down, it did
    /// not look in time.
    /// </para>
    /// <para>
    /// No table is released here. The booking was never promised, so there is no hold to let go and
    /// nothing on the floor changes - which is why this is not the timer the notifications document
    /// says must never free a table.
    /// </para>
    /// </remarks>
    /// <param name="atUtc">Now.</param>
    /// <param name="because">
    /// Whose silence: the venue's (<see cref="Enums.DeclineReason.NoAnswer"/>), or the guest's before this
    /// booking on the table (<see cref="Enums.DeclineReason.EarlierGuestNoAnswer"/>).
    /// </param>
    /// <exception cref="DomainStateException">The window has not run out, or the booking is not waiting.</exception>
    public void DeclineForNoAnswer(DateTime atUtc, DeclineReason because = Enums.DeclineReason.NoAnswer)
    {
        if (!IsForNoAnswer(because))
        {
            throw new ArgumentOutOfRangeException(nameof(because), because, "Not a reason for nobody answering.");
        }

        if (!ApprovalHasLapsedAt(atUtc))
        {
            throw new DomainStateException(
                $"Booking {Code} is {Status} and its answer window has not run out; it cannot be declined for no answer.");
        }

        Cancel(ReservationStatus.CancelledByVenue, atUtc, reason: null, afterDeadline: false);
        DeclineReason = because;
    }

    /// <summary>
    /// Refuses a decision that comes too late: the window closed, the sweep already declined the
    /// booking, or - for an approval - the booking has started.
    /// </summary>
    private void RequireStillAnswerable(DateTime atUtc, bool approving)
    {
        // Declined by the sweep: the booking ended, or - for a change of a confirmed booking - it went
        // back to the slot it had. Either way the request is gone.
        if (Status != ReservationStatus.PendingApproval && DeclineReason is { } declined && IsForNoAnswer(declined))
        {
            throw new ReservationApprovalExpiredException(
                Id, Code, Status, StartUtc, ApprovalExpiresAtUtc,
                Status == ReservationStatus.Confirmed
                    ? $"The change to booking {Code} was already declined because nobody answered in time; the "
                      + "booking stands as it was, and the diner has been told."
                    : $"Booking {Code} was already declined because nobody answered in time. The diner has been "
                      + "told and can book again.");
        }

        if (Status != ReservationStatus.PendingApproval)
        {
            return;
        }

        if (ApprovalExpiresAtUtc is { } deadline && atUtc >= deadline)
        {
            throw new ReservationApprovalExpiredException(
                Id, Code, Status, StartUtc, deadline,
                $"The time to answer booking {Code} has run out. It is being declined, and the diner told "
                + "they can book again.");
        }

        if (approving && atUtc >= StartUtc)
        {
            throw new ReservationApprovalExpiredException(
                Id, Code, Status, StartUtc, ApprovalExpiresAtUtc,
                $"Booking {Code} has already started, so it can no longer be approved. Decline it, or seat "
                + "the party as a walk-in if they are here.");
        }
    }

    /// <summary>
    /// Records whether the booking waits for a decision, why, and until when - for a new booking and
    /// for one being moved.
    /// </summary>
    /// <remarks>
    /// A booking that is not waiting has no window. The reason is kept on a confirmed booking - it is
    /// the record of why it once waited - and replaced only when it goes to approval again.
    /// </remarks>
    private void AwaitDecision(ReservationStatus status, ApprovalTrigger? trigger, DateTime? expiresAtUtc)
    {
        if (status != ReservationStatus.PendingApproval)
        {
            if (expiresAtUtc is not null)
            {
                throw new ArgumentException(
                    "Only a booking waiting for approval has an answer window.", nameof(expiresAtUtc));
            }

            ApprovalExpiresAtUtc = null;

            return;
        }

        if (expiresAtUtc is { } deadline && Guard.NotLocalTime(deadline, nameof(expiresAtUtc)) > StartUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc), expiresAtUtc, "The answer window must close by the time the booking starts.");
        }

        ApprovalTrigger = trigger is null ? null : Guard.Defined(trigger.Value, nameof(trigger));
        ApprovalExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>
    /// Whether the diner may still change this booking at <paramref name="nowUtc"/>: it is pending or
    /// confirmed, and it has not started.
    /// </summary>
    /// <remarks>
    /// From the start the booking is the venue's to run - the table is held for the party, the late
    /// nudge fires, a waiter may release it - so moving it then would take the table from under a
    /// party that may be walking through the door.
    /// </remarks>
    public bool IsChangeableAt(DateTime nowUtc) =>
        Status is ReservationStatus.Confirmed or ReservationStatus.PendingApproval && StartUtc > nowUtc;

    /// <summary>Refuses, unless <see cref="IsChangeableAt"/>.</summary>
    /// <exception cref="ReservationNotChangeableException">The booking is finished with, or has started.</exception>
    public void RequireChangeableAt(DateTime nowUtc)
    {
        if (!IsChangeableAt(nowUtc))
        {
            throw new ReservationNotChangeableException(Id, Code, Status, StartUtc);
        }
    }

    /// <summary>
    /// The diner moves their booking - another time, another table, another party size - and it stays
    /// the same booking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same booking, not a cancellation and a new one.</b> The <see cref="Code"/> the diner
    /// was given, the manage link (<see cref="ManageTokenHash"/>), the note, the guest's name and
    /// number, the channel and the original <see cref="ClientCommandId"/> all stay: the code is what
    /// they quote at the door and the link may already be in a WhatsApp thread, and replacing either
    /// turns a change of plan into a booking nobody can find.
    /// </para>
    /// <para>
    /// <b>The interval is recomputed</b> from the branch's turn time as it stands, exactly as
    /// creation computes it, and any hold is cleared - it was for the old start.
    /// </para>
    /// <para>
    /// <b>The status is decided again</b>, by the caller and by the same rules as creation: a move can
    /// keep a booking confirmed, confirm one that was waiting, or send one back for approval - a party
    /// that grew past what the venue confirmed, a venue that approves every booking, a diner whose
    /// no-shows cost them instant confirmation, a move into tonight. Sending it back clears
    /// <see cref="ConfirmedAtUtc"/>, because that approval was for a different booking, and starts a
    /// fresh answer window when the caller gives one - the old one was for a different request.
    /// </para>
    /// </remarks>
    /// <param name="diningTableId">The table it moves to - the same one, or another at the branch.</param>
    /// <param name="startUtc">The new start.</param>
    /// <param name="localDate">The new local date, as the diner picked it.</param>
    /// <param name="localStartTime">The new local start time, as the diner picked it.</param>
    /// <param name="partySize">How many are coming now.</param>
    /// <param name="policy">The branch's policy, for the turn time that fixes <see cref="EndUtc"/>.</param>
    /// <param name="status">Confirmed, or PendingApproval - decided by the caller.</param>
    /// <param name="nowUtc">The instant of the change, against which "not started" is judged.</param>
    /// <param name="approvalTrigger">Why it waits, when <paramref name="status"/> is PendingApproval.</param>
    /// <param name="approvalExpiresAtUtc">Until when the venue may answer, for a short-notice wait.</param>
    /// <param name="promisedFreeByUtc">
    /// The time the diner accepted to free the table by at the new slot, when its next booking cuts a full
    /// sitting short - see <see cref="Create"/>. Replaces any promise the old slot had.
    /// </param>
    /// <exception cref="ReservationNotChangeableException">The booking is finished with, or has started.</exception>
    public void Reschedule(
        Guid diningTableId,
        DateTime startUtc,
        DateOnly localDate,
        TimeOnly localStartTime,
        int partySize,
        ReservationPolicy policy,
        ReservationStatus status,
        DateTime nowUtc,
        ApprovalTrigger? approvalTrigger = null,
        DateTime? approvalExpiresAtUtc = null,
        DateTime? promisedFreeByUtc = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        RequireChangeableAt(nowUtc);
        RequireAnswerWindowOpenAt(nowUtc);

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "A moved reservation must be Confirmed or PendingApproval.");
        }

        var naturalEnd = startUtc.AddMinutes(policy.TurnTimeMinutes);

        if (promisedFreeByUtc is { } freeBy
            && (Guard.NotLocalTime(freeBy, nameof(promisedFreeByUtc)) <= startUtc || freeBy > naturalEnd))
        {
            throw new ArgumentOutOfRangeException(
                nameof(promisedFreeByUtc),
                promisedFreeByUtc,
                "A time to free the table by must come after the start and no later than a full sitting.");
        }

        if (status == ReservationStatus.PendingApproval)
        {
            // A confirmed booking whose change now has to wait keeps the slot somebody said yes to,
            // so a no to the change is not a no to the booking. One that was already waiting keeps
            // what it had: the last confirmed slot, or none.
            if (Status == ReservationStatus.Confirmed)
            {
                PreviousDiningTableId = DiningTableId;
                PreviousStartUtc = StartUtc;
                PreviousEndUtc = EndUtc;
                PreviousLocalDate = LocalDate;
                PreviousLocalStartTime = LocalStartTime;
                PreviousPartySize = PartySize;
                PreviousConfirmedAtUtc = ConfirmedAtUtc;
                PreviousPromisedFreeByUtc = PromisedFreeByUtc;
            }
        }
        else
        {
            // Confirmed where it is now: nothing to go back to.
            ForgetConfirmedSlot();
        }

        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        StartUtc = Guard.NotLocalTime(startUtc, nameof(startUtc));
        EndUtc = promisedFreeByUtc ?? naturalEnd;
        PromisedFreeByUtc = promisedFreeByUtc;
        LocalDate = localDate;
        LocalStartTime = localStartTime;
        PartySize = Guard.Positive(partySize, nameof(partySize));
        HoldExpiresAtUtc = null;

        if (status == ReservationStatus.PendingApproval)
        {
            ConfirmedAtUtc = null;
        }

        Status = status;

        // A new request: whatever an earlier one was declined for is history.
        DeclineReason = null;
        AwaitDecision(status, approvalTrigger, approvalExpiresAtUtc);
    }

    /// <summary>Drops the confirmed slot a waiting change could fall back to.</summary>
    private void ForgetConfirmedSlot()
    {
        PreviousDiningTableId = null;
        PreviousStartUtc = null;
        PreviousEndUtc = null;
        PreviousLocalDate = null;
        PreviousLocalStartTime = null;
        PreviousPartySize = null;
        PreviousConfirmedAtUtc = null;
        PreviousPromisedFreeByUtc = null;
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
    /// diner has already been told is theirs. Recorded as <see cref="Enums.DeclineReason.StaffDeclined"/>.
    /// </remarks>
    /// <exception cref="ReservationApprovalExpiredException">
    /// The answer window has closed, or the booking was already declined for nobody answering: the
    /// diner is being told nobody looked, and a late "no" must not tell them otherwise.
    /// </exception>
    /// <param name="atUtc">Now.</param>
    /// <param name="reason">Optional free text, recorded as <see cref="CancellationReason"/>.</param>
    /// <param name="because">
    /// Whose no: a member of staff's (<see cref="Enums.DeclineReason.StaffDeclined"/>), or the guest's before
    /// this booking on the table (<see cref="Enums.DeclineReason.EarlierGuestDeclined"/>).
    /// </param>
    /// <exception cref="DomainStateException">The booking is not waiting for approval.</exception>
    public void Reject(DateTime atUtc, string? reason, DeclineReason because = Enums.DeclineReason.StaffDeclined)
    {
        if (IsForNoAnswer(because))
        {
            throw new ArgumentOutOfRangeException(nameof(because), because, "A no is somebody's answer, not the clock's.");
        }

        RequireStillAnswerable(atUtc, approving: false);

        if (Status != ReservationStatus.PendingApproval)
        {
            throw new DomainStateException(
                $"Only a pending reservation can be rejected; {Code} is {Status}.");
        }

        Cancel(ReservationStatus.CancelledByVenue, atUtc, reason, afterDeadline: false);
        DeclineReason = Guard.Defined(because, nameof(because));
    }

    /// <summary>
    /// Nobody came.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="IsLateAt"/>: lateness is derived and needs no row written,
    /// but declaring a no-show is an action somebody takes, and it is what the rolling no-show
    /// count later reads. Only from the start: before it nobody can have failed to come, and how
    /// late is too late after it is the waiter's call, not the grace period's.
    /// </remarks>
    /// <exception cref="ReservationNotStartedException">The booking has not started yet.</exception>
    public void MarkNoShow(DateTime atUtc)
    {
        if (Status != ReservationStatus.Confirmed)
        {
            throw new DomainStateException(
                $"Only a confirmed reservation can be recorded as a no-show; {Code} is {Status}.");
        }

        if (Guard.NotLocalTime(atUtc, nameof(atUtc)) < StartUtc)
        {
            throw new ReservationNotStartedException(Id, Code, StartUtc);
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

        // No longer waiting for anybody, so nothing for the sweep to find - and nothing to go back to.
        ApprovalExpiresAtUtc = null;
        ForgetConfirmedSlot();
    }
}
