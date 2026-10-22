using Yalla.Domain.Takeaway;

namespace Yalla.Application.Takeaway;

// ---------------------------------------------------------------- the console's settings

/// <summary>The branch's takeaway settings, as the console saves them.</summary>
/// <param name="Enabled">Guests may order to take away. Off by default.</param>
/// <param name="AutoAccept">Orders are accepted without a tap, ready at the time asked for. Off by default.</param>
/// <param name="PrepMinutes">The earliest an "as soon as possible" order is ready: 5 to 90, in fives. Default 20.</param>
/// <param name="LastOrderMinutes">Ordering closes this long before closing: 0 to 120, in fifteens. Default 30.</param>
/// <param name="MaxInProgress">How many orders may be sent or accepted at once: 1 to 30. Default 5.</param>
public sealed record TakeawaySettings(bool Enabled, bool AutoAccept, int PrepMinutes, int LastOrderMinutes, int MaxInProgress);

/// <summary>The settings with their bounds and the product's fixed numbers, for the console's card.</summary>
/// <param name="Enabled">Guests may order to take away.</param>
/// <param name="AutoAccept">Accepted without a tap.</param>
/// <param name="PrepMinutes">The preparation time.</param>
/// <param name="LastOrderMinutes">How long before closing ordering stops.</param>
/// <param name="MaxInProgress">How many orders may be in progress at once.</param>
/// <param name="Bounds">What each number may be.</param>
/// <param name="AcceptWindowMinutes">An order nobody accepts within this is declined (5) - when auto-accept is off.</param>
/// <param name="NoShowAfterMinutes">"No-show" may be marked this long after ready (30).</param>
public sealed record TakeawaySettingsView(
    bool Enabled,
    bool AutoAccept,
    int PrepMinutes,
    int LastOrderMinutes,
    int MaxInProgress,
    TakeawaySettingsBounds Bounds,
    int AcceptWindowMinutes,
    int NoShowAfterMinutes);

/// <summary>What each takeaway setting may be: inclusive bounds and the stepper's step.</summary>
public sealed record TakeawaySettingsBounds(
    int PrepMinutesMin,
    int PrepMinutesMax,
    int PrepMinutesStep,
    int LastOrderMinutesMin,
    int LastOrderMinutesMax,
    int LastOrderMinutesStep,
    int MaxInProgressMin,
    int MaxInProgressMax);

// ---------------------------------------------------------------- the diner

/// <summary>Whether a branch takes takeaway now, and the pickup times it offers - for the checkout screen.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="Enabled">The venue takes takeaway at all. False: no button.</param>
/// <param name="Open">Orders are taken now: inside opening hours, before the last order.</param>
/// <param name="PrepMinutes">The venue's preparation time - "ready in about 20 minutes".</param>
/// <param name="AsapReadyAtUtc">"As soon as possible" would be ready at this instant. Absent when closed.</param>
/// <param name="AsapReadyAt">The same on the branch's wall clock, <c>HH:mm</c>. Absent when closed.</param>
/// <param name="Slots">
/// The pickup times offered today, on the branch's wall clock (<c>HH:mm</c>), in quarter hours from the ASAP time to the
/// last pickup. A time that would put more orders in the kitchen than the venue allows is left out. Empty when closed.
/// </param>
/// <param name="LastPickupAt">The latest pickup today, <c>HH:mm</c>: closing minus the last-order minutes. Absent when closed.</param>
/// <param name="Busy">The kitchen has as many orders in progress as the venue allows: ASAP is refused; a slot may still work.</param>
/// <param name="OpensAt">Closed: the next opening an order can be taken from, <c>HH:mm</c> on its day. Absent otherwise.</param>
/// <param name="OpensAtUtc">The same instant. Absent with <paramref name="OpensAt"/>.</param>
public sealed record TakeawayAvailability(
    Guid BranchId,
    bool Enabled,
    bool Open,
    int PrepMinutes,
    DateTime? AsapReadyAtUtc,
    string? AsapReadyAt,
    IReadOnlyList<string> Slots,
    string? LastPickupAt,
    bool Busy,
    string? OpensAt,
    DateTime? OpensAtUtc);

/// <summary>One dish as the guest orders it.</summary>
public sealed record TakeawayLineCommand(Guid MenuItemId, int Quantity);

/// <summary>Place a takeaway order.</summary>
/// <param name="BranchId">Where.</param>
/// <param name="Asap">As soon as possible. Exactly one of this and <paramref name="At"/>.</param>
/// <param name="At">A pickup slot, local <c>HH:mm</c>, one of the availability's <c>slots</c>.</param>
/// <param name="Note">For the venue, at most 500 characters.</param>
/// <param name="Lines">The dishes.</param>
/// <param name="ClientCommandId">Idempotency: a retry answers the order the first try made.</param>
public sealed record PlaceTakeawayOrderCommand(
    Guid BranchId,
    bool Asap,
    string? At,
    string? Note,
    IReadOnlyList<TakeawayLineCommand> Lines,
    Guid ClientCommandId);

/// <summary>One line of a takeaway order, at the name and price it was ordered at.</summary>
public sealed record TakeawayLineView(Guid Id, Guid MenuItemId, string Name, long UnitPriceAmd, int Quantity, long LineTotalAmd);

/// <summary>
/// A takeaway order as its guest sees it - the status screen.
/// </summary>
/// <param name="Id">The order.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="VenueName">The venue.</param>
/// <param name="BranchName">Which location.</param>
/// <param name="BranchAddress">Where to pick it up.</param>
/// <param name="BranchPhoneE164">The venue's number, to call. Absent when it has none on file.</param>
/// <param name="Latitude">For directions.</param>
/// <param name="Longitude">For directions.</param>
/// <param name="Code">"T-482" - what to say at the counter.</param>
/// <param name="Status">1 Sent, 2 Accepted ("Accepted · being prepared"), 3 Ready, 4 Collected, 5 Declined, 6 Cancelled, 7 NoShow.</param>
/// <param name="PickupAsap">Ordered for as soon as possible.</param>
/// <param name="RequestedPickupUtc">When the guest asked for it.</param>
/// <param name="RequestedPickupAt">The same, branch wall clock <c>HH:mm</c>.</param>
/// <param name="ReadyAtUtc">When the venue said it will be ready. Absent until accepted.</param>
/// <param name="ReadyAt">The same, <c>HH:mm</c>.</param>
/// <param name="Note">The guest's note.</param>
/// <param name="Lines">What was ordered.</param>
/// <param name="TotalAmd">To pay at pickup.</param>
/// <param name="PaymentMethod">How it was paid: 1 Cash, 2 Card. Absent until collected.</param>
/// <param name="PaidAmountAmd">What was paid. Absent until collected.</param>
/// <param name="SentAtUtc">When it was sent.</param>
/// <param name="AcceptedAtUtc">When the venue accepted it.</param>
/// <param name="ReadyMarkedAtUtc">When the kitchen marked it ready.</param>
/// <param name="CollectedAtUtc">When it was handed over.</param>
/// <param name="DeclinedAtUtc">When it was declined.</param>
/// <param name="CancelledAtUtc">When the guest cancelled.</param>
/// <param name="NoShowAtUtc">When it was marked not collected.</param>
/// <param name="DeclinedBecause">1 the venue said no, 2 nobody answered in time. Absent unless declined.</param>
/// <param name="DeclineReason">What the venue said, when it said why.</param>
/// <param name="AcceptDeadlineUtc">While sent: when it is declined if nobody answers.</param>
/// <param name="CanCancel">True while sent - after that, a phone call.</param>
/// <param name="WasReplay">On the answer to placing it: the same <c>clientCommandId</c> placed it before.</param>
public sealed record TakeawayOrderView(
    Guid Id,
    Guid BranchId,
    string VenueName,
    string BranchName,
    string BranchAddress,
    string? BranchPhoneE164,
    double Latitude,
    double Longitude,
    string Code,
    TakeawayStatus Status,
    bool PickupAsap,
    DateTime RequestedPickupUtc,
    string RequestedPickupAt,
    DateTime? ReadyAtUtc,
    string? ReadyAt,
    string? Note,
    IReadOnlyList<TakeawayLineView> Lines,
    long TotalAmd,
    TakeawayPaymentMethod? PaymentMethod,
    long? PaidAmountAmd,
    DateTime SentAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? ReadyMarkedAtUtc,
    DateTime? CollectedAtUtc,
    DateTime? DeclinedAtUtc,
    DateTime? CancelledAtUtc,
    DateTime? NoShowAtUtc,
    TakeawayDeclineCause? DeclinedBecause,
    string? DeclineReason,
    DateTime? AcceptDeadlineUtc,
    bool CanCancel,
    bool WasReplay = false);

// ---------------------------------------------------------------- the floor tablet

/// <summary>
/// A takeaway order as the floor tablet and the counter see it.
/// </summary>
/// <param name="Id">The order - the id the actions take.</param>
/// <param name="Code">"T-482".</param>
/// <param name="Status">1 Sent, 2 Accepted, 3 Ready, 4 Collected, 5 Declined, 6 Cancelled, 7 NoShow.</param>
/// <param name="GuestName">Who to call out.</param>
/// <param name="GuestPhone">Their proved number, to call. Empty for a platform admin: the venue's to call.</param>
/// <param name="PickupAsap">As soon as possible, rather than a slot.</param>
/// <param name="RequestedPickupUtc">When the guest wants it - the stepper's starting point.</param>
/// <param name="RequestedPickupAt">The same, <c>HH:mm</c>.</param>
/// <param name="ReadyAtUtc">When it was promised. Absent until accepted.</param>
/// <param name="ReadyAt">The same, <c>HH:mm</c>.</param>
/// <param name="Note">The guest's note.</param>
/// <param name="Lines">What to cook.</param>
/// <param name="TotalAmd">To take at the counter.</param>
/// <param name="SentAtUtc">When it came in.</param>
/// <param name="AcceptDeadlineUtc">While sent: when it is declined for nobody answering - the countdown. Absent with auto-accept.</param>
/// <param name="AcceptedAtUtc">When it was accepted.</param>
/// <param name="AcceptedByName">Who accepted it; absent when accepted automatically.</param>
/// <param name="ReadyMarkedAtUtc">When the kitchen marked it ready - "waiting 35 min" counts from here.</param>
/// <param name="NoShowFromUtc">From when "no-show" may be marked: thirty minutes after ready. Absent until accepted.</param>
/// <param name="CollectedAtUtc">When it was handed over.</param>
/// <param name="PaymentMethod">1 Cash, 2 Card, once collected.</param>
/// <param name="PaidAmountAmd">What was taken, once collected.</param>
/// <param name="DeclinedAtUtc">When it was declined.</param>
/// <param name="DeclinedBecause">1 staff, 2 nobody answered.</param>
/// <param name="DeclineReason">What staff said.</param>
/// <param name="NoShowAtUtc">When it was marked not collected.</param>
/// <param name="CancelledAtUtc">When the guest cancelled.</param>
/// <param name="WasReplay">On an action's answer: it had been done already, and nothing changed this time.</param>
public sealed record StaffTakeawayOrderView(
    Guid Id,
    string Code,
    TakeawayStatus Status,
    string GuestName,
    string GuestPhone,
    bool PickupAsap,
    DateTime RequestedPickupUtc,
    string RequestedPickupAt,
    DateTime? ReadyAtUtc,
    string? ReadyAt,
    string? Note,
    IReadOnlyList<TakeawayLineView> Lines,
    long TotalAmd,
    DateTime SentAtUtc,
    DateTime? AcceptDeadlineUtc,
    DateTime? AcceptedAtUtc,
    string? AcceptedByName,
    DateTime? ReadyMarkedAtUtc,
    DateTime? NoShowFromUtc,
    DateTime? CollectedAtUtc,
    TakeawayPaymentMethod? PaymentMethod,
    long? PaidAmountAmd,
    DateTime? DeclinedAtUtc,
    TakeawayDeclineCause? DeclinedBecause,
    string? DeclineReason,
    DateTime? NoShowAtUtc,
    DateTime? CancelledAtUtc,
    bool WasReplay = false);

/// <summary>A branch's takeaway counter.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="Active">Sent, Accepted and Ready - the rail's and the counter's first two columns. Oldest first.</param>
/// <param name="FinishedToday">Collected, NoShow and Declined on the branch's service day, newest first.</param>
/// <param name="SentCount">How many wait for an answer - with <paramref name="ReadyCount"/>, the tab's badge.</param>
/// <param name="ReadyCount">How many wait at the counter.</param>
/// <param name="AsOfUtc">When it was read.</param>
public sealed record BranchTakeawayOrders(
    Guid BranchId,
    IReadOnlyList<StaffTakeawayOrderView> Active,
    IReadOnlyList<StaffTakeawayOrderView> FinishedToday,
    int SentCount,
    int ReadyCount,
    DateTime AsOfUtc);

/// <summary>Takeaway orders: the diner's, the floor's, the kitchen's one move, the console's settings, and the sweep.</summary>
public interface ITakeawayService
{
    /// <summary>Whether the branch takes takeaway now, and the pickup times on offer.</summary>
    Task<TakeawayAvailability> GetAvailabilityAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Places an order. Idempotent on <c>clientCommandId</c>.</summary>
    Task<TakeawayOrderView> PlaceAsync(PlaceTakeawayOrderCommand command, CancellationToken cancellationToken = default);

    /// <summary>One of the caller's orders. Somebody else's is not found.</summary>
    Task<TakeawayOrderView> GetMineAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>The guest cancels, only while sent.</summary>
    Task<TakeawayOrderView> CancelAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>The branch's active orders and today's finished ones.</summary>
    Task<BranchTakeawayOrders> ListForBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<StaffTakeawayOrderView> AcceptAsync(Guid orderId, DateTime readyAtUtc, CancellationToken cancellationToken = default);

    Task<StaffTakeawayOrderView> DeclineAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>The kitchen's one move - and the floor's.</summary>
    Task<StaffTakeawayOrderView> MarkReadyAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<StaffTakeawayOrderView> CollectAsync(
        Guid orderId, TakeawayPaymentMethod method, CancellationToken cancellationToken = default);

    Task<StaffTakeawayOrderView> MarkNoShowAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<TakeawaySettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<TakeawaySettingsView> SaveSettingsAsync(
        Guid branchId, TakeawaySettings settings, CancellationToken cancellationToken = default);
}
