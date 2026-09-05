using Yalla.Application.Ordering;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>One item on an order being placed.</summary>
/// <param name="MenuItemId">Which dish.</param>
/// <param name="Quantity">How many. At least one.</param>
/// <param name="IsShared">True for something the table shares.</param>
/// <param name="Note">What the guest asked for. Goes to the kitchen, not the bill.</param>
public sealed record OrderItemRequest(Guid MenuItemId, int Quantity, bool IsShared = false, string? Note = null)
{
    public OrderItemInput ToInput() => new(MenuItemId, Quantity, IsShared, Note);
}

/// <summary>An order, from a diner's phone or a waiter's tablet.</summary>
/// <param name="Items">What was ordered. At least one line.</param>
/// <param name="ClientCommandId">
/// The caller's own id. A double-tap over flaky wifi returns the first order rather than sending
/// the kitchen two.
/// </param>
/// <param name="OnBehalfOfParticipantId">
/// <b>Staff only.</b> Who the spoken order was for. Leave it out when the waiter did not know, and
/// the lines are attributed to the table and split across everyone present.
/// </param>
public sealed record PlaceOrderRequest(
    IReadOnlyList<OrderItemRequest> Items,
    Guid ClientCommandId,
    Guid? OnBehalfOfParticipantId = null)
{
    public PlaceOrderCommand ToCommand(Guid tabId) =>
        new(tabId, [.. Items.Select(i => i.ToInput())], ClientCommandId, OnBehalfOfParticipantId);
}

/// <summary>Taking a line off the bill. It stays visible to the diner, labelled.</summary>
/// <param name="Reason">Why. Shown to the diner.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record VoidLineRequest(string Reason, Guid ClientCommandId);

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
    Guid ClientCommandId);

/// <summary>Moving an order along the kitchen rail.</summary>
/// <param name="Status">2 InKitchen, 3 Ready, 4 Served.</param>
public sealed record MoveOrderStatusRequest(TabOrderStatus Status);

/// <summary>A table asking for something.</summary>
/// <param name="Preset">1 Napkins, 2 Water, 3 TheBill, 4 Other.</param>
/// <param name="Note">One short line, optional. Not a conversation.</param>
public sealed record RaiseServiceRequest(ServiceRequestPreset Preset, string? Note = null);

/// <summary>Cash handed over at the table.</summary>
/// <param name="AmountAmd">What was taken against the balance, in whole dram.</param>
/// <param name="TabParticipantId">Who handed it over, when the waiter knows.</param>
/// <param name="TipAmd">A tip. Kept entirely outside the balance.</param>
/// <param name="ClientCommandId">Idempotency.</param>
public sealed record RecordCashRequest(
    long AmountAmd,
    Guid? TabParticipantId,
    long TipAmd,
    Guid ClientCommandId)
{
    public RecordCashPaymentCommand ToCommand(Guid tabId) =>
        new(tabId, AmountAmd, TabParticipantId, TipAmd, ClientCommandId);
}

/// <summary>Writing off an outstanding balance.</summary>
/// <param name="Reason">Why the venue is accepting the loss.</param>
public sealed record AbandonTabRequest(string Reason);
