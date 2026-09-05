using Yalla.Domain.Enums;

namespace Yalla.Application.Floor;

/// <summary>
/// One entry in a branch's change stream, as a client catching up reads it.
/// </summary>
/// <param name="Sequence">Position in the stream. Strictly increasing per branch.</param>
/// <param name="TableStateChangeId">The audit row this came from.</param>
/// <param name="DiningTableId">The table that changed.</param>
/// <param name="TableLabel">Its label, so a client can render without a second lookup.</param>
/// <param name="FromStatus">1 Free, 2 Held, 4 Occupied, 5 OutOfService.</param>
/// <param name="ToStatus">The status after the change.</param>
/// <param name="Reason">Free text recorded with the transition.</param>
/// <param name="ActorType">1 Diner, 2 Staff, 3 System.</param>
/// <param name="ActorId">Who acted. Null only for a System change.</param>
/// <param name="AtUtc">When it happened.</param>
/// <param name="TableSessionId">The seating opened or closed by this change, when there was one.</param>
/// <param name="ReservationId">The booking involved, when there was one.</param>
public sealed record BranchChange(
    long Sequence,
    Guid TableStateChangeId,
    Guid DiningTableId,
    string TableLabel,
    TableStatus FromStatus,
    TableStatus ToStatus,
    string Reason,
    ActorType ActorType,
    Guid? ActorId,
    DateTime AtUtc,
    Guid? TableSessionId,
    Guid? ReservationId);

/// <summary>
/// A page of a branch's change stream, and where the stream currently ends.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the realtime hub will plug into. A client that lost its connection has a
/// sequence number from its last floor response or change page; it asks for everything after that
/// and applies the entries in order, rather than refetching the whole floor and working out what
/// moved. The hub, when it exists, pushes the same entries.
/// </para>
/// <para>
/// <see cref="MaxSequence"/> is the branch's end of stream, not the last entry in this page. A
/// client that receives fewer entries than it asked for still learns it is up to date, and one
/// that is far behind learns how far without paging to the end.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch.</param>
/// <param name="AfterSequence">The sequence the caller asked to start after.</param>
/// <param name="MaxSequence">The branch's highest sequence right now. Zero when nothing has happened.</param>
/// <param name="HasMore">
/// True when the page hit its cap and there are further entries before <paramref name="MaxSequence"/>.
/// </param>
/// <param name="Changes">The entries, in sequence order.</param>
public sealed record BranchChangePage(
    Guid BranchId,
    long AfterSequence,
    long MaxSequence,
    bool HasMore,
    IReadOnlyList<BranchChange> Changes);
