using Yalla.Domain.Common;
using Yalla.Domain.Menus;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One item on an order: two flat whites, one shared plate of cheese.
/// </summary>
/// <remarks>
/// The item's name and unit price are <b>snapshotted</b> onto the line. A menu edit next week -
/// a price rise, a rename, a deletion - must never change what a closed bill says the guest
/// agreed to pay. <see cref="MenuItemId"/> is kept only to link back to the live item for
/// reporting.
/// </remarks>
public sealed class TabOrderLine : Entity
{
    private readonly List<TabOrderLineShare> _shares = [];

    public Guid TabOrderId { get; private set; }

    public TabOrder TabOrder { get; private set; } = null!;

    public Guid MenuItemId { get; private set; }

    public MenuItem MenuItem { get; private set; } = null!;

    /// <summary>The item name as it read on the menu when this was ordered.</summary>
    public string NameSnapshot { get; private set; } = null!;

    /// <summary>The unit price in whole dram as it stood when this was ordered.</summary>
    public long UnitPriceAmdSnapshot { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>
    /// True when the item belongs to the table rather than to one person, and its cost splits
    /// across the participants recorded in <see cref="Shares"/>.
    /// </summary>
    public bool IsShared { get; private set; }

    /// <summary>
    /// True when nobody owns this line: a waiter keyed in a spoken order and could not say who
    /// asked for it.
    /// </summary>
    /// <remarks>
    /// It splits across everyone present, exactly like a shared line, and carries its own snapshot
    /// rows for the same reason. The flag exists so the difference stays visible: "the table shared
    /// a bottle" and "we do not know who ordered this" produce identical arithmetic and mean
    /// completely different things. This is the seam where paper-ordering habits leak into the
    /// data, and a venue whose bills are full of table-attributed lines has a training problem the
    /// numbers should be able to show them.
    /// </remarks>
    public bool IsTableAttributed { get; private set; }

    /// <summary>What the guest asked for: no onions, extra hot. Goes to the kitchen, not the bill.</summary>
    public string? Note { get; private set; }

    /// <summary>
    /// Who a waiter said this one line was for, when it is not who the rest of the order was for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tablet assigns every line of a spoken round on its own - the latte to the table, the
    /// steak to Narine - and sends the round as one order, so the kitchen gets one ticket. The
    /// order's <see cref="TabOrder.OnBehalfOfParticipantId"/> is the default for its lines; this is
    /// the line's own answer, and wins. Null means "whoever the order was for", which is exactly
    /// what every line written before this column existed means.
    /// </para>
    /// <para>
    /// Never set on a shared line: a shared line has no owner, which is also why the order's default
    /// does not reach one. Only on a waiter's order, for the order-level field's reason - a diner
    /// ordering from their own phone is already the person it is for.
    /// </para>
    /// </remarks>
    public Guid? OnBehalfOfParticipantId { get; private set; }

    public TabParticipant? OnBehalfOfParticipant { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }

    public Guid? VoidedByStaffId { get; private set; }

    public StaffMember? VoidedByStaff { get; private set; }

    public string? VoidReason { get; private set; }

    /// <summary>
    /// Who was at the table when this item was ordered. See <see cref="TabOrderLineShare"/>.
    /// </summary>
    public IReadOnlyCollection<TabOrderLineShare> Shares => _shares;

    public bool IsVoided => VoidedAtUtc is not null;

    /// <summary>Line total in whole dram, ignoring the service charge. Zero once voided.</summary>
    /// <remarks>
    /// Zero rather than absent, because a voided line <b>stays on the bill and stays visible to the
    /// diner</b>, labelled as removed by staff. Nothing may silently disappear from a bill somebody
    /// is watching on their phone: a line that vanishes reads as the venue editing the bill, and the
    /// guest has no way to tell that apart from one.
    /// </remarks>
    public long LineTotalAmd => IsVoided ? 0L : UnitPriceAmdSnapshot * Quantity;

    /// <summary>True when this line splits across the participants snapshotted on it, for either reason.</summary>
    public bool IsSplitAcrossParticipants => IsShared || IsTableAttributed;

    private TabOrderLine()
    {
    }

    internal TabOrderLine(
        Guid tabOrderId,
        Guid menuItemId,
        string nameSnapshot,
        long unitPriceAmdSnapshot,
        int quantity,
        bool isShared,
        bool isTableAttributed = false,
        string? note = null,
        Guid? onBehalfOfParticipantId = null)
        : base(Guid.CreateVersion7())
    {
        if (onBehalfOfParticipantId is not null && (isShared || isTableAttributed))
        {
            throw new ArgumentException(
                "A shared line belongs to the table, so it cannot also be put in one guest's name.",
                nameof(onBehalfOfParticipantId));
        }

        OnBehalfOfParticipantId = onBehalfOfParticipantId;
        IsTableAttributed = isTableAttributed;
        Note = Guard.OptionalText(note, nameof(note), FieldLengths.OrderNote);
        TabOrderId = Guard.NotEmpty(tabOrderId, nameof(tabOrderId));
        MenuItemId = Guard.NotEmpty(menuItemId, nameof(menuItemId));
        NameSnapshot = Guard.NotBlank(nameSnapshot, nameof(nameSnapshot), FieldLengths.Name);
        UnitPriceAmdSnapshot = Guard.NotNegativeAmd(unitPriceAmdSnapshot, nameof(unitPriceAmdSnapshot));
        Quantity = Guard.Positive(quantity, nameof(quantity));
        IsShared = isShared;
    }

    /// <summary>
    /// Records one participant as sharing this line. Called once per person present at the moment
    /// of ordering.
    /// </summary>
    public TabOrderLineShare AddShare(Guid tabParticipantId)
    {
        if (_shares.Any(s => s.TabParticipantId == tabParticipantId))
        {
            throw new DomainStateException("That participant already shares this line.");
        }

        var share = new TabOrderLineShare(Id, tabParticipantId);
        _shares.Add(share);
        return share;
    }

    public void Void(DateTime voidedAtUtc, string reason, Guid? voidedByStaffId = null)
    {
        if (IsVoided)
        {
            throw new DomainStateException("This line is already voided.");
        }

        VoidedAtUtc = Guard.NotLocalTime(voidedAtUtc, nameof(voidedAtUtc));
        VoidReason = Guard.NotBlank(reason, nameof(reason), FieldLengths.Reason);
        VoidedByStaffId = voidedByStaffId;
    }
}
