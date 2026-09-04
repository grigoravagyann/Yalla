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

    public DateTime PlacedAtUtc { get; private set; }

    public TabOrderStatus Status { get; private set; }

    public IReadOnlyCollection<TabOrderLine> Lines => _lines;

    private TabOrder()
    {
    }

    private TabOrder(Guid tabId, Guid? placedByParticipantId, Guid? placedByStaffId, DateTime placedAtUtc)
        : base(Guid.CreateVersion7())
    {
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
    public static TabOrder PlacedByDiner(Guid tabId, Guid participantId, DateTime placedAtUtc) =>
        new(tabId, Guard.NotEmpty(participantId, nameof(participantId)), null, placedAtUtc);

    /// <summary>A spoken order a waiter keyed in on the staff tablet.</summary>
    public static TabOrder PlacedByStaffMember(Guid tabId, Guid staffId, DateTime placedAtUtc) =>
        new(tabId, null, Guard.NotEmpty(staffId, nameof(staffId)), placedAtUtc);

    /// <summary>
    /// Adds a line, snapshotting the item's name and price as they stand right now.
    /// </summary>
    public TabOrderLine AddLine(
        Guid menuItemId,
        string nameSnapshot,
        long unitPriceAmdSnapshot,
        int quantity,
        bool isShared = false)
    {
        var line = new TabOrderLine(Id, menuItemId, nameSnapshot, unitPriceAmdSnapshot, quantity, isShared);
        _lines.Add(line);
        return line;
    }
}
