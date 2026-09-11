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
/// <para>
/// <b>Voided lines are here too, and so are adjustments.</b> They are excluded from every total and
/// included in what the diner sees, which is a rule about the projection and not about the read -
/// see <see cref="TabProjection"/>.
/// </para>
/// </remarks>
/// <param name="TabId">The tab.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="VenueName">The venue's name, so the tab can say where it is.</param>
/// <param name="BranchName">The branch's name.</param>
/// <param name="DiningTableId">The table.</param>
/// <param name="TableLabel">The label printed on it.</param>
/// <param name="TimeZoneId">
/// The branch's IANA zone. Carried so the client can render every instant in the venue's wall
/// clock rather than the phone's - a diner on holiday must not be told their food is ready at a
/// time that means nothing where they are sitting.
/// </param>
/// <param name="Status">The tab's status.</param>
/// <param name="SettlementMode">How the bill splits.</param>
/// <param name="SettlementModeLockedAtUtc">When the split was frozen, if it has been.</param>
/// <param name="HideTotalFromGuests">The table default for a joiner's visibility flag.</param>
/// <param name="HostParticipantId">Who hosts.</param>
/// <param name="ServiceChargePercentSnapshot">
/// The branch's service charge as it stood when this tab opened. A fact about the venue, shown to
/// everyone.
/// </param>
/// <param name="OpenedAtUtc">When the tab opened.</param>
/// <param name="ClosedAtUtc">When it closed, if it has.</param>
/// <param name="SubtotalAmd">Cached subtotal.</param>
/// <param name="ServiceChargeAmd">Cached service charge.</param>
/// <param name="TotalAmd">Cached total.</param>
/// <param name="PaidAmd">Cached paid.</param>
/// <param name="RemainingAmd">Cached remaining.</param>
/// <param name="MaxEventSequence">
/// The tab's newest event position, so a client knows where it stands without a second call to
/// <c>/events</c> purely to find out.
/// </param>
/// <param name="Participants">Everyone who has been on the tab, removed ones included.</param>
/// <param name="Lines">Every line, voided ones included.</param>
/// <param name="Adjustments">Every comp and discount, voided ones included.</param>
public sealed record TabSnapshot(
    Guid TabId,
    Guid BranchId,
    string VenueName,
    string BranchName,
    Guid DiningTableId,
    string TableLabel,
    string TimeZoneId,
    TabStatus Status,
    SettlementMode SettlementMode,
    DateTime? SettlementModeLockedAtUtc,
    bool HideTotalFromGuests,
    Guid? HostParticipantId,
    decimal ServiceChargePercentSnapshot,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long RemainingAmd,
    long MaxEventSequence,
    IReadOnlyList<TabParticipantSnapshot> Participants,
    IReadOnlyList<TabLineSnapshot> Lines,
    IReadOnlyList<TabAdjustmentSnapshot> Adjustments);

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

/// <summary>
/// One order line with who placed it and, for a shared item, who was present.
/// </summary>
/// <param name="LineId">The order line.</param>
/// <param name="OrderId">The order it was part of, so a client can group a round together.</param>
/// <param name="MenuItemId">The item, so the client can link to the menu entry.</param>
/// <param name="PlacedByParticipantId">Who ordered it from their phone. Null when a waiter keyed it in.</param>
/// <param name="Name">The item name as it read on the menu when ordered.</param>
/// <param name="UnitPriceAmd">Unit price in whole dram as it stood when ordered.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Note">The kitchen note, e.g. "no onions".</param>
/// <param name="OrderStatus">Where the order is on the kitchen rail.</param>
/// <param name="IsShared">True when the item belongs to the table and is split across those present.</param>
/// <param name="IsVoided">True once staff took it off the bill.</param>
/// <param name="VoidedAtUtc">When.</param>
/// <param name="VoidReason">Why, in the waiter's own words.</param>
/// <param name="SharedWithParticipantIds">
/// Who was at the table when a shared line was ordered. <b>Snapshotted, not current</b>: a friend
/// who arrives ten minutes later is not on the bottle that has already been poured.
/// </param>
public sealed record TabLineSnapshot(
    Guid LineId,
    Guid OrderId,
    Guid MenuItemId,
    Guid? PlacedByParticipantId,
    string Name,
    long UnitPriceAmd,
    int Quantity,
    string? Note,
    TabOrderStatus OrderStatus,
    bool IsShared,
    bool IsVoided,
    DateTime? VoidedAtUtc,
    string? VoidReason,
    IReadOnlyList<Guid> SharedWithParticipantIds)
{
    /// <summary>Unit price times quantity; zero once voided, as on the entity.</summary>
    public long LineTotalAmd => IsVoided ? 0L : UnitPriceAmd * Quantity;
}

/// <summary>
/// One comp or discount a manager applied.
/// </summary>
/// <remarks>
/// On the snapshot because it belongs on the diner's bill. A total that drops with no visible cause
/// is the fastest way to make somebody distrust the app, and it is a waiter who then has to explain
/// it at the table.
/// </remarks>
/// <param name="AdjustmentId">The adjustment row.</param>
/// <param name="TabOrderLineId">The line it applies to, or null for one against the whole tab.</param>
/// <param name="Kind">1 Discount, 2 Comp.</param>
/// <param name="Percent">The percentage, when it is one.</param>
/// <param name="AmountAmd">The flat amount, when it is one.</param>
/// <param name="ReductionAmd">What it actually took off, in whole dram.</param>
/// <param name="Reason">What the manager typed. The part a diner reads.</param>
/// <param name="CreatedAtUtc">When it was applied.</param>
/// <param name="IsVoided">True once it was reversed. It stays on the record, marked.</param>
public sealed record TabAdjustmentSnapshot(
    Guid AdjustmentId,
    Guid? TabOrderLineId,
    AdjustmentKind Kind,
    decimal? Percent,
    long? AmountAmd,
    long ReductionAmd,
    string Reason,
    DateTime CreatedAtUtc,
    bool IsVoided);
