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
        Guid? clientCommandId = null)
        : base(Guid.CreateVersion7())
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

    /// <summary>Who owns the items on this order, or null when it belongs to the table.</summary>
    public Guid? OwningParticipantId => PlacedByParticipantId ?? OnBehalfOfParticipantId;

    /// <summary>Records the kitchen estimate, from the longest prep time across the lines.</summary>
    public void EstimateReadyAt(DateTime estimatedReadyAtUtc) =>
        EstimatedReadyAtUtc = Guard.NotLocalTime(estimatedReadyAtUtc, nameof(estimatedReadyAtUtc));

    /// <summary>
    /// Adds a line, snapshotting the item's name and price as they stand right now.
    /// </summary>
    public TabOrderLine AddLine(
        Guid menuItemId,
        string nameSnapshot,
        long unitPriceAmdSnapshot,
        int quantity,
        bool isShared = false,
        bool isTableAttributed = false,
        string? note = null)
    {
        var line = new TabOrderLine(
            Id, menuItemId, nameSnapshot, unitPriceAmdSnapshot, quantity, isShared, isTableAttributed, note);

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
    public void MoveTo(TabOrderStatus next)
    {
        Guard.Defined(next, nameof(next));

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

        Status = next;
    }
}
