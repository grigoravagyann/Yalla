using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Audit;

/// <summary>
/// An append-only log of every transition of every table's status.
/// </summary>
/// <remarks>
/// <para>
/// Cheap to write and hard to reconstruct after the fact. It settles the argument that actually
/// happens on a Friday night - "somebody gave away my reserved table" - with a row naming who
/// changed what, when and why.
/// </para>
/// <para>
/// It is also the raw material for the turnover reporting sold to owners later: how long tables
/// sat empty between covers, how often bookings were released as no-shows, which areas turn
/// fastest. None of that can be backfilled, which is why the log starts on day one.
/// </para>
/// <para>
/// And it is the <b>idempotency ledger</b>. See <see cref="ClientCommandId"/>.
/// </para>
/// </remarks>
public sealed class TableStateChange : Entity
{
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DiningTableId { get; private set; }

    public DiningTable DiningTable { get; private set; } = null!;

    public TableStatus FromStatus { get; private set; }

    public TableStatus ToStatus { get; private set; }

    /// <summary>Why it changed, in words: "seated walk-in", "grace expired", "staff released".</summary>
    public string Reason { get; private set; } = null!;

    public ActorType ActorType { get; private set; }

    /// <summary>
    /// The diner or staff member responsible. Null when
    /// <see cref="Enums.ActorType.System"/> did it - a hold lapsing has no author.
    /// </summary>
    public Guid? ActorId { get; private set; }

    public DateTime AtUtc { get; private set; }

    /// <summary>The booking involved, if the change was about one.</summary>
    public Guid? ReservationId { get; private set; }

    /// <summary>The tab involved, if the change was about one.</summary>
    public Guid? TabId { get; private set; }

    /// <summary>
    /// The occupancy this change opened or closed, if any.
    /// </summary>
    /// <remarks>
    /// Recorded so a replayed command can return exactly the same answer as the original,
    /// including the session id - not merely "this already happened". Without it, resolving the
    /// session for a replay means guessing from timestamps.
    /// </remarks>
    public Guid? TableSessionId { get; private set; }

    /// <summary>
    /// The caller's own id for the command that produced this row. Unique across the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The staff tablet queues state changes locally when the cafe wifi drops and replays them on
    /// reconnect, so "seat table 7" <i>will</i> arrive twice. This column, with its unique index,
    /// is what makes the second arrival a no-op that returns the first one's result instead of a
    /// second session and a second audit row.
    /// </para>
    /// <para>
    /// The unique index is the real guarantee. A check-then-insert loses the race between two
    /// simultaneous replays; the index does not, and the loser is caught and answered from the
    /// existing row.
    /// </para>
    /// </remarks>
    public Guid ClientCommandId { get; private set; }

    /// <summary>
    /// Position in the branch's change stream. Assigned by the database, never by the application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every state change already writes exactly one of these rows in the same transaction as the
    /// change itself, which makes this table an event log whether or not anybody reads it as one.
    /// Giving it an order now is what lets a client that dropped its connection ask "what have I
    /// missed since 4,812?" instead of refetching the whole floor.
    /// </para>
    /// <para>
    /// <c>CreatedAtUtc</c> is not an ordering. Two changes in the same millisecond tie, and a clock
    /// that steps backwards would interleave them wrongly. An identity column is monotonic by
    /// construction.
    /// </para>
    /// <para>
    /// Added before the hub that needs it, deliberately: adding an identity column to a live,
    /// growing audit table later is a far more painful migration than adding it to an empty one.
    /// </para>
    /// </remarks>
    public long Sequence { get; private set; }

    private TableStateChange()
    {
    }

    public TableStateChange(
        Guid branchId,
        Guid diningTableId,
        TableStatus fromStatus,
        TableStatus toStatus,
        string reason,
        ActorType actorType,
        DateTime atUtc,
        Guid clientCommandId,
        Guid? actorId = null,
        Guid? reservationId = null,
        Guid? tabId = null,
        Guid? tableSessionId = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        FromStatus = Guard.Defined(fromStatus, nameof(fromStatus));
        ToStatus = Guard.Defined(toStatus, nameof(toStatus));
        Reason = Guard.NotBlank(reason, nameof(reason), FieldLengths.Reason);
        ActorType = Guard.Defined(actorType, nameof(actorType));
        AtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        ClientCommandId = Guard.NotEmpty(clientCommandId, nameof(clientCommandId));
        ReservationId = reservationId;
        TabId = tabId;
        TableSessionId = tableSessionId;

        if (actorType != ActorType.System && actorId is null)
        {
            throw new ArgumentException(
                "A change made by a diner or a staff member must name them.", nameof(actorId));
        }

        ActorId = actorId;
    }
}
