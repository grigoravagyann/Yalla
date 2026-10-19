using System.ComponentModel.DataAnnotations;
using Yalla.Application.Ordering;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>One item on an order being placed.</summary>
/// <param name="MenuItemId">Which dish.</param>
/// <param name="Quantity">How many. At least one.</param>
/// <param name="IsShared">True for something the table shares.</param>
/// <param name="Note">What the guest asked for. Goes to the kitchen, not the bill.</param>
/// <param name="ParticipantId">
/// <b>Staff only.</b> Who this one line is for - a named guest's dish in a round that is otherwise
/// the table's, or another guest's. Overrides the order's <c>onBehalfOfParticipantId</c> for this
/// line alone, so one send stays one order and one kitchen ticket. Checked exactly as that field is:
/// not on this tab is 404 <c>not-found</c>, not approved yet is 409 <c>conflicting-state</c>, and from
/// a diner's phone it is 403 <c>forbidden</c>. <b>Never with <c>isShared</c></b>: a shared line belongs
/// to the table, so naming a guest on it is 400 <c>invalid-request</c> with
/// <c>context.field</c> <c>items[i].participantId</c>. Leave it out to take the order's answer.
/// </param>
public sealed record OrderItemRequest(
    Guid MenuItemId,
    int Quantity,
    bool IsShared = false,
    string? Note = null,
    Guid? ParticipantId = null)
{
    public OrderItemInput ToInput() => new(MenuItemId, Quantity, IsShared, Note, ParticipantId);
}

/// <summary>An order, from a diner's phone or a waiter's tablet.</summary>
/// <param name="Items">What was ordered. At least one line.</param>
/// <param name="ClientCommandId">
/// The caller's own id. A double-tap over flaky wifi returns the first order rather than sending
/// the kitchen two. <b>Required</b>: left out, it bound <c>Guid.Empty</c> and replayed whichever order
/// first used that, on any tab; now it is refused with 400. A replay is matched on this tab and this
/// caller only.
/// </param>
/// <param name="OnBehalfOfParticipantId">
/// <b>Staff only.</b> Who the spoken order was for. Leave it out when the waiter did not know, and
/// the lines are attributed to the table and split across everyone present. A line's own
/// <c>participantId</c> overrides it for that line; a shared line splits whatever it says.
/// </param>
public sealed record PlaceOrderRequest(
    IReadOnlyList<OrderItemRequest> Items,
    Guid ClientCommandId,
    Guid? OnBehalfOfParticipantId = null) : IClientCommandRequest
{
    public PlaceOrderCommand ToCommand(Guid tabId) =>
        new(tabId, [.. Items.Select(i => i.ToInput())], ClientCommandId, OnBehalfOfParticipantId);
}

/// <summary>Taking a line off the bill. It stays visible to the diner, labelled.</summary>
/// <param name="Reason">Why. Shown to the diner.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record VoidLineRequest(string Reason, Guid ClientCommandId) : IClientCommandRequest;

/// <summary>A discount or a comp.</summary>
/// <param name="TabOrderLineId">The line, or null for the whole tab.</param>
/// <param name="Kind">1 Discount (commercial), 2 Comp (an apology).</param>
/// <param name="Percent">Percentage off. Exactly one of this and <paramref name="AmountAmd"/>.</param>
/// <param name="AmountAmd">Flat dram off. Exactly one of this and <paramref name="Percent"/>.</param>
/// <param name="Reason">Why. Shown to the diner.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record AddAdjustmentRequest(
    Guid? TabOrderLineId,
    AdjustmentKind Kind,
    decimal? Percent,
    long? AmountAmd,
    string Reason,
    Guid ClientCommandId) : IClientCommandRequest;

/// <summary>Moving an order along the kitchen rail.</summary>
/// <param name="Status">2 InKitchen, 3 Ready, 4 Served.</param>
public sealed record MoveOrderStatusRequest(TabOrderStatus Status);

/// <summary>A table asking for something.</summary>
/// <param name="Preset">1 Napkins, 2 Water, 3 TheBill, 4 Other.</param>
/// <param name="Note">One short line, optional. Not a conversation.</param>
/// <param name="BillScope">
/// Only with preset 3: whose part of the bill - 1 WholeBill, 2 MyShare. The server prices it and the
/// response carries <c>billAmountAmd</c>. Leave it out for a plain "the bill, please". WholeBill needs
/// sight of the table total. Replaces the asker's own earlier open request for the bill on the tab.
/// </param>
/// <param name="TipPercent">
/// Only with a <paramref name="BillScope"/>: the tip, 0 to 25 percent of the items that scope still
/// leaves unpaid (before service). The response carries it priced as <c>tipAmd</c>.
/// </param>
public sealed record RaiseServiceRequest(
    ServiceRequestPreset Preset,
    string? Note = null,
    BillScope? BillScope = null,
    [Range(0, BillQuote.MaxTipPercent)] int? TipPercent = null);

/// <summary>Cash handed over at the table.</summary>
/// <param name="AmountAmd">What was taken against the balance, in whole dram.</param>
/// <param name="TabParticipantId">Who handed it over, when the waiter knows.</param>
/// <param name="TipAmd">A tip. Kept entirely outside the balance.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record RecordCashRequest(
    long AmountAmd,
    Guid? TabParticipantId,
    long TipAmd,
    Guid ClientCommandId) : IClientCommandRequest
{
    public RecordCashPaymentCommand ToCommand(Guid tabId) =>
        new(tabId, AmountAmd, TabParticipantId, TipAmd, ClientCommandId);
}

/// <summary>Writing off an outstanding balance.</summary>
/// <param name="Reason">Why the venue is accepting the loss.</param>
public sealed record AbandonTabRequest(string Reason);
