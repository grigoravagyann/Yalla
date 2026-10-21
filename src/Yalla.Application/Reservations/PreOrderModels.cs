using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>A booking's pre-order, as every read gives it - the guest's, the waiter's and the console's alike.</summary>
/// <param name="Id">The pre-order - and its kitchen ticket, and the order on the party's tab it becomes.</param>
/// <param name="ReservationId">Its booking.</param>
/// <param name="State">1 Editable, 2 Locked, 3 Sent, 4 NotComing, 5 Cancelled - derived on every read.</param>
/// <param name="Lines">The dishes, as the guest listed them, at their locked prices.</param>
/// <param name="Note">The booking-level note. Absent when none.</param>
/// <param name="TotalAmd">The lines at their locked prices. No service charge: that is the tab's.</param>
/// <param name="EditableUntilUtc">Two hours before the start: the guest may change it until then.</param>
/// <param name="DueAtUtc">When the waiter's card appears: the branch's minutes before the start.</param>
/// <param name="SentAtUtc">When a waiter sent it to the kitchen. Absent until then.</param>
/// <param name="KitchenStatus">
/// Where its ticket is on the kitchen rail once sent (2 InKitchen, 3 Ready, 4 Served, 5 Voided) - the held ticket's
/// until the party sits, then the tab order's.
/// </param>
/// <param name="OnTab">It is an order on the party's tab.</param>
/// <param name="NotComingAtUtc">A waiter marked the party "not coming / no answer".</param>
/// <param name="UpdatedAtUtc">Last changed.</param>
public sealed record PreOrderView(
    Guid Id,
    Guid ReservationId,
    PreOrderState State,
    IReadOnlyList<PreOrderLineView> Lines,
    string? Note,
    long TotalAmd,
    DateTime EditableUntilUtc,
    DateTime DueAtUtc,
    DateTime? SentAtUtc,
    TabOrderStatus? KitchenStatus,
    bool OnTab,
    DateTime? NotComingAtUtc,
    DateTime UpdatedAtUtc);

/// <param name="Id">The line.</param>
/// <param name="MenuItemId">The dish.</param>
/// <param name="Name">Its name when it was ordered.</param>
/// <param name="UnitPriceAmd">Its locked price.</param>
/// <param name="Quantity">How many, 1 to 20.</param>
/// <param name="LineTotalAmd"><paramref name="UnitPriceAmd"/> times <paramref name="Quantity"/>.</param>
/// <param name="Note">For the kitchen. Absent when none.</param>
/// <param name="AvailableNow">
/// False when the dish has since sold out, been excluded from pre-orders or left the menu. It stays on the pre-order -
/// it was promised - but a save that keeps it is refused.
/// </param>
public sealed record PreOrderLineView(
    Guid Id,
    Guid MenuItemId,
    string Name,
    long UnitPriceAmd,
    int Quantity,
    long LineTotalAmd,
    string? Note,
    bool AvailableNow);

/// <summary>The answer to saving or clearing a pre-order.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="PreOrder">The pre-order as it stands. Absent after clearing.</param>
/// <param name="WasReplay">The same command arrived before; nothing changed this time.</param>
public sealed record PreOrderResult(Guid ReservationId, PreOrderView? PreOrder, bool WasReplay);

/// <summary>One dish as the guest sends it.</summary>
public sealed record PreOrderLineCommand(Guid MenuItemId, int Quantity, string? Note);

/// <summary>Replace the whole pre-order with these lines and this note.</summary>
public sealed record ReplacePreOrderCommand(
    Guid ReservationId,
    IReadOnlyList<PreOrderLineCommand> Lines,
    string? Note,
    Guid ClientCommandId);

/// <summary>The same, from a booking's manage link.</summary>
public sealed record ReplacePreOrderByManageTokenCommand(
    IReadOnlyList<PreOrderLineCommand> Lines,
    string? Note,
    Guid ClientCommandId);

/// <summary>The answer to "Yes, on my way".</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="GuestConfirmedOnWayAtUtc">When the guest first said so.</param>
/// <param name="WasReplay">They had said so already; this changed nothing.</param>
public sealed record OnMyWayResult(Guid ReservationId, DateTime GuestConfirmedOnWayAtUtc, bool WasReplay);

/// <summary>One card on the floor tablet's pre-order rail - and the answer to the waiter's two actions.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="TableId">Its table.</param>
/// <param name="TableLabel">The table, as printed on it.</param>
/// <param name="RoomName">The table's room. Absent when none.</param>
/// <param name="StartUtc">When the booking starts.</param>
/// <param name="LocalStartTime">The same, on the branch's wall clock.</param>
/// <param name="PartySize">How many.</param>
/// <param name="Status">The booking's: 2 Confirmed or 4 Seated on the rail.</param>
/// <param name="GuestName">Who booked.</param>
/// <param name="GuestPhone">Their number, to call. Empty for a platform admin: the venue's to call.</param>
/// <param name="GuestConfirmedOnWayAtUtc">"Yes, on my way" - no need to call. Absent until they say it.</param>
/// <param name="GuestRecord">The guest's record, under the console list's rule. Absent where not shown.</param>
/// <param name="DueAtUtc">When this card appeared (or will).</param>
/// <param name="PreOrder">The pre-order.</param>
/// <param name="SentByName">The waiter who sent it, as the PIN roster names them. Absent until sent.</param>
/// <param name="NotComingByName">The waiter who marked the party not coming. Absent unless marked.</param>
/// <param name="TabId">The party's tab, once it is an order on it.</param>
/// <param name="WasReplay">On an action's answer: it had been done already, and nothing changed this time.</param>
public sealed record DuePreOrder(
    Guid ReservationId,
    string Code,
    Guid TableId,
    string TableLabel,
    string? RoomName,
    DateTime StartUtc,
    TimeOnly LocalStartTime,
    int PartySize,
    ReservationStatus Status,
    string GuestName,
    string GuestPhone,
    DateTime? GuestConfirmedOnWayAtUtc,
    GuestRecord? GuestRecord,
    DateTime DueAtUtc,
    PreOrderView PreOrder,
    string? SentByName,
    string? NotComingByName,
    Guid? TabId,
    bool WasReplay = false);

/// <summary>The branch's pre-order rail.</summary>
/// <param name="Items">The cards due now, soonest first.</param>
/// <param name="NextDueAtUtc">
/// When the next card will appear - read the rail again then. Absent when no other booking with a pre-order is coming.
/// </param>
public sealed record DuePreOrders(IReadOnlyList<DuePreOrder> Items, DateTime? NextDueAtUtc);

/// <summary>A waiter's action on a pre-order card.</summary>
public sealed record PreOrderStaffCommand(Guid ReservationId, Guid ClientCommandId);

/// <summary>The branch's pre-order settings, as the console edits them.</summary>
/// <param name="AcceptsPreOrders">Guests may pre-order on their bookings. Off by default.</param>
/// <param name="MinutesBefore">How long before a booking its card appears on the floor tablet. 5 to 120, default 30.</param>
/// <param name="ExcludedMenuItemIds">The dishes guests may not pre-order. Every other dish on the menu may be.</param>
public sealed record PreOrderSettings(bool AcceptsPreOrders, int MinutesBefore, IReadOnlyList<Guid> ExcludedMenuItemIds);

/// <summary>The settings with the bounds and the product's fixed numbers, for the console's form.</summary>
public sealed record PreOrderSettingsView(
    bool AcceptsPreOrders,
    int MinutesBefore,
    IReadOnlyList<Guid> ExcludedMenuItemIds,
    PreOrderSettingsBounds Bounds,
    int EditableUntilHoursBefore,
    int OnYourWayMinutesBefore);

/// <summary>What <c>minutesBefore</c> may be.</summary>
public sealed record PreOrderSettingsBounds(int MinutesBeforeMin, int MinutesBeforeMax);

/// <summary>Pre-orders: the guest's edits, "on my way", the waiter's rail and its two actions, and the console's settings.</summary>
public interface IPreOrderService
{
    Task<PreOrderResult> ReplaceAsync(ReplacePreOrderCommand command, CancellationToken cancellationToken = default);

    Task<PreOrderResult> ClearAsync(Guid reservationId, Guid clientCommandId, CancellationToken cancellationToken = default);

    Task<PreOrderResult> ReplaceByManageTokenAsync(
        string token, ReplacePreOrderByManageTokenCommand command, CancellationToken cancellationToken = default);

    Task<PreOrderResult> ClearByManageTokenAsync(
        string token, Guid clientCommandId, CancellationToken cancellationToken = default);

    Task<OnMyWayResult> ConfirmOnMyWayAsync(Guid reservationId, CancellationToken cancellationToken = default);

    Task<OnMyWayResult> ConfirmOnMyWayByManageTokenAsync(string token, CancellationToken cancellationToken = default);

    Task<DuePreOrders> ListDueAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<DuePreOrder> SendAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default);

    Task<DuePreOrder> MarkNotComingAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default);

    Task<PreOrderSettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<PreOrderSettingsView> SaveSettingsAsync(
        Guid branchId, PreOrderSettings settings, CancellationToken cancellationToken = default);
}
