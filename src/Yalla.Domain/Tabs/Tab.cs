using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Tabs;

/// <summary>
/// The running bill for one seating: what was ordered, by whom, how it will be split, what is
/// still owed.
/// </summary>
/// <remarks>
/// A tab hangs off a <see cref="TableSession"/>, never off a reservation, because most tabs
/// belong to people who never booked. Anyone who scans the table QR code can open one.
/// </remarks>
public sealed class Tab : Entity
{
    private readonly List<TabParticipant> _participants = [];
    private readonly List<TabOrder> _orders = [];
    private readonly List<TabJoinToken> _joinTokens = [];
    private readonly List<Payment> _payments = [];

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DiningTableId { get; private set; }

    public DiningTable DiningTable { get; private set; } = null!;

    /// <summary>The occupancy this bill belongs to. Required: a tab without a seating is meaningless.</summary>
    public Guid TableSessionId { get; private set; }

    public TableSession TableSession { get; private set; } = null!;

    public TabStatus Status { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }

    public SettlementMode SettlementMode { get; private set; }

    /// <summary>
    /// Once settlement has begun the split cannot be renegotiated; null while it is still open
    /// to change.
    /// </summary>
    public DateTime? SettlementModeLockedAtUtc { get; private set; }

    /// <summary>
    /// The participant who opened the tab and, under
    /// <see cref="SettlementMode.HostPaysEverything"/>, owes the bill.
    /// </summary>
    /// <remarks>
    /// Not a foreign key: <c>TabParticipant.TabId</c> is the real key in the other direction and
    /// an opposing key here would make the pair circular.
    /// </remarks>
    public Guid? HostParticipantId { get; private set; }

    /// <summary>
    /// The branch's service charge as it stood when this tab opened. Snapshotted so that changing
    /// the branch policy tomorrow cannot alter a bill that has already been presented.
    /// </summary>
    public decimal ServiceChargePercentSnapshot { get; private set; }

    /// <summary>
    /// When true, guests see only their own items. A host paying for a business dinner does not
    /// necessarily want the table reading the total.
    /// </summary>
    /// <remarks>
    /// This is the <i>table default</i> for a guest's <c>CanSeeTableTotal</c> flag: it is read
    /// when a participant joins and copied onto their row. The host can then override any one
    /// person individually, so changing this later does not retroactively re-flag people who are
    /// already on the tab.
    /// </remarks>
    public bool HideTotalFromGuests { get; private set; }

    /// <summary>
    /// The caller's own id for the command that opened this tab. Unique across the table.
    /// </summary>
    /// <remarks>
    /// A phone on cafe wifi scans a QR code, sees nothing, and scans again. Without this the
    /// second scan would open a second tab - or, once the first one has landed, land the same
    /// person on their own tab as a pending guest. The unique index on this column is what makes
    /// the retry return the first scan's answer instead: a check-then-insert loses the race
    /// between two simultaneous retries, and the index does not. Tabs opened by code paths with
    /// no caller command - fixtures, seeding - take a fresh id so the index still holds.
    /// </remarks>
    public Guid ClientCommandId { get; private set; }

    /// <summary>
    /// Sum of the non-voided order lines, in whole Armenian dram.
    /// </summary>
    /// <remarks>
    /// <b>Server-computed only.</b> The client must never calculate a total, a service charge or
    /// a remaining balance and send it in - it may only display what the server reports. Every
    /// money field on this entity is assigned through <see cref="ApplyComputedTotals"/> and
    /// nowhere else. Amounts are whole dram (<see cref="long"/>): the dram has no subunit in
    /// practice, and decimal or floating-point money invites rounding drift across a split bill.
    /// </remarks>
    public long SubtotalAmd { get; private set; }

    /// <summary>Service charge in whole dram. Server-computed only; see <see cref="SubtotalAmd"/>.</summary>
    public long ServiceChargeAmd { get; private set; }

    /// <summary>Subtotal plus service charge, in whole dram. Server-computed only.</summary>
    public long TotalAmd { get; private set; }

    /// <summary>Successfully settled so far, in whole dram. Server-computed only.</summary>
    public long PaidAmd { get; private set; }

    /// <summary>
    /// Still owed, in whole dram. Server-computed only, and the value a payment reservation is
    /// taken against.
    /// </summary>
    public long RemainingAmd { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. This is what makes concurrent payments safe: two people
    /// tapping Pay at once cannot both reserve the same remaining dram.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<TabParticipant> Participants => _participants;

    public IReadOnlyCollection<TabOrder> Orders => _orders;

    public IReadOnlyCollection<TabJoinToken> JoinTokens => _joinTokens;

    public IReadOnlyCollection<Payment> Payments => _payments;

    /// <summary>
    /// Whether somebody new may still be put on this tab. Only while it is <see cref="TabStatus.Open"/>:
    /// once staff mark it closing, someone who already paid their share must not find a
    /// stranger's dessert added after they have left.
    /// </summary>
    public bool AcceptsNewParticipants => Status == TabStatus.Open;

    /// <summary>Whether the tab is still live in any sense - open or settling.</summary>
    public bool IsActive => Status is TabStatus.Open or TabStatus.Closing;

    private Tab()
    {
    }

    public Tab(
        Guid branchId,
        Guid diningTableId,
        Guid tableSessionId,
        DateTime openedAtUtc,
        decimal serviceChargePercentSnapshot,
        SettlementMode settlementMode = SettlementMode.AnyonePaysAnyAmount,
        bool hideTotalFromGuests = false,
        Guid? clientCommandId = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        TableSessionId = Guard.NotEmpty(tableSessionId, nameof(tableSessionId));
        OpenedAtUtc = Guard.NotLocalTime(openedAtUtc, nameof(openedAtUtc));
        SettlementMode = Guard.Defined(settlementMode, nameof(settlementMode));
        HideTotalFromGuests = hideTotalFromGuests;
        Status = TabStatus.Open;

        // A caller that has no command id - a fixture, a seeder - gets a synthetic one, so the
        // unique index still holds. A caller that HAS one must not pass Guid.Empty and mean it.
        ClientCommandId = clientCommandId is { } id
            ? Guard.NotEmpty(id, nameof(clientCommandId))
            : Guid.CreateVersion7();

        if (serviceChargePercentSnapshot < 0m || serviceChargePercentSnapshot > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceChargePercentSnapshot),
                serviceChargePercentSnapshot,
                "Service charge must be between 0 and 100 percent.");
        }

        ServiceChargePercentSnapshot = decimal.Round(serviceChargePercentSnapshot, 2);
    }

    /// <summary>
    /// The only way money is written to a tab. The caller supplies the two figures it summed from
    /// the order lines and the payments; the derived totals are computed here so that
    /// <see cref="TotalAmd"/> and <see cref="RemainingAmd"/> cannot disagree with their parts.
    /// </summary>
    public void ApplyComputedTotals(long subtotalAmd, long serviceChargeAmd, long paidAmd)
    {
        SubtotalAmd = Guard.NotNegativeAmd(subtotalAmd, nameof(subtotalAmd));
        ServiceChargeAmd = Guard.NotNegativeAmd(serviceChargeAmd, nameof(serviceChargeAmd));
        PaidAmd = Guard.NotNegativeAmd(paidAmd, nameof(paidAmd));
        TotalAmd = SubtotalAmd + ServiceChargeAmd;
        RemainingAmd = Math.Max(0L, TotalAmd - PaidAmd);
    }

    public void SetHostParticipant(Guid participantId) =>
        HostParticipantId = Guard.NotEmpty(participantId, nameof(participantId));

    /// <summary>
    /// Moves the host role to another person on the tab. A staff action: the host has left early
    /// or their phone has died, and without this the tab is stuck with nobody able to approve
    /// joiners or change the split.
    /// </summary>
    /// <remarks>
    /// The role change on the two participant rows is done by the caller, which has them loaded;
    /// this records which row the tab now answers to. It refuses a tab that is no longer live -
    /// there is nothing to host on a closed bill.
    /// </remarks>
    public void ReassignHost(Guid newHostParticipantId)
    {
        if (!IsActive)
        {
            throw new DomainStateException($"This tab is {Status}; a closed tab has no host to reassign.");
        }

        Guard.NotEmpty(newHostParticipantId, nameof(newHostParticipantId));

        if (HostParticipantId == newHostParticipantId)
        {
            throw new DomainStateException("That participant is already the host.");
        }

        HostParticipantId = newHostParticipantId;
    }

    public void SetSettlementMode(SettlementMode settlementMode)
    {
        if (SettlementModeLockedAtUtc is not null)
        {
            throw new DomainStateException("The settlement mode is locked and can no longer be changed.");
        }

        SettlementMode = Guard.Defined(settlementMode, nameof(settlementMode));
    }

    /// <summary>Freezes the split so it cannot be renegotiated once settlement has begun.</summary>
    public void LockSettlementMode(DateTime lockedAtUtc) =>
        SettlementModeLockedAtUtc ??= Guard.NotLocalTime(lockedAtUtc, nameof(lockedAtUtc));

    public void SetHideTotalFromGuests(bool hide) => HideTotalFromGuests = hide;

    /// <summary>
    /// Settles and closes the tab. Refuses while anything is still owed.
    /// </summary>
    /// <remarks>
    /// Note that this does not gate freeing the <i>table</i>. If the diners have left with a
    /// balance outstanding the table is freed anyway and the tab stays open for staff to resolve -
    /// physical state and financial state are independent on purpose, and a floor plan that lies
    /// about who is sitting where costs more than an unpaid tab.
    /// </remarks>
    public void Close(DateTime closedAtUtc)
    {
        if (Status is TabStatus.Closed or TabStatus.Abandoned)
        {
            throw new DomainStateException("This tab is already closed.");
        }

        if (RemainingAmd > 0L)
        {
            throw new DomainStateException(
                $"This tab still has {RemainingAmd} AMD outstanding and cannot be closed.");
        }

        Status = TabStatus.Closed;
        ClosedAtUtc = Guard.NotLocalTime(closedAtUtc, nameof(closedAtUtc));
    }

    /// <summary>
    /// Writes off an outstanding balance: the diners left without paying and the venue is
    /// accepting the loss.
    /// </summary>
    /// <remarks>
    /// The role check lives in the service layer, which is where the acting staff member is
    /// known - this is manager-only, because it is a decision about money rather than about the
    /// floor.
    /// </remarks>
    public void MarkAbandoned(DateTime abandonedAtUtc)
    {
        if (Status is TabStatus.Closed or TabStatus.Abandoned)
        {
            throw new DomainStateException("This tab is already closed.");
        }

        Status = TabStatus.Abandoned;
        ClosedAtUtc = Guard.NotLocalTime(abandonedAtUtc, nameof(abandonedAtUtc));
    }

    /// <summary>The bill has been asked for; no new orders and no new participants.</summary>
    public void BeginClosing()
    {
        if (Status != TabStatus.Open)
        {
            throw new DomainStateException($"A tab must be open to begin closing; this one is {Status}.");
        }

        Status = TabStatus.Closing;
    }
}
