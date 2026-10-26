using Yalla.Domain.Enums;

namespace Yalla.Application.Ordering;

// ---------------------------------------------------------------- commands

/// <summary>One item on an order being placed.</summary>
/// <param name="MenuItemId">Which dish.</param>
/// <param name="Quantity">How many. At least one.</param>
/// <param name="IsShared">
/// True for something the table shares. It snapshots the approved participants present at this
/// moment - a friend who joins later is not on it, and one who is removed still is.
/// </param>
/// <param name="Note">What the guest asked for: no onions. Goes to the kitchen, not the bill.</param>
/// <param name="ParticipantId">
/// Staff only: who this one line is for, overriding <see cref="PlaceOrderCommand.OnBehalfOfParticipantId"/>
/// for it. Checked exactly as that is - an approved participant on this tab - and refused on a
/// shared line, which has no owner. Null leaves the line to the order's answer.
/// </param>
public sealed record OrderItemInput(
    Guid MenuItemId,
    int Quantity,
    bool IsShared = false,
    string? Note = null,
    Guid? ParticipantId = null);

/// <summary>
/// An order being placed, from either surface.
/// </summary>
/// <param name="TabId">The tab it goes on.</param>
/// <param name="Items">What was ordered. Empty is refused - an order of nothing is a client bug.</param>
/// <param name="ClientCommandId">
/// The caller's own id. A double-tap on "add to order" over a flaky connection returns the first
/// order rather than sending the kitchen two - the single most likely duplicate in the product.
/// </param>
/// <param name="OnBehalfOfParticipantId">
/// Staff only: who the spoken order was for. Null means the waiter did not know, and the lines are
/// attributed to the table and split across everyone present. A line's own
/// <see cref="OrderItemInput.ParticipantId"/> overrides it for that line.
/// </param>
public sealed record PlaceOrderCommand(
    Guid TabId,
    IReadOnlyList<OrderItemInput> Items,
    Guid ClientCommandId,
    Guid? OnBehalfOfParticipantId = null);

/// <summary>A manager taking money off a bill.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="TabOrderLineId">The line, or null for the whole tab.</param>
/// <param name="Kind">1 Discount (commercial), 2 Comp (an apology).</param>
/// <param name="Percent">Percentage off. Exactly one of this and <paramref name="AmountAmd"/>.</param>
/// <param name="AmountAmd">Flat dram off. Exactly one of this and <paramref name="Percent"/>.</param>
/// <param name="Reason">Why. Shown to the diner, so it is written for them.</param>
/// <param name="ClientCommandId">Idempotency, as everywhere else.</param>
public sealed record AddAdjustmentCommand(
    Guid TabId,
    Guid? TabOrderLineId,
    AdjustmentKind Kind,
    decimal? Percent,
    long? AmountAmd,
    string Reason,
    Guid ClientCommandId);

/// <summary>A waiter taking a line off the bill.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="LineId">The line. It stays visible to the diner, labelled as removed by staff.</param>
/// <param name="Reason">Why. Shown to the diner.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record VoidLineCommand(Guid TabId, Guid LineId, string Reason, Guid ClientCommandId);

/// <summary>Cash handed over at the table.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="AmountAmd">What was taken, in whole dram. Must not exceed what is owed.</param>
/// <param name="TabParticipantId">Who handed it over, when the waiter knows.</param>
/// <param name="TipAmd">A tip, kept entirely outside the balance.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record RecordCashPaymentCommand(
    Guid TabId,
    long AmountAmd,
    Guid? TabParticipantId,
    long TipAmd,
    Guid ClientCommandId);

// ---------------------------------------------------------------- views

/// <summary>One line as it was placed, for the order confirmation and the kitchen queue.</summary>
/// <param name="LineId">The line.</param>
/// <param name="MenuItemId">The item it came from.</param>
/// <param name="Name">The name snapshotted at order time.</param>
/// <param name="UnitPriceAmd">The price snapshotted at order time.</param>
/// <param name="Quantity">How many.</param>
/// <param name="LineTotalAmd">Unit price times quantity. Zero once voided.</param>
/// <param name="IsShared">Split across the table.</param>
/// <param name="IsTableAttributed">Nobody owns it: the waiter could not say who ordered it.</param>
/// <param name="Note">The kitchen note.</param>
/// <param name="SharedWithParticipantIds">Who it splits across, snapshotted at order time.</param>
/// <param name="OnBehalfOfParticipantId">
/// Who a waiter keyed this line in for: the line's own <c>participantId</c> when one was sent,
/// otherwise the order's <c>onBehalfOfParticipantId</c>. Null on a diner's own order (the line is
/// the orderer's), when the waiter named nobody, and on a shared line, which is the table's.
/// </param>
public sealed record OrderLineView(
    Guid LineId,
    Guid MenuItemId,
    string Name,
    long UnitPriceAmd,
    int Quantity,
    long LineTotalAmd,
    bool IsShared,
    bool IsTableAttributed,
    string? Note,
    IReadOnlyList<Guid> SharedWithParticipantIds,
    Guid? OnBehalfOfParticipantId);

/// <summary>An order as it was accepted.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="TabId">The tab it went on.</param>
/// <param name="Status">1 New, 2 InKitchen, 3 Ready, 4 Served, 5 Voided.</param>
/// <param name="PlacedByParticipantId">The diner who ordered from their phone.</param>
/// <param name="PlacedByStaffId">The waiter who keyed it in.</param>
/// <param name="OnBehalfOfParticipantId">Who a spoken order was for, when the waiter knew.</param>
/// <param name="PlacedAtUtc">When.</param>
/// <param name="EstimatedReadyAtUtc">
/// From the longest prep time on the order, not the sum: a kitchen cooks an order together.
/// </param>
/// <param name="Lines">What was ordered.</param>
/// <param name="TableTotalVisible">
/// Whether <paramref name="Totals"/> is present: for staff, and for a diner allowed the table total -
/// the rule <c>tableTotalVisible</c> on their tab follows.
/// </param>
/// <param name="Totals">
/// The tab's totals as they stand after this order. <b>Absent</b> for a diner the host hid the
/// total from - an order of their own is no way round the hidden bill. Never zeroed.
/// </param>
/// <param name="TabEventSequence">The tab's event sequence after this order, for a live client.</param>
/// <param name="WasReplay">True when the command had already been applied and this is the original answer.</param>
public sealed record OrderView(
    Guid OrderId,
    Guid TabId,
    TabOrderStatus Status,
    Guid? PlacedByParticipantId,
    Guid? PlacedByStaffId,
    Guid? OnBehalfOfParticipantId,
    DateTime PlacedAtUtc,
    DateTime? EstimatedReadyAtUtc,
    IReadOnlyList<OrderLineView> Lines,
    bool TableTotalVisible,
    TabTotalsSnapshot? Totals,
    long TabEventSequence,
    bool WasReplay);

/// <summary>The tab's money, in whole dram. Server-computed; a client only ever displays it.</summary>
/// <param name="SubtotalAmd">Lines less adjustments.</param>
/// <param name="ServiceChargeAmd">Charged on the post-discount subtotal.</param>
/// <param name="TotalAmd">Subtotal plus service charge.</param>
/// <param name="PaidAmd">Settled so far. Tips excluded.</param>
/// <param name="RemainingAmd">Still owed.</param>
public sealed record TabTotalsSnapshot(
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long RemainingAmd);

/// <summary>An adjustment as the diner sees it, reason included.</summary>
/// <param name="AdjustmentId">The adjustment.</param>
/// <param name="TabOrderLineId">The line it applies to, or null for the whole tab.</param>
/// <param name="Kind">1 Discount, 2 Comp.</param>
/// <param name="Percent">Percentage off, when that is how it was expressed.</param>
/// <param name="AmountAmd">Flat dram off, when that is how it was expressed.</param>
/// <param name="ReductionAmd">
/// What it actually took off, in whole dram: its own reduction worked out in the bill's order (a line's own adjustments
/// before the tab-wide ones) - the amount the History and the reports give it. Not the subtotal's drop, which under a
/// standing tab-wide discount is less than a line's comp took. Zero once reversed.
/// </param>
/// <param name="Reason">Why. Shown to the diner for the same reason a void is.</param>
/// <param name="CreatedAtUtc">When.</param>
/// <param name="IsVoided">True once reversed. It stays on the record either way.</param>
public sealed record AdjustmentView(
    Guid AdjustmentId,
    Guid? TabOrderLineId,
    AdjustmentKind Kind,
    decimal? Percent,
    long? AmountAmd,
    long ReductionAmd,
    string Reason,
    DateTime CreatedAtUtc,
    bool IsVoided);

/// <summary>What one person owes. See <c>docs/billing.md</c> for the arithmetic.</summary>
/// <param name="ParticipantId">The participant.</param>
/// <param name="DisplayName">Their name on the tab.</param>
/// <param name="Status">1 PendingApproval, 2 Approved, 3 Removed.</param>
/// <param name="OwnItemsAmd">Their own unshared lines.</param>
/// <param name="SharedItemsAmd">Their slice of shared and table-attributed lines.</param>
/// <param name="AbsorbedFromRemovedAmd">
/// What fell to them because somebody removed from the tab cannot pay for what they ate. Non-zero
/// only for the host, and stated rather than folded in silently.
/// </param>
/// <param name="PersonalAmd">Their part of the subtotal.</param>
/// <param name="ServiceChargeAmd">Their pro-rata slice of the service charge.</param>
/// <param name="ShareAmd">What they owe. These sum to the tab total exactly.</param>
/// <param name="PaidAmd">What they have settled. Reported, never netted off the share.</param>
public sealed record ParticipantShareView(
    Guid ParticipantId,
    string DisplayName,
    ParticipantStatus Status,
    long OwnItemsAmd,
    long SharedItemsAmd,
    long AbsorbedFromRemovedAmd,
    long PersonalAmd,
    long ServiceChargeAmd,
    long ShareAmd,
    long PaidAmd);

/// <summary>
/// Who owes what on a tab, projected through the caller's own visibility.
/// </summary>
/// <remarks>
/// A participant without <c>CanSeeTableTotal</c> gets their own share and <b>no table aggregate</b> -
/// the members are absent, not zeroed. A zero reads as "nothing owed" and a null as "free"; an
/// absent member beside an explicit flag can only be read as "not shown to you".
/// </remarks>
/// <param name="TabId">The tab.</param>
/// <param name="MyShare">The caller's own share. Always present.</param>
/// <param name="TableTotalVisible">Whether the two members below are present.</param>
/// <param name="Totals">The table aggregate. Absent when not visible.</param>
/// <param name="Shares">Everyone's share. Absent when not visible.</param>
/// <param name="AbsorbedFromRemovedAmd">How much fell to the host from removed participants.</param>
/// <param name="MaxSequence">
/// Where the tab's event stream stands, so a client that just read the split knows whether it has
/// missed anything. Prompt 8 said every tab response carries it; this one did not, which made the
/// bill screen - the screen where being out of date matters most - the one that had to ask twice.
/// </param>
public sealed record TabSharesView(
    Guid TabId,
    ParticipantShareView? MyShare,
    bool TableTotalVisible,
    TabTotalsSnapshot? Totals,
    IReadOnlyList<ParticipantShareView>? Shares,
    long AbsorbedFromRemovedAmd,
    long MaxSequence);

/// <summary>An order on the kitchen queue.</summary>
/// <param name="OrderId">The order. For a booking's pre-order, the pre-order's id - the same before and after the party sits.</param>
/// <param name="TabId">
/// Its tab. <b>Absent</b> on a pre-order sent before the party sat: a held ticket, cooked ahead, on no bill until they
/// arrive - it then reappears under the same <paramref name="OrderId"/> with the tab.
/// </param>
/// <param name="TableLabel">Which table to take it to.</param>
/// <param name="Status">Where it is on the rail.</param>
/// <param name="PlacedAtUtc">When it was sent.</param>
/// <param name="EstimatedReadyAtUtc">When it is due.</param>
/// <param name="WaitingMinutes">How long it has been waiting, at the moment of reading.</param>
/// <param name="Lines">What to cook, with the kitchen notes.</param>
/// <param name="PlacedByName">
/// Who sent it: the waiter's name as the staff sign-in roster shows it, when a waiter keyed it in.
/// <b>Absent</b> when a diner ordered from their own phone - the ticket then says it came from the
/// app. Read as the staff member is now: a rename shows, and a waiter no longer active still sent it.
/// </param>
/// <param name="PreOrder">
/// Set when this is a booking's pre-order - held or on the tab it became: whose, and when they are due, so the kitchen
/// times the cooking. Absent on every other order.
/// </param>
/// <param name="Takeaway">
/// Set when this is a takeaway order the venue accepted - the "TAKEAWAY" band: its code, when it is promised, whose it
/// is and the guest's note. <paramref name="TableLabel"/> is the code, <paramref name="TabId"/> is absent, and the
/// kitchen's "Ready" is <c>POST /api/takeaway-orders/{orderId}/ready</c>. Absent on every other order.
/// </param>
public sealed record KitchenOrderView(
    Guid OrderId,
    Guid? TabId,
    string TableLabel,
    TabOrderStatus Status,
    DateTime PlacedAtUtc,
    DateTime? EstimatedReadyAtUtc,
    int WaitingMinutes,
    IReadOnlyList<OrderLineView> Lines,
    string? PlacedByName,
    KitchenPreOrder? PreOrder = null,
    KitchenTakeaway? Takeaway = null);

/// <summary>The takeaway order a kitchen ticket is.</summary>
/// <param name="Code">"T-482" - what the guest says at the counter.</param>
/// <param name="ReadyAtUtc">When the venue promised it.</param>
/// <param name="ReadyAtLocalTime">The same, on the branch's wall clock - "ready 19:10".</param>
/// <param name="GuestName">Whose it is - a name, never a number.</param>
/// <param name="Note">The guest's note: "no onion, separate bag". Absent when none.</param>
public sealed record KitchenTakeaway(
    string Code,
    DateTime ReadyAtUtc,
    TimeOnly ReadyAtLocalTime,
    string GuestName,
    string? Note);

/// <summary>The booking a kitchen ticket was pre-ordered for.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="StartUtc">When the party is due.</param>
/// <param name="LocalStartTime">The same, on the branch's wall clock - "pre-order for 19:30".</param>
/// <param name="PartySize">How many.</param>
/// <param name="GuestName">Who booked - a name, never a number.</param>
public sealed record KitchenPreOrder(
    Guid ReservationId,
    DateTime StartUtc,
    TimeOnly LocalStartTime,
    int PartySize,
    string GuestName);

/// <summary>A table asking for something.</summary>
/// <param name="ServiceRequestId">The request.</param>
/// <param name="TabId">Its tab.</param>
/// <param name="TableLabel">Which table to walk to.</param>
/// <param name="Preset">1 Napkins, 2 Water, 3 TheBill, 4 Other.</param>
/// <param name="Note">The one optional line.</param>
/// <param name="RequestedByParticipantId">Who asked.</param>
/// <param name="CreatedAtUtc">When.</param>
/// <param name="WaitingMinutes">How long they have been waiting.</param>
/// <param name="AcknowledgedAtUtc">When a waiter picked it up, if they have.</param>
/// <param name="TabEventSequence">
/// Where the tab's event stream stands. Raising or acknowledging a request appends an event, so the
/// response says where that left the stream - otherwise the phone that just called a waiter is the
/// one client guaranteed to be behind. Zero on the branch-wide list, which spans many tabs and so
/// has no single stream to be caught up with.
/// </param>
/// <param name="BillScope">
/// On a request for the bill that said whose part: 1 WholeBill, 2 MyShare. Absent on every other
/// request.
/// </param>
/// <param name="BillAmountAmd">
/// What that scope left to pay when the diner asked, in whole dram - service charge included, tip
/// excluded. Priced by the server, so the waiter brings the number the diner was shown. Absent with
/// <paramref name="BillScope"/>.
/// </param>
/// <param name="TipPercent">The tip the diner chose, 0 to 25. Absent when none was asked.</param>
/// <param name="TipAmd">
/// That tip in whole dram, on top of <paramref name="BillAmountAmd"/> and outside the bill, like a
/// cash payment's <c>tipAmd</c>. Absent with <paramref name="TipPercent"/>.
/// </param>
/// <param name="AcknowledgedByStaffMemberId">Who acknowledged it. Absent while nobody has.</param>
/// <param name="AcknowledgedByName">That waiter's name as stored now. Absent with the id.</param>
/// <param name="ClaimedAtUtc">When a waiter said "I'm on it" (<c>claimServiceRequest</c>); absent while nobody has.</param>
/// <param name="ClaimedByStaffMemberId">Who - the first to claim it.</param>
/// <param name="ClaimedByName">Their name as stored now, for "Aram is on it".</param>
public sealed record ServiceRequestView(
    Guid ServiceRequestId,
    Guid TabId,
    string TableLabel,
    ServiceRequestPreset Preset,
    string? Note,
    Guid? RequestedByParticipantId,
    DateTime CreatedAtUtc,
    int WaitingMinutes,
    DateTime? AcknowledgedAtUtc,
    long TabEventSequence,
    BillScope? BillScope,
    long? BillAmountAmd,
    int? TipPercent,
    long? TipAmd,
    Guid? AcknowledgedByStaffMemberId = null,
    string? AcknowledgedByName = null,
    DateTime? ClaimedAtUtc = null,
    Guid? ClaimedByStaffMemberId = null,
    string? ClaimedByName = null);

/// <summary>A cash payment, as recorded.</summary>
/// <param name="PaymentId">The payment.</param>
/// <param name="TabId">The tab.</param>
/// <param name="AmountAmd">What was taken against the balance.</param>
/// <param name="TipAmd">The tip, which is not part of the balance.</param>
/// <param name="Status">1 Reserved, 2 Succeeded, 3 Failed, 4 Released.</param>
/// <param name="TabParticipantId">Who handed it over, when known.</param>
/// <param name="Totals">The tab's totals after it.</param>
/// <param name="TabClosed">True when this payment settled the bill and closed the tab.</param>
/// <param name="TableSessionClosed">
/// True when closing the tab also closed the sitting. The <b>table is not freed</b> by this -
/// that stays an explicit waiter action, because physical and financial state are independent.
/// </param>
/// <param name="TabEventSequence">The tab's event sequence after this payment.</param>
/// <param name="WasReplay">True when the command had already been applied.</param>
public sealed record CashPaymentView(
    Guid PaymentId,
    Guid TabId,
    long AmountAmd,
    long TipAmd,
    PaymentStatus Status,
    Guid? TabParticipantId,
    TabTotalsSnapshot Totals,
    bool TabClosed,
    bool TableSessionClosed,
    long TabEventSequence,
    bool WasReplay);

/// <summary>One thing that happened on a tab.</summary>
/// <param name="Sequence">This event's place on its tab, from 1. Ask for everything after the one you hold.</param>
/// <param name="Type">What happened. Ignore a type you do not recognise and keep your position.</param>
/// <param name="Payload">The details, shaped per type.</param>
/// <param name="ActorType">1 Diner, 2 Staff, 3 System.</param>
/// <param name="ActorId">Who, when there is a who.</param>
/// <param name="AtUtc">When.</param>
public sealed record TabEventView(
    long Sequence,
    TabEventType Type,
    System.Text.Json.JsonElement Payload,
    ActorType ActorType,
    Guid? ActorId,
    DateTime AtUtc);

/// <summary>A page of a tab's event stream.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="AfterSequence">What the caller already had.</param>
/// <param name="MaxSequence">The newest position on the tab, whether or not it is in this page.</param>
/// <param name="HasMore">True when more remain past this page.</param>
/// <param name="Events">The events, oldest first.</param>
public sealed record TabEventPage(
    Guid TabId,
    long AfterSequence,
    long MaxSequence,
    bool HasMore,
    IReadOnlyList<TabEventView> Events);
