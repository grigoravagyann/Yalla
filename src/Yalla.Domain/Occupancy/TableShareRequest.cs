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
    /// <summary>How long the earlier guest has to answer, at most: two hours.</summary>
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
    /// The deadline: two hours from the request, never past the earlier booking's start - the same
    /// instant as the later booking's <c>ApprovalExpiresAtUtc</c>.
    /// </summary>
    public DateTime ExpiresAtUtc { get; private set; }

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
        bool asksEarlierGuest)
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
        Status = TableShareRequestStatus.Pending;
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
