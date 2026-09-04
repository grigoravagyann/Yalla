using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Audit;

/// <summary>
/// An append-only log of every transition of every table's status.
/// </summary>
/// <remarks>
/// <para>
/// Cheap to write and hard to reconstruct after the fact. It answers the argument that actually
/// happens on a Friday night - "somebody gave away my reserved table" - with a row naming who
/// changed what, when and why.
/// </para>
/// <para>
/// It is also the raw material for the turnover reporting sold to owners later: how long tables
/// sat empty between covers, how often bookings were released as no-shows, which areas turn
/// fastest. None of that can be backfilled, which is why the log starts on day one.
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
        Guid? actorId = null,
        Guid? reservationId = null,
        Guid? tabId = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        FromStatus = Guard.Defined(fromStatus, nameof(fromStatus));
        ToStatus = Guard.Defined(toStatus, nameof(toStatus));
        Reason = Guard.NotBlank(reason, nameof(reason), FieldLengths.Reason);
        ActorType = Guard.Defined(actorType, nameof(actorType));
        AtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        ReservationId = reservationId;
        TabId = tabId;

        if (actorType != ActorType.System && actorId is null)
        {
            throw new ArgumentException(
                "A change made by a diner or a staff member must name them.", nameof(actorId));
        }

        ActorId = actorId;
    }
}
