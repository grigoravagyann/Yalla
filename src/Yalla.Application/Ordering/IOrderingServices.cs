using Yalla.Domain.Enums;

namespace Yalla.Application.Ordering;

/// <summary>
/// Placing orders, taking things off the bill, and moving food along the kitchen rail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two entry points, one code path.</b> A diner ordering from their phone and a waiter keying in
/// what was spoken at the table both land in <see cref="PlaceOrderAsync"/>. That is not tidiness: the
/// live bill is only correct if <i>every</i> order goes through the system, which means the staff
/// tablet is an order-entry point, which means this is the venue's till. A second path for spoken
/// orders would be a second source of truth, and the totals would be wrong in exactly the venues
/// that need them most.
/// </para>
/// <para>
/// Who is allowed to do what is decided inside the service, from <c>ICurrentActor</c> and the tab's
/// own participant rows, so it holds for every caller rather than only for the HTTP pipeline.
/// </para>
/// </remarks>
public interface ITabOrderService
{
    /// <summary>
    /// Places an order against a tab, snapshotting every name and price as it stands now.
    /// </summary>
    /// <exception cref="Domain.Tabs.MenuItemUnavailableException">
    /// Something on the order has sold out. Nothing is placed - see the exception's remarks.
    /// </exception>
    /// <exception cref="Domain.Tabs.TabNotAcceptingOrdersException">The bill has been asked for.</exception>
    /// <exception cref="Domain.Tabs.TabPermissionException">The caller may not order on this tab.</exception>
    Task<OrderView> PlaceOrderAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a line off the bill. <b>Waiter or above.</b>
    /// </summary>
    /// <remarks>
    /// The line stays on the tab and stays visible to the diner, labelled as removed by staff.
    /// Nothing silently disappears from a bill somebody is watching on their phone.
    /// </remarks>
    /// <exception cref="Domain.Tabs.LineAlreadyPaidException">That would be a refund, not a void.</exception>
    Task<OrderView> VoidLineAsync(VoidLineCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discounts or comps part of a bill. <b>Manager or above</b>, because it is a decision about
    /// money rather than about the floor.
    /// </summary>
    Task<AdjustmentView> AddAdjustmentAsync(
        AddAdjustmentCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Reverses an adjustment. It stays on the record, marked. <b>Manager or above.</b></summary>
    Task<AdjustmentView> VoidAdjustmentAsync(
        Guid adjustmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an order along the kitchen rail. <b>Waiter or above</b>, with Kitchen confined to
    /// <c>InKitchen -> Ready</c>: cooking is what that role does, and serving is not.
    /// </summary>
    /// <exception cref="DomainStateException">The transition is not one the rail allows.</exception>
    /// <exception cref="Domain.Staff.StaffPermissionException">This role may not make that move.</exception>
    Task<KitchenOrderView> MoveOrderStatusAsync(
        Guid orderId,
        TabOrderStatus next,
        CancellationToken cancellationToken = default);

    /// <summary>The branch's open orders, oldest first. <b>Waiter or above.</b></summary>
    Task<IReadOnlyList<KitchenOrderView>> GetBranchOrdersAsync(
        Guid branchId,
        TabOrderStatus? status,
        CancellationToken cancellationToken = default);
}

/// <summary>Who owes what, and the tab's event stream.</summary>
public interface ITabBillingQuery
{
    /// <summary>
    /// Every participant's share, projected through the caller's own visibility.
    /// </summary>
    /// <remarks>
    /// Needed now rather than at payment time: a waiter taking cash for one person's half needs the
    /// number while standing at the table. Pass <paramref name="actingParticipantId"/> for a diner,
    /// or null for staff, who are not subject to the host's visibility flags.
    /// </remarks>
    Task<TabSharesView> GetSharesAsync(
        Guid tabId,
        Guid? actingParticipantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything that happened on a tab after <paramref name="afterSequence"/>, oldest first.
    /// </summary>
    /// <remarks>
    /// The catch-up a phone does when it comes out of a lift. Without it the only options are
    /// re-fetching the whole tab on every reconnect - which is what makes a live bill flicker and
    /// disagree with itself - or missing the change entirely.
    /// </remarks>
    Task<TabEventPage> GetEventsAsync(
        Guid tabId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Calling a waiter, and a waiter answering.</summary>
public interface IServiceRequestService
{
    /// <summary>A table asks for something. Presets only, rate limited per tab.</summary>
    /// <exception cref="Domain.Tabs.ServiceRequestRateLimitedException">Too many, too fast.</exception>
    Task<ServiceRequestView> RaiseAsync(
        Guid tabId,
        Guid actingParticipantId,
        ServiceRequestPreset preset,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>What is outstanding in this branch, newest first. <b>Waiter or above.</b></summary>
    Task<IReadOnlyList<ServiceRequestView>> GetOpenAsync(
        Guid branchId,
        CancellationToken cancellationToken = default);

    /// <summary>A waiter has seen it. <b>Waiter or above.</b></summary>
    Task<ServiceRequestView> AcknowledgeAsync(
        Guid serviceRequestId,
        CancellationToken cancellationToken = default);
}

/// <summary>Settling a tab. Cash is the only rail in this task.</summary>
/// <remarks>
/// <b>A payment recorded here is not a fiscal receipt.</b> The venue's registered cash register
/// still issues the receipt, and nothing in this module may be described to a venue as fiscal
/// compliance. See <c>docs/billing.md</c>.
/// </remarks>
public interface ITabPaymentService
{
    /// <summary>
    /// Records cash taken at the table. <b>Waiter or above, branch-scoped.</b>
    /// </summary>
    /// <remarks>
    /// The amount is <b>reserved against the remaining balance under the tab's row version</b>
    /// before anything is recorded, and only then marked succeeded. For cash both steps happen in
    /// one call; the two-step shape exists so a wallet provider can sit in <c>Reserved</c> while it
    /// is being called, and it is far easier to get right with one rail than three.
    /// </remarks>
    /// <exception cref="Domain.Tabs.PaymentExceedsRemainingException">
    /// More was offered than is owed. Carries the current balance, because the waiter is standing
    /// at the table and needs the number.
    /// </exception>
    Task<CashPaymentView> RecordCashAsync(
        RecordCashPaymentCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes off an outstanding balance. <b>Manager or above.</b>
    /// </summary>
    Task<TabTotalsSnapshot> AbandonAsync(
        Guid tabId,
        string reason,
        CancellationToken cancellationToken = default);
}
