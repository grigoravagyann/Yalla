using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>
/// Everything about one tab that any participant's view could need, read once and unfiltered.
/// </summary>
/// <remarks>
/// <para>
/// This is the input to <see cref="TabProjection"/>, and the reason the projection can be a pure
/// function: the query loads the whole tab, and the projection decides what the viewer gets. It
/// is never returned to a client - it carries every line and every flag - which is the point. If
/// the query filtered per viewer there would be two places deciding visibility, and the
/// disagreement between them would be a leak.
/// </para>
/// <para>
/// Participants here include removed ones, because a line they placed still names them. The
/// projection is what keeps them out of the roster.
/// </para>
/// </remarks>
public sealed record TabSnapshot(
    Guid TabId,
    Guid BranchId,
    Guid DiningTableId,
    string TableLabel,
    TabStatus Status,
    SettlementMode SettlementMode,
    DateTime? SettlementModeLockedAtUtc,
    bool HideTotalFromGuests,
    Guid? HostParticipantId,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long RemainingAmd,
    IReadOnlyList<TabParticipantSnapshot> Participants,
    IReadOnlyList<TabLineSnapshot> Lines);

/// <summary>One participant, every flag, no filtering.</summary>
public sealed record TabParticipantSnapshot(
    Guid ParticipantId,
    string DisplayName,
    ParticipantRole Role,
    ParticipantStatus Status,
    bool CanOrder,
    bool CanSeeTableTotal,
    bool CanPay,
    DateTime JoinedAtUtc);

/// <summary>One order line with who placed it and, for a shared item, who was present.</summary>
public sealed record TabLineSnapshot(
    Guid LineId,
    Guid? PlacedByParticipantId,
    string Name,
    long UnitPriceAmd,
    int Quantity,
    bool IsShared,
    bool IsVoided,
    IReadOnlyList<Guid> SharedWithParticipantIds)
{
    /// <summary>Unit price times quantity; zero once voided, as on the entity.</summary>
    public long LineTotalAmd => IsVoided ? 0L : UnitPriceAmd * Quantity;
}
