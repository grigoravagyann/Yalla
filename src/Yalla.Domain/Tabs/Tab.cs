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

    /// <summary>
    /// Whether the people on this tab may order yet - see <see cref="TabAdmission"/>.
    /// </summary>
    /// <remarks>
    /// A photographed QR code lets somebody open a tab from home, so a scan whose host has no proved
    /// phone number waits for a waiter who can see the table. Only ordering waits: the tab can be read,
    /// joiners approved and a waiter called.
    /// </remarks>
    public TabAdmission Admission { get; private set; }

    /// <summary>Why it was admitted; null while waiting, once turned away, and on tabs from before admission.</summary>
    public TabAdmissionReason? AdmittedBecause { get; private set; }

    /// <summary>When it was admitted or turned away - for an automatic admission, when it opened.</summary>
    public DateTime? AdmissionDecidedAtUtc { get; private set; }

    /// <summary>The waiter who admitted it, turned it away, opened it, or keyed in the order that admitted it.</summary>
    public Guid? AdmissionDecidedByStaffId { get; private set; }

    /// <summary>Whether the tab takes orders as far as admission goes. The participants' own flags still apply.</summary>
    public bool IsAdmitted => Admission == TabAdmission.Admitted;

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
        TabAdmissionReason? admittedBecause,
        SettlementMode settlementMode = SettlementMode.AnyonePaysAnyAmount,
        bool hideTotalFromGuests = false,
        Guid? clientCommandId = null,
        Guid? admittedByStaffId = null)
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

        // Required, with no default, on purpose: whoever opens a tab says whether it may order. Null is
        // a scan by somebody with no proved number - it waits for a waiter.
        if (admittedBecause is { } because)
        {
            Admission = TabAdmission.Admitted;
            AdmittedBecause = Guard.Defined(because, nameof(admittedBecause));
            AdmissionDecidedAtUtc = OpenedAtUtc;
            AdmissionDecidedByStaffId = admittedByStaffId;
        }
        else
        {
            Admission = TabAdmission.AwaitingAdmission;
        }
    }

    /// <summary>
    /// Lets a tab that was waiting order, and raises <see cref="TabAdmitted"/>. Answers false, changing and raising
    /// nothing, when it is already admitted.
    /// </summary>
    /// <param name="atUtc">Now.</param>
    /// <param name="because">A waiter's tap or the booker arriving.</param>
    /// <param name="byStaffId">The waiter, when it was one.</param>
    /// <exception cref="TabAdmissionDecidedException">A waiter already turned it away.</exception>
    public bool Admit(DateTime atUtc, TabAdmissionReason because, Guid? byStaffId)
    {
        if (Admission == TabAdmission.Admitted)
        {
            return false;
        }

        if (Admission == TabAdmission.Rejected)
        {
            throw new TabAdmissionDecidedException(Id, Admission);
        }

        Admission = TabAdmission.Admitted;
        AdmittedBecause = Guard.Defined(because, nameof(because));
        AdmissionDecidedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        AdmissionDecidedByStaffId = byStaffId;

        Raise(new TabAdmitted(this, because, byStaffId, AdmissionDecidedAtUtc.Value));

        return true;
    }

    /// <summary>
    /// A waiter turns away a tab that was waiting: nobody they can see is sitting there. The tab closes, and
    /// <see cref="TabTurnedAway"/> is raised. Answers false, changing and raising nothing, when it was already turned
    /// away.
    /// </summary>
    /// <remarks>
    /// Nothing can be on it - no phone order is accepted before admission, and the caller refuses a tab a
    /// waiter has keyed orders on - so closing it settles nothing. What happens to the table is the caller's:
    /// freed (or held again) when the scan seated it, left as it is when a waiter or a booking did.
    /// </remarks>
    /// <param name="atUtc">Now.</param>
    /// <param name="byStaffId">The waiter.</param>
    /// <param name="reason">Why, as the waiter gave it; null when they gave none.</param>
    /// <param name="tableFreed">The scan seated the table, and the caller gives it back with the tab.</param>
    /// <exception cref="TabAdmissionDecidedException">It was already admitted.</exception>
    /// <exception cref="DomainStateException">There is money on it, which cannot happen through this API.</exception>
    public bool RejectAdmission(DateTime atUtc, Guid byStaffId, string? reason, bool tableFreed)
    {
        if (Admission == TabAdmission.Rejected)
        {
            return false;
        }

        if (Admission == TabAdmission.Admitted)
        {
            throw new TabAdmissionDecidedException(Id, Admission);
        }

        if (TotalAmd > 0L || PaidAmd > 0L)
        {
            throw new DomainStateException("This tab has items on it and cannot be turned away. Close it as usual.");
        }

        Admission = TabAdmission.Rejected;
        AdmittedBecause = null;
        AdmissionDecidedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        AdmissionDecidedByStaffId = Guard.NotEmpty(byStaffId, nameof(byStaffId));

        if (Status is TabStatus.Open or TabStatus.Closing)
        {
            Status = TabStatus.Closed;
            ClosedAtUtc = AdmissionDecidedAtUtc;
        }

        Raise(new TabTurnedAway(this, reason, tableFreed, byStaffId, AdmissionDecidedAtUtc.Value));

        return true;
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

    /// <summary>
    /// Records which participant hosts the tab, raising nothing: the first phone onto a tab a waiter opened with nobody
    /// on it takes the role unannounced, and a test seeds a tab with it. A scan that opens the tab is
    /// <see cref="OpenWithHost"/>.
    /// </summary>
    public void SetHostParticipant(Guid participantId) =>
        HostParticipantId = Guard.NotEmpty(participantId, nameof(participantId));

    /// <summary>
    /// A phone's scan - or a booker with their code - opened this tab and hosts it: records the host and raises
    /// <see cref="TabOpenedByScan"/>, which tells the floor when the tab waits for a waiter.
    /// </summary>
    /// <param name="host">The host, on this tab.</param>
    public void OpenWithHost(TabParticipant host)
    {
        Own(host);

        if (!host.IsHost)
        {
            throw new ArgumentException("The phone that opens a tab hosts it.", nameof(host));
        }

        HostParticipantId = host.Id;

        Raise(new TabOpenedByScan(this, host, OpenedAtUtc));
    }

    /// <summary>
    /// A tab a waiter opens on a seated table, with nobody on it: it may order at once, and the waiter is recorded as
    /// having decided so. Raises <see cref="TabOpenedByStaff"/>.
    /// </summary>
    /// <param name="branchId">The branch.</param>
    /// <param name="diningTableId">The table.</param>
    /// <param name="tableSessionId">The sitting it is the bill for.</param>
    /// <param name="openedAtUtc">Now.</param>
    /// <param name="serviceChargePercentSnapshot">The branch's service charge as it stands.</param>
    /// <param name="clientCommandId">The tablet's id for the tap.</param>
    /// <param name="byStaffId">The waiter.</param>
    public static Tab OpenByStaff(
        Guid branchId,
        Guid diningTableId,
        Guid tableSessionId,
        DateTime openedAtUtc,
        decimal serviceChargePercentSnapshot,
        Guid clientCommandId,
        Guid? byStaffId)
    {
        var tab = new Tab(
            branchId,
            diningTableId,
            tableSessionId,
            openedAtUtc,
            serviceChargePercentSnapshot,
            TabAdmissionReason.OpenedByStaff,
            clientCommandId: clientCommandId,
            admittedByStaffId: byStaffId);

        tab.Raise(new TabOpenedByStaff(tab, byStaffId, tab.OpenedAtUtc));

        return tab;
    }

    /// <summary>
    /// Moves the host role to another person on the tab, and raises <see cref="TabHostMoved"/>: a waiter's move - the
    /// host has left early or their phone has died, and without this the tab is stuck with nobody able to approve
    /// joiners or change the split - or the host leaving and handing it on (<see cref="Leave"/>).
    /// </summary>
    /// <remarks>
    /// Order matters: the tab refuses a closed tab and a no-op before either participant is touched, and the new host
    /// must be approved before they can take the role. The old host steps down to guest, keeping what they may do.
    /// </remarks>
    /// <param name="newHost">The approved participant who takes over, on this tab.</param>
    /// <param name="atUtc">Now.</param>
    /// <param name="hostLeft">The host is leaving the tab and hands it on.</param>
    /// <exception cref="DomainStateException">The tab is closed, they already host it, or they are not approved.</exception>
    public void MoveHostTo(TabParticipant newHost, DateTime atUtc, bool hostLeft)
    {
        Own(newHost);
        Guard.NotLocalTime(atUtc, nameof(atUtc));

        if (!IsActive)
        {
            throw new DomainStateException($"This tab is {Status}; a closed tab has no host to reassign.");
        }

        if (HostParticipantId == newHost.Id)
        {
            throw new DomainStateException("That participant is already the host.");
        }

        // Checked before anything changes, so a refusal leaves the tab answering to its old host.
        if (newHost.Status != ParticipantStatus.Approved)
        {
            throw new DomainStateException(
                $"Only an approved participant can become the host; this one is {newHost.Status}.");
        }

        var oldHost = _participants.FirstOrDefault(p => p.Id == HostParticipantId);

        HostParticipantId = newHost.Id;
        newHost.BecomeHost();
        oldHost?.BecomeGuest();

        Raise(new TabHostMoved(this, oldHost, newHost, hostLeft, atUtc));
    }

    /// <summary>Somebody new landed on the tab pending, and waits for the host: raises <see cref="TabJoinRequested"/>.</summary>
    /// <param name="joiner">The newcomer, pending, on this tab.</param>
    /// <param name="givenName">The name they gave, or null.</param>
    /// <param name="atUtc">Now.</param>
    public void AskToJoin(TabParticipant joiner, string? givenName, DateTime atUtc)
    {
        Own(joiner);

        if (joiner.Status != ParticipantStatus.PendingApproval)
        {
            throw new ArgumentException("Only somebody waiting to be let on asks to join.", nameof(joiner));
        }

        Raise(new TabJoinRequested(this, joiner, givenName, Guard.NotLocalTime(atUtc, nameof(atUtc))));
    }

    /// <summary>Lets somebody on and raises <see cref="TabParticipantApproved"/>.</summary>
    /// <param name="participant">The participant, on this tab.</param>
    /// <param name="atUtc">Now.</param>
    /// <param name="asBooker">The booking lets them on, not the host's tap.</param>
    /// <exception cref="DomainStateException">They were removed.</exception>
    public void ApproveParticipant(TabParticipant participant, DateTime atUtc, bool asBooker)
    {
        Own(participant).Approve(atUtc);

        Raise(new TabParticipantApproved(this, participant, asBooker, atUtc));
    }

    /// <summary>The host turns away somebody waiting to be let on; raises <see cref="TabParticipantRejected"/>.</summary>
    /// <exception cref="DomainStateException">They were not waiting.</exception>
    public void RejectParticipant(TabParticipant participant, DateTime atUtc)
    {
        Own(participant).Reject(atUtc);

        Raise(new TabParticipantRejected(this, participant, atUtc));
    }

    /// <summary>The host takes somebody off the tab; raises <see cref="TabParticipantRemoved"/>.</summary>
    /// <exception cref="DomainStateException">They are the host.</exception>
    public void RemoveParticipant(TabParticipant participant, DateTime atUtc)
    {
        Own(participant).Remove(atUtc);

        Raise(new TabParticipantRemoved(this, participant, Left: false, atUtc));
    }

    /// <summary>
    /// Somebody takes themself off the tab - a host first handing it to <paramref name="successor"/>
    /// (<see cref="MoveHostTo"/>) - and <see cref="TabParticipantRemoved"/> is raised as their own doing.
    /// </summary>
    /// <param name="leaving">The participant leaving, on this tab.</param>
    /// <param name="successor">Who takes the tab over, when the host is the one leaving.</param>
    /// <param name="atUtc">Now.</param>
    public void Leave(TabParticipant leaving, TabParticipant? successor, DateTime atUtc)
    {
        Own(leaving);

        if (successor is not null)
        {
            MoveHostTo(successor, atUtc, hostLeft: true);
        }

        leaving.Remove(atUtc);

        Raise(new TabParticipantRemoved(this, leaving, Left: true, atUtc));
    }

    /// <summary>The host sets what somebody may do; raises <see cref="TabParticipantPermissionsChanged"/>.</summary>
    /// <exception cref="FieldValidationException">The host's own sight of the total switched off.</exception>
    /// <exception cref="ArgumentException">Paying without seeing the total.</exception>
    public void SetParticipantPermissions(
        TabParticipant participant, bool canOrder, bool canSeeTableTotal, bool canPay, DateTime atUtc)
    {
        Own(participant).SetPermissions(canOrder, canSeeTableTotal, canPay);

        Raise(new TabParticipantPermissionsChanged(this, participant, atUtc));
    }

    /// <summary>Somebody puts their name on the tab; raises <see cref="TabParticipantRenamed"/>.</summary>
    public void RenameParticipant(TabParticipant participant, string displayName, DateTime atUtc)
    {
        Own(participant).SetDisplayName(displayName);

        Raise(new TabParticipantRenamed(this, participant, atUtc));
    }

    /// <summary>The participant, once it is known to be on this tab.</summary>
    private TabParticipant Own(TabParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        if (participant.TabId != Id)
        {
            throw new ArgumentException("That participant is on another tab.", nameof(participant));
        }

        return participant;
    }

    /// <summary>The host chooses how the bill is split; raises <see cref="TabSettlementModeChanged"/>.</summary>
    /// <exception cref="DomainStateException">The split is locked: money has landed against it.</exception>
    public void SetSettlementMode(SettlementMode settlementMode, DateTime atUtc)
    {
        if (SettlementModeLockedAtUtc is not null)
        {
            throw new DomainStateException("The settlement mode is locked and can no longer be changed.");
        }

        SettlementMode = Guard.Defined(settlementMode, nameof(settlementMode));

        Raise(new TabSettlementModeChanged(this, Guard.NotLocalTime(atUtc, nameof(atUtc))));
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
    /// Takes a payment that went through onto the bill, and raises <see cref="TabPaymentRecorded"/>: the split locks,
    /// and a bill now paid in full closes - with its sitting, when that is still open (<see cref="TabSettled"/>). The
    /// table is not freed: a party that has paid usually sits on.
    /// </summary>
    /// <remarks>
    /// The caller has added the payment and applied the totals with it counted (the ledger's arithmetic), so
    /// <see cref="RemainingAmd"/> is already what is owed after it.
    /// </remarks>
    /// <param name="payment">The payment, succeeded, on this tab.</param>
    /// <param name="atUtc">Now.</param>
    /// <param name="sitting">The tab's sitting, when the caller has it; closed with the bill when still open.</param>
    /// <returns>Whether the tab closed, and whether its sitting closed with it.</returns>
    /// <exception cref="DomainStateException">The tab is already closed.</exception>
    public (bool Closed, bool SittingClosed) TakePayment(Payment payment, DateTime atUtc, TableSession? sitting)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.TabId != Id || payment.Status != PaymentStatus.Succeeded)
        {
            throw new ArgumentException("Only a payment on this tab that went through is taken onto it.", nameof(payment));
        }

        if (!IsActive)
        {
            throw new DomainStateException("This tab is already closed.");
        }

        Guard.NotLocalTime(atUtc, nameof(atUtc));

        // The split cannot be renegotiated once money has landed against it.
        LockSettlementMode(atUtc);

        var settles = RemainingAmd == 0L;

        Raise(new TabPaymentRecorded(this, payment, settles, atUtc));

        if (!settles)
        {
            return (false, false);
        }

        Close(atUtc);

        var sittingClosed = false;

        if (sitting is { IsOpen: true } && sitting.Id == TableSessionId)
        {
            sitting.Close(atUtc);
            sittingClosed = true;
        }

        Raise(new TabSettled(this, sittingClosed, atUtc));

        return (true, sittingClosed);
    }

    /// <summary>
    /// Writes off an outstanding balance - the diners left without paying and the venue is accepting the loss - and
    /// raises <see cref="TabWrittenOff"/> with what was still owed.
    /// </summary>
    /// <remarks>
    /// The role check lives in the service layer, which is where the acting staff member is
    /// known - this is manager-only, because it is a decision about money rather than about the
    /// floor.
    /// </remarks>
    /// <exception cref="DomainStateException">The tab is already closed.</exception>
    public void WriteOff(DateTime abandonedAtUtc, Guid byStaffId, string? reason)
    {
        if (Status is TabStatus.Closed or TabStatus.Abandoned)
        {
            throw new DomainStateException("This tab is already closed.");
        }

        Guard.NotEmpty(byStaffId, nameof(byStaffId));

        Status = TabStatus.Abandoned;
        ClosedAtUtc = Guard.NotLocalTime(abandonedAtUtc, nameof(abandonedAtUtc));

        Raise(new TabWrittenOff(this, RemainingAmd, reason, byStaffId, ClosedAtUtc.Value));
    }

    /// <summary>
    /// A waiter presents the bill: no new orders and no new participants. Raises <see cref="TabBillPresented"/>.
    /// </summary>
    public void BeginClosing(DateTime atUtc)
    {
        if (Status != TabStatus.Open)
        {
            throw new DomainStateException($"A tab must be open to begin closing; this one is {Status}.");
        }

        Status = TabStatus.Closing;

        Raise(new TabBillPresented(this, Guard.NotLocalTime(atUtc, nameof(atUtc))));
    }
}
