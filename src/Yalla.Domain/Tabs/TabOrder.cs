using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One round of ordering against a tab: what was sent to the kitchen together, and by whom.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="PlacedByParticipantId"/> and <see cref="PlacedByStaffId"/> is set.
/// Diners order from their own phone; waiters enter spoken orders on the tablet. Both land here,
/// so the kitchen queue and the bill never care which happened.
/// </remarks>
public sealed class TabOrder : Entity
{
    private readonly List<TabOrderLine> _lines = [];

    /// <summary>Set by <see cref="Place"/>, so an order goes on the tab once. Not persisted: only a new order is placed.</summary>
    private bool _placed;

    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>The diner who ordered, when the order came from a phone.</summary>
    public Guid? PlacedByParticipantId { get; private set; }

    public TabParticipant? PlacedByParticipant { get; private set; }

    /// <summary>The waiter who keyed it in, when the order was spoken at the table.</summary>
    public Guid? PlacedByStaffId { get; private set; }

    public StaffMember? PlacedByStaff { get; private set; }

    /// <summary>
    /// Who the waiter said the spoken order was for. Null when they did not know.
    /// </summary>
    /// <remarks>
    /// This is what keeps "everyone pays their own" working once a waiter is an order-entry point.
    /// Without it every spoken order lands on the table rather than on a person, and a table that
    /// agreed to split by what they ate cannot. When it is null the lines are table-attributed and
    /// split across everyone present - a real answer rather than a wrong one, and one whose cost the
    /// venue can see.
    /// </remarks>
    public Guid? OnBehalfOfParticipantId { get; private set; }

    public TabParticipant? OnBehalfOfParticipant { get; private set; }

    /// <summary>
    /// When the kitchen expects to be done, from the longest prep time on the order.
    /// </summary>
    /// <remarks>
    /// The longest, not the sum: a kitchen cooks an order together and sends it out together. Stated
    /// at order time on purpose - "about 20 minutes" up front is worth more to a waiting table than
    /// any amount of progress reporting afterwards.
    /// </remarks>
    public DateTime? EstimatedReadyAtUtc { get; private set; }

    /// <summary>
    /// The caller's own id for the command that placed this order.
    /// </summary>
    /// <remarks>
    /// The single most likely duplicate in the product: a diner taps "add to order" on cafe wifi,
    /// sees nothing happen, and taps again. Idempotency runs through the <c>ProcessedCommand</c>
    /// store, so the second tap returns the first order rather than sending the kitchen two.
    /// </remarks>
    public Guid ClientCommandId { get; private set; }

    public DateTime PlacedAtUtc { get; private set; }

    /// <summary>
    /// The table's waiter when this order was placed - the person the sale is theirs from. Null when the table had
    /// none yet (a guest's phone ordered before any waiter touched it). A transfer later does not move it: what was
    /// done before stays with the earlier person. See <c>docs/activity.md</c>.
    /// </summary>
    public Guid? ServerStaffMemberId { get; private set; }

    /// <summary>Records whose table this was when the order was placed. Set once, as the order is built.</summary>
    public void TakenUnder(Guid? serverStaffMemberId) =>
        ServerStaffMemberId = serverStaffMemberId == Guid.Empty ? null : serverStaffMemberId;

    public TabOrderStatus Status { get; private set; }

    public IReadOnlyCollection<TabOrderLine> Lines => _lines;

    private TabOrder()
    {
    }

    private TabOrder(
        Guid tabId,
        Guid? placedByParticipantId,
        Guid? placedByStaffId,
        DateTime placedAtUtc,
        Guid? onBehalfOfParticipantId = null,
        Guid? clientCommandId = null,
        Guid? id = null)
        : base(id ?? Guid.CreateVersion7())
    {
        if (onBehalfOfParticipantId is not null && placedByStaffId is null)
        {
            throw new ArgumentException(
                "Only a waiter keys in an order on somebody's behalf; a diner ordering from their own "
                + "phone is already the person it is for.",
                nameof(onBehalfOfParticipantId));
        }

        OnBehalfOfParticipantId = onBehalfOfParticipantId;
        ClientCommandId = clientCommandId ?? Guid.CreateVersion7();
        if (placedByParticipantId is null == (placedByStaffId is null))
        {
            throw new ArgumentException(
                "An order is placed either by a participant or by a staff member, never both and never neither.",
                nameof(placedByParticipantId));
        }

        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        PlacedByParticipantId = placedByParticipantId;
        PlacedByStaffId = placedByStaffId;
        PlacedAtUtc = Guard.NotLocalTime(placedAtUtc, nameof(placedAtUtc));
        Status = TabOrderStatus.New;
    }

    /// <summary>An order a diner placed from their own phone.</summary>
    public static TabOrder PlacedByDiner(
        Guid tabId, Guid participantId, DateTime placedAtUtc, Guid? clientCommandId = null) =>
        new(tabId, Guard.NotEmpty(participantId, nameof(participantId)), null, placedAtUtc,
            clientCommandId: clientCommandId);

    /// <summary>
    /// A spoken order a waiter keyed in on the staff tablet, optionally on a named diner's behalf.
    /// </summary>
    public static TabOrder PlacedByStaffMember(
        Guid tabId,
        Guid staffId,
        DateTime placedAtUtc,
        Guid? onBehalfOfParticipantId = null,
        Guid? clientCommandId = null) =>
        new(tabId, null, Guard.NotEmpty(staffId, nameof(staffId)), placedAtUtc,
            onBehalfOfParticipantId, clientCommandId);

    /// <summary>
    /// A booking's pre-order arriving on the party's tab: the waiter who sent it to the kitchen placed it, when they
    /// sent it, and it keeps the pre-order's id - which was already its kitchen ticket's - so the kitchen screen's
    /// ticket does not change identity when the party sits down.
    /// </summary>
    public static TabOrder FromPreOrder(Guid preOrderId, Guid tabId, Guid sentByStaffId, DateTime sentAtUtc) =>
        new(tabId, null, Guard.NotEmpty(sentByStaffId, nameof(sentByStaffId)), sentAtUtc,
            id: Guard.NotEmpty(preOrderId, nameof(preOrderId)));

    /// <summary>
    /// Puts the built order on the tab - its lines added, its estimate and its server recorded - and raises
    /// <see cref="TabOrderPlaced"/>, which is what writes its activity entry and its tab-stream events.
    /// </summary>
    /// <remarks>
    /// A waiter's order is in the kitchen the moment it is sent (decided 2026-10-20): the waiter who took it at the table
    /// is already the check a phone's order waits at New for, so a second "send" tap could only be forgotten. It moves
    /// here, as part of being placed, and the event says so.
    /// </remarks>
    /// <exception cref="DomainStateException">The order was already placed.</exception>
    public void Place()
    {
        if (_placed || Status != TabOrderStatus.New)
        {
            throw new DomainStateException("This order is already on the tab.");
        }

        _placed = true;

        var straightToKitchen = PlacedByStaffId is not null;

        if (straightToKitchen)
        {
            Advance(TabOrderStatus.InKitchen);
        }

        Raise(new TabOrderPlaced(this, straightToKitchen, PlacedAtUtc));
    }

    /// <summary>
    /// Replays a booking's held kitchen ticket onto the order it becomes when the party sits: the rail's own moves up to
    /// where the ticket got, and a voided ticket's lines off the bill. Raises nothing - the kitchen's moves happened to
    /// the pre-order and were told then (its activity entry, signal 6); arriving on the tab is not a new one.
    /// </summary>
    /// <exception cref="DomainStateException">The rail does not reach <paramref name="reached"/> from New.</exception>
    public void ArriveFromHeldTicket(TabOrderStatus reached, DateTime voidedAtUtc)
    {
        if (reached == TabOrderStatus.Voided)
        {
            // A voided ticket never reaches a tab (MarkAttached refuses it), but if one did its lines would come off the
            // bill with it, as any voided order's do - never a voided order still charging for its food.
            TakeOff(voidedAtUtc, null, WholeOrderVoidReason);

            return;
        }

        foreach (var step in new[] { TabOrderStatus.InKitchen, TabOrderStatus.Ready, TabOrderStatus.Served })
        {
            if (Status == reached)
            {
                break;
            }

            Advance(step);
        }
    }

    /// <summary>
    /// The guest a waiter keyed this line in for, as every read reports it: the line's own, else the order's - and
    /// nobody on a split line, which belongs to the table whatever the order says.
    /// </summary>
    public Guid? OnBehalfOf(TabOrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.IsSplitAcrossParticipants ? null : line.OnBehalfOfParticipantId ?? OnBehalfOfParticipantId;
    }

    /// <summary>
    /// Who owns the items on this order, or null when it belongs to the table. A line can say
    /// otherwise for itself - see <see cref="OwnerOf"/>.
    /// </summary>
    public Guid? OwningParticipantId => PlacedByParticipantId ?? OnBehalfOfParticipantId;

    /// <summary>
    /// Who one of this order's lines is charged to whole, or null when it splits across the table.
    /// </summary>
    /// <remarks>
    /// The line's own <see cref="TabOrderLine.OnBehalfOfParticipantId"/> first, then the order's
    /// owner. A split line - shared, or table-attributed - is nobody's, whatever the order says.
    /// </remarks>
    public Guid? OwnerOf(TabOrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.IsSplitAcrossParticipants ? null : line.OnBehalfOfParticipantId ?? OwningParticipantId;
    }

    /// <summary>Records the kitchen estimate, from the longest prep time across the lines.</summary>
    public void EstimateReadyAt(DateTime estimatedReadyAtUtc) =>
        EstimatedReadyAtUtc = Guard.NotLocalTime(estimatedReadyAtUtc, nameof(estimatedReadyAtUtc));

    /// <summary>
    /// Adds a line, snapshotting the item's name and price as they stand right now.
    /// </summary>
    /// <remarks>
    /// <c>onBehalfOfParticipantId</c> is who a waiter said this one line was for, overriding
    /// <see cref="OnBehalfOfParticipantId"/> for it alone - see
    /// <see cref="TabOrderLine.OnBehalfOfParticipantId"/>.
    /// </remarks>
    public TabOrderLine AddLine(
        Guid menuItemId,
        string nameSnapshot,
        long unitPriceAmdSnapshot,
        int quantity,
        bool isShared = false,
        bool isTableAttributed = false,
        string? note = null,
        Guid? onBehalfOfParticipantId = null)
    {
        if (onBehalfOfParticipantId is not null && PlacedByStaffId is null)
        {
            throw new ArgumentException(
                "Only a waiter keys in a line on somebody's behalf; a diner ordering from their own "
                + "phone is already the person it is for.",
                nameof(onBehalfOfParticipantId));
        }

        var line = new TabOrderLine(
            Id, menuItemId, nameSnapshot, unitPriceAmdSnapshot, quantity, isShared, isTableAttributed, note,
            onBehalfOfParticipantId);

        _lines.Add(line);

        return line;
    }

    /// <summary>
    /// Moves the order along the kitchen's rail: New, InKitchen, Ready, Served.
    /// </summary>
    /// <remarks>
    /// Named transitions against an explicit table, the same shape as the table state machine and
    /// for the same reason: a status column anyone can assign eventually holds something nobody
    /// expected. Going backwards is refused - a Ready order tapped back to InKitchen loses the fact
    /// that it was ever cooked, and the kitchen screen and the floor then disagree about whether
    /// food exists.
    /// </remarks>
    /// <exception cref="DomainStateException">
    /// The rail does not allow the move - or it is <see cref="TabOrderStatus.Voided"/>, which goes through
    /// <see cref="Void"/> so the lines come off the bill with it.
    /// </exception>
    public void MoveTo(TabOrderStatus next, DateTime movedAtUtc, Guid? byStaffId)
    {
        Guard.Defined(next, nameof(next));

        if (next == TabOrderStatus.Voided)
        {
            throw new DomainStateException(
                "Voiding an order takes its lines off the bill, so it goes through TabOrder.Void, not MoveTo.");
        }

        var from = Status;
        Advance(next);

        Raise(new TabOrderMoved(this, from, next, byStaffId, Guard.NotLocalTime(movedAtUtc, nameof(movedAtUtc))));
    }

    /// <summary>
    /// Takes one line off the bill on its own - stamped with when, why and by whom - and raises
    /// <see cref="TabOrderLineVoided"/>. The order stays where it is on the rail.
    /// </summary>
    /// <exception cref="ArgumentException">The line is not on this order.</exception>
    /// <exception cref="DomainStateException">The line is already voided.</exception>
    public void VoidLine(TabOrderLine line, DateTime voidedAtUtc, string reason, Guid voidedByStaffId)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!_lines.Contains(line))
        {
            throw new ArgumentException("That line is not on this order.", nameof(line));
        }

        line.Void(voidedAtUtc, reason, voidedByStaffId);

        Raise(new TabOrderLineVoided(this, line, reason, voidedByStaffId, voidedAtUtc));
    }

    /// <summary>The reason a line carries when it came off the bill with its whole order.</summary>
    public const string WholeOrderVoidReason = "The whole order was voided.";

    /// <summary>
    /// Voids the whole order: off the rail, and <b>every line still on the bill comes off it</b>, stamped with when,
    /// by whom and <see cref="WholeOrderVoidReason"/> - exactly as if each had been voided on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bill is the lines (<c>docs/billing.md</c>): every total - the tab, the diner's view, the balance a payment
    /// is reserved against, the revenue and staff reports - adds up the lines whose <see cref="TabOrderLine.VoidedAtUtc"/>
    /// is empty, and none of them looks at the order's status. A voided order whose lines were left alone was
    /// therefore still charged in full. Stamping the lines keeps that one rule true rather than teaching every reader a
    /// second one. A line voided earlier on its own keeps its own stamp and reason.
    /// </para>
    /// <para>
    /// Whether the tab may still lose money is the caller's question: a void is refused once the tab has been paid
    /// against, since that is a refund (<see cref="LineAlreadyPaidException"/>).
    /// </para>
    /// </remarks>
    /// <returns>The lines this took off the bill - none when every one was already voided.</returns>
    /// <exception cref="DomainStateException">The rail does not allow voiding from where the order is.</exception>
    public IReadOnlyList<TabOrderLine> Void(DateTime voidedAtUtc, Guid? voidedByStaffId, string reason = WholeOrderVoidReason)
    {
        var from = Status;
        var takenOff = TakeOff(voidedAtUtc, voidedByStaffId, reason);

        Raise(new TabOrderVoided(this, from, takenOff, voidedByStaffId, voidedAtUtc));

        return takenOff;
    }

    /// <summary>The void itself, told by <see cref="Void"/> and replayed silently by <see cref="ArriveFromHeldTicket"/>.</summary>
    private List<TabOrderLine> TakeOff(DateTime voidedAtUtc, Guid? voidedByStaffId, string reason)
    {
        RequireRailAllows(TabOrderStatus.Voided);

        var takenOff = _lines.Where(l => !l.IsVoided).ToList();

        foreach (var line in takenOff)
        {
            line.Void(voidedAtUtc, reason, voidedByStaffId);
        }

        Status = TabOrderStatus.Voided;

        return takenOff;
    }

    /// <summary>One move along the rail by its rules, and nothing else - the raising is the caller's.</summary>
    private void Advance(TabOrderStatus next)
    {
        RequireRailAllows(next);

        Status = next;
    }

    private void RequireRailAllows(TabOrderStatus next)
    {
        var allowed = Status switch
        {
            TabOrderStatus.New => next is TabOrderStatus.InKitchen or TabOrderStatus.Voided,
            TabOrderStatus.InKitchen => next is TabOrderStatus.Ready or TabOrderStatus.Voided,
            TabOrderStatus.Ready => next is TabOrderStatus.Served,
            _ => false,
        };

        if (!allowed)
        {
            throw new DomainStateException($"An order cannot go from {Status} to {next}.");
        }
    }
}
