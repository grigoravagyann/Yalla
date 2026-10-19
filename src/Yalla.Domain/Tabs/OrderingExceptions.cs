using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

/// <summary>
/// Something on the order is not available tonight.
/// </summary>
/// <remarks>
/// <para>
/// Carries the item's <b>name</b>, not only its id, because the client has to be able to say
/// <i>which</i> dish. "Your order could not be placed" sends the diner back to a waiter to find out
/// what went wrong, which is the question this whole feature exists to remove.
/// </para>
/// <para>
/// The order is refused entirely rather than placed without the missing dish. A partial order is a
/// silent decision made on the diner's behalf: they may not want the main course without the
/// starter, and they certainly do not want to discover the substitution when the food arrives.
/// </para>
/// </remarks>
public sealed class MenuItemUnavailableException(Guid menuItemId, string itemName)
    : DomainStateException($"{itemName} is not available right now, so the order was not placed.")
{
    public Guid MenuItemId { get; } = menuItemId;

    public string ItemName { get; } = itemName;
}

/// <summary>
/// The bill has been asked for, so nothing more goes on it.
/// </summary>
/// <remarks>
/// Its own error rather than a generic refusal, because the client's response differs: this is not
/// "you may not order" but "this tab is finishing", and the app should show the bill rather than
/// the menu. Somebody who has already paid their share must not find a stranger's dessert added
/// after they have left, which is what the <c>Closing</c> flag was built for.
/// </remarks>
public sealed class TabNotAcceptingOrdersException(Guid tabId, TabStatus status)
    : DomainStateException(
        status == TabStatus.Closing
            ? "The bill has been asked for, so nothing more can be added to this tab."
            : $"This tab is {status} and takes no more orders.")
{
    public Guid TabId { get; } = tabId;

    public TabStatus Status { get; } = status;
}

/// <summary>
/// A waiter named a guest for an order, or for one line of it, who is not on this tab at all.
/// </summary>
/// <remarks>
/// A <see cref="KeyNotFoundException"/>, so it answers 404 <c>not-found</c> as it always did. Its own
/// type so the answer can say <i>which</i> field named <i>whom</i>: a tablet sending a round it
/// queued offline has to tell "that guest is gone" from a dish deleted from the menu it cached, which
/// answers the same status and code.
/// </remarks>
/// <param name="tabId">The tab the order was for.</param>
/// <param name="participantId">The guest named.</param>
/// <param name="field">Where: <c>onBehalfOfParticipantId</c>, or <c>items[i].participantId</c>.</param>
public sealed class NamedGuestNotOnTabException(Guid tabId, Guid participantId, string field)
    : KeyNotFoundException($"Participant {participantId} is not on tab {tabId}.")
{
    public Guid ParticipantId { get; } = participantId;

    public string Field { get; } = field;
}

/// <summary>
/// A waiter named a guest for an order, or for one line of it, who is on this tab but may not have
/// an order put in their name: still waiting for the host, or taken off by them.
/// </summary>
/// <remarks>
/// A <see cref="DomainStateException"/>, so it answers 409 <c>conflicting-state</c> as it always did,
/// with the field, the guest and their status beside it for the same reason as
/// <see cref="NamedGuestNotOnTabException"/>.
/// </remarks>
/// <param name="participantId">The guest named.</param>
/// <param name="participantStatus">Where they are: pending approval, or removed.</param>
/// <param name="field">Where: <c>onBehalfOfParticipantId</c>, or <c>items[i].participantId</c>.</param>
public sealed class NamedGuestNotOrderableException(Guid participantId, ParticipantStatus participantStatus, string field)
    : DomainStateException(
        participantStatus == ParticipantStatus.Removed
            ? "That guest was taken off the tab, so an order cannot be put in their name."
            : "That guest is not on the tab yet, so an order cannot be put in their name.")
{
    public Guid ParticipantId { get; } = participantId;

    public ParticipantStatus ParticipantStatus { get; } = participantStatus;

    public string Field { get; } = field;
}

/// <summary>
/// The line has been paid for, so removing it is a refund.
/// </summary>
/// <remarks>
/// Refunds are a separate thing with their own rail, their own audit and their own conversation
/// with a manager. Letting a void quietly reverse money that has already changed hands would put
/// the tab's arithmetic and the cash drawer out of step with nothing recording why.
/// </remarks>
public sealed class LineAlreadyPaidException(Guid tabId, Guid lineId)
    : DomainStateException(
        "This tab has already been paid for, so removing a line from it is a refund rather than a void.")
{
    public Guid TabId { get; } = tabId;

    public Guid LineId { get; } = lineId;
}

/// <summary>
/// The payment is for more than the tab still owes.
/// </summary>
/// <remarks>
/// Carries the current remaining balance, because the waiter is standing at the table holding cash
/// and needs the number rather than a refusal. The usual cause is two people settling at once -
/// somebody paid in the app while the waiter was typing - which is exactly what the reserve exists
/// to catch.
/// </remarks>
public sealed class PaymentExceedsRemainingException(Guid tabId, long requestedAmd, long remainingAmd)
    : DomainStateException(
        remainingAmd == 0L
            ? "This tab is fully settled; there is nothing left to pay."
            : $"Only {remainingAmd} AMD is still owed on this tab, and {requestedAmd} AMD was offered.")
{
    public Guid TabId { get; } = tabId;

    public long RequestedAmd { get; } = requestedAmd;

    public long RemainingAmd { get; } = remainingAmd;
}

/// <summary>
/// This table has asked for something too many times in too short a span.
/// </summary>
/// <remarks>
/// A bored table tapping "napkins" twenty times turns the floor screen into noise and buries the
/// request that mattered. The limit is per tab rather than per person: the counter a waiter is
/// looking at is the table's, and one guest cannot be allowed to drown out another's.
/// </remarks>
public sealed class ServiceRequestRateLimitedException(Guid tabId, int limit, int windowMinutes)
    : DomainStateException(
        $"This table has already asked for {limit} things in the last {windowMinutes} minutes. "
        + "A waiter is on the way; please give them a moment.")
{
    public Guid TabId { get; } = tabId;

    public int Limit { get; } = limit;

    public int WindowMinutes { get; } = windowMinutes;
}

/// <summary>
/// Staff asked to open a tab on a table nobody is sitting at.
/// </summary>
/// <remarks>
/// A tab hangs off a sitting, and the sitting is what seating a party creates. Opening one on a
/// free table would have to guess the party size and seat it in the same tap, which is the walk-in
/// button's job - so the answer is to seat the party first, and the client says exactly that.
/// </remarks>
public sealed class TableNotSeatedException(Guid tableId, string tableLabel, TableStatus status)
    : DomainStateException($"Nobody is seated at table {tableLabel}. Seat the party first, then open the tab.")
{
    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public TableStatus Status { get; } = status;
}
