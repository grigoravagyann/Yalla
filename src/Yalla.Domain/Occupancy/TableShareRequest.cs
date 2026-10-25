using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// A later booking asking the booking before it on the same table to free the table in time: "Someone
/// would like your table at 19:45. Will you have left by 19:40?"
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A table whose previous booking started less than three hours before is one somebody may
/// still be sitting at - Yerevan cafes sit long - so it is not simply sold again (see
/// <see cref="TableSequencing"/>). The later booking waits, <c>PendingApproval</c>, while the earlier
/// booking is asked. Yes: the later booking is confirmed and the earlier one's end is cut to
/// <see cref="FreeByUtc"/> (<see cref="Reservation.PromiseToFreeBy"/>). No, or no answer by
/// <see cref="ExpiresAtUtc"/>: the later booking is declined.
/// </para>
/// <para>
/// <b>Who answers</b> (<see cref="AsksEarlierGuest"/>): the earlier guest themselves, in the app, when
/// they booked from it and have a phone to push to - and the later booking needs nobody else's say.
/// Otherwise the floor, from the staff tablet's awaiting list, after calling them. A short-notice
/// booking is always the floor's: tonight the floor can see the table.
/// </para>
/// <para>
/// <b>One open question per earlier booking.</b> A guest is not asked by two people at once; while one
/// request waits, the table cannot be asked for by anybody else (<see
/// cref="ReservationRejectionReason.EarlierGuestBeingAsked"/>). A filtered unique index holds the rule
/// in the database too.
/// </para>
/// <para>
/// The later guest stays anonymous: the request carries the time they want and the time to leave by,
/// never their name.
/// </para>
/// </remarks>
public sealed class TableShareRequest : Entity
{
    /// <summary>How long the earlier guest - or the floor, calling them - has to answer, at most: two hours.</summary>
    public const int AnswerWindowMinutes = 120;

    /// <summary>The booking that has the table first, whose guest is asked.</summary>
    public Guid EarlierReservationId { get; private set; }

    /// <summary>The booking that wants the table after it, waiting on the answer.</summary>
    public Guid LaterReservationId { get; private set; }

    /// <summary>When the later booking starts - what the earlier guest is told: "someone wants your table at 19:45".</summary>
    public DateTime RequestedStartUtc { get; private set; }

    /// <summary>The time the earlier guest is asked to have left by: the later start less the clearing time.</summary>
    public DateTime FreeByUtc { get; private set; }

    public TableShareRequestStatus Status { get; private set; }

    /// <summary>
    /// True when the earlier guest is asked in the app (a push, and a card on their booking); false
    /// when the floor answers for them.
    /// </summary>
    public bool AsksEarlierGuest { get; private set; }

    /// <summary>
    /// Until when the question can be answered - always the same instant as the later booking's
    /// <c>ApprovalExpiresAtUtc</c>, past which the sweep declines it. Asked in the app, or by the floor
    /// another day: two hours from the request, never past the earlier booking's start nor the later
    /// one's. Asked by the floor tonight: the short-notice fifteen minutes. (Requests from before the floor's
    /// had a clock ran to the later booking's start.)
    /// </summary>
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>
    /// Why the later booking would still wait without this question - the reason it waits for the floor
    /// anyway (a branch that approves every booking, a large party, a no-show history, short notice) -
    /// or null when it would be confirmed at once.
    /// </summary>
    /// <remarks>
    /// Stored so that when the earlier booking gets out of the way - cancelled, moved - the later one is
    /// decided again without the question: confirmed when null, left waiting for this reason otherwise.
    /// Always null for a question asked in the app, which is only asked when nothing else needs a human.
    /// </remarks>
    public ApprovalTrigger? FallbackTrigger { get; private set; }

    /// <summary>When it was answered, expired or withdrawn; null while it waits.</summary>
    public DateTime? DecidedAtUtc { get; private set; }

    /// <summary>The earlier guest, the floor and the sweep can each end a request; the first one wins.</summary>
    public byte[] RowVersion { get; private set; } = [];

    private TableShareRequest()
    {
    }

    public TableShareRequest(
        Guid earlierReservationId,
        Guid laterReservationId,
        DateTime requestedStartUtc,
        DateTime freeByUtc,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        bool asksEarlierGuest,
        ApprovalTrigger? fallbackTrigger = null)
        : base(Guid.CreateVersion7())
    {
        EarlierReservationId = Guard.NotEmpty(earlierReservationId, nameof(earlierReservationId));
        LaterReservationId = Guard.NotEmpty(laterReservationId, nameof(laterReservationId));

        if (earlierReservationId == laterReservationId)
        {
            throw new ArgumentException("A booking cannot ask itself for its table.", nameof(laterReservationId));
        }

        RequestedStartUtc = Guard.NotLocalTime(requestedStartUtc, nameof(requestedStartUtc));
        FreeByUtc = Guard.NotLocalTime(freeByUtc, nameof(freeByUtc));

        if (freeByUtc > requestedStartUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(freeByUtc), freeByUtc, "The table has to be free by the time the later booking starts.");
        }

        StampCreatedAt(createdAtUtc);

        if (Guard.NotLocalTime(expiresAtUtc, nameof(expiresAtUtc)) <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc), expiresAtUtc, "A request has to leave some time to answer it.");
        }

        ExpiresAtUtc = expiresAtUtc;
        AsksEarlierGuest = asksEarlierGuest;

        if (asksEarlierGuest && fallbackTrigger is not null)
        {
            throw new ArgumentException(
                "The earlier guest is asked in the app only when nothing else about the later booking needs a human.",
                nameof(fallbackTrigger));
        }

        FallbackTrigger = fallbackTrigger is null ? null : Guard.Defined(fallbackTrigger.Value, nameof(fallbackTrigger));
        Status = TableShareRequestStatus.Pending;
    }

    /// <summary>
    /// Puts the question: raises <see cref="TableShareAsked"/>, whose handler pushes it to the earlier guest when they are
    /// asked in the app (<see cref="AsksEarlierGuest"/>); one the floor puts by phone tells nobody here.
    /// </summary>
    /// <remarks>
    /// Separate from the constructor, as sending a call is from building it: a request built and never asked - a test
    /// seeding an evening - tells nobody. A request retimed for a move is not asked again (<see cref="Retime"/>).
    /// </remarks>
    public void Ask() => Raise(new TableShareAsked(this, CreatedAtUtc));

    /// <summary>
    /// The later booking moved and still asks the same guest, the same way: the same question at its new
    /// time, without asking again - in the app under the same deadline or a sooner one, never a later
    /// one; by the floor under the deadline the moved booking now has (which the caller never makes later
    /// than it was), with the reason it would wait for anyway as it now is.
    /// </summary>
    /// <exception cref="DomainStateException">The request was already answered or withdrawn.</exception>
    public void Retime(DateTime requestedStartUtc, DateTime freeByUtc, DateTime expiresAtUtc, ApprovalTrigger? fallbackTrigger)
    {
        if (Status != TableShareRequestStatus.Pending)
        {
            throw new DomainStateException($"This request for a table was already {Status}.");
        }

        if (Guard.NotLocalTime(freeByUtc, nameof(freeByUtc)) > Guard.NotLocalTime(requestedStartUtc, nameof(requestedStartUtc)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(freeByUtc), freeByUtc, "The table has to be free by the time the later booking starts.");
        }

        RequestedStartUtc = requestedStartUtc;
        FreeByUtc = freeByUtc;

        var expires = Guard.NotLocalTime(expiresAtUtc, nameof(expiresAtUtc));

        if (!AsksEarlierGuest)
        {
            ExpiresAtUtc = expires;
            FallbackTrigger = fallbackTrigger is null ? null : Guard.Defined(fallbackTrigger.Value, nameof(fallbackTrigger));
        }
        else if (expires < ExpiresAtUtc)
        {
            ExpiresAtUtc = expires;
        }
    }

    /// <summary>Whether it can still be answered at <paramref name="nowUtc"/>: waiting, and before its deadline.</summary>
    public bool IsOpenAt(DateTime nowUtc) => Status == TableShareRequestStatus.Pending && nowUtc < ExpiresAtUtc;

    /// <summary>Yes: the earlier booking will free the table by <see cref="FreeByUtc"/>.</summary>
    public void Accept(DateTime atUtc) => Decide(TableShareRequestStatus.Accepted, atUtc);

    /// <summary>No.</summary>
    public void Decline(DateTime atUtc) => Decide(TableShareRequestStatus.Declined, atUtc);

    /// <summary>Nobody answered by <see cref="ExpiresAtUtc"/>.</summary>
    public void Expire(DateTime atUtc) => Decide(TableShareRequestStatus.Expired, atUtc);

    /// <summary>The question went away: one of the two bookings moved, or ended.</summary>
    public void Withdraw(DateTime atUtc) => Decide(TableShareRequestStatus.Withdrawn, atUtc);

    private void Decide(TableShareRequestStatus to, DateTime atUtc)
    {
        if (Status != TableShareRequestStatus.Pending)
        {
            throw new DomainStateException($"This request for a table was already {Status}.");
        }

        Status = to;
        DecidedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }
}
