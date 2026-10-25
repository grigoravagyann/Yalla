using Yalla.Domain.Billing;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Tabs;

/// <summary>
/// A table asking for something: napkins, water, the bill.
/// </summary>
/// <remarks>
/// <para>
/// The smallest thing in the product and one of the most useful. Catching a waiter's eye is the
/// single most common friction in a busy room, and it is worse for the diner who is shy, the one
/// sitting where the waiter's round does not pass, and the one who does not speak the language.
/// </para>
/// <para>
/// <b>Presets and one short note, deliberately not a chat.</b> A message box creates the expectation
/// of a reply, and on a Friday night nobody answers it - which turns a feature that was meant to
/// reduce friction into a second thing the table is waiting on. A fixed list is something a waiter
/// can act on at a glance from across the room without reading anything.
/// </para>
/// <para>
/// <b>A request for the bill may say how much.</b> The pay sheet lets a diner choose the whole bill
/// or their own share and a tip, and the request then carries the amount the server priced for that
/// choice (<see cref="BillQuote"/>). The four bill columns travel together: all null on every other
/// request and on a plain "the bill, please", all set - the tip pair as a pair - on one that chose.
/// </para>
/// <para>
/// <b>Asking again replaces the earlier ask.</b> A diner who asked for their share and then, on the
/// same sheet, for the whole bill has one bill in mind - the latest. Their earlier open request for
/// the bill is <see cref="Supersede">superseded</see> by the new one and leaves the waiter's list, so
/// nobody walks over with a figure the table has already changed its mind about.
/// </para>
/// </remarks>
public sealed class ServiceRequest : Entity
{
    /// <summary>Set by <see cref="Send"/>, so a call goes out once. Not persisted: only a new call is sent.</summary>
    private bool _sent;

    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>
    /// Denormalised from the tab so the floor screen's open-request list is one indexed read.
    /// </summary>
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>Which table to walk to. The only field the waiter actually needs.</summary>
    public Guid DiningTableId { get; private set; }

    public DiningTable DiningTable { get; private set; } = null!;

    /// <summary>Who asked. Null when a request survives the participant being removed.</summary>
    public Guid? RequestedByParticipantId { get; private set; }

    public TabParticipant? RequestedByParticipant { get; private set; }

    public ServiceRequestPreset Preset { get; private set; }

    /// <summary>One short line, optional. Not a conversation.</summary>
    public string? Note { get; private set; }

    /// <summary>
    /// Whose part of the bill a <see cref="ServiceRequestPreset.TheBill"/> request is for. Null on
    /// every other request, and on a request for the bill that did not say.
    /// </summary>
    public BillScope? BillScope { get; private set; }

    /// <summary>
    /// What <see cref="BillScope"/> left to pay when the request was raised, in whole dram: service
    /// charge included, tip excluded. A snapshot - the waiter is bringing the bill the diner was
    /// shown, and a coffee ordered since is the next conversation.
    /// </summary>
    public long? BillAmountAmd { get; private set; }

    /// <summary>The tip the diner chose on the pay sheet, in percent. Null when none was asked.</summary>
    public int? TipPercent { get; private set; }

    /// <summary>
    /// The tip in whole dram, priced with the request. <b>Outside the bill</b>, like
    /// <c>Payment.TipAmd</c>: it is what the waiter should expect on top of <see cref="BillAmountAmd"/>.
    /// </summary>
    public long? TipAmd { get; private set; }

    public DateTime? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedByStaffId { get; private set; }

    public StaffMember? AcknowledgedByStaff { get; private set; }

    /// <summary>
    /// When a waiter said "I'm on it". The call stays on the list, now showing who has it, until it is acknowledged;
    /// acknowledging a call nobody claimed claims it in the same moment. Its time from the call is the waiter's response
    /// time. See <c>docs/activity.md</c>.
    /// </summary>
    public DateTime? ClaimedAtUtc { get; private set; }

    /// <summary>Who claimed it - the first to; nobody takes a claim over.</summary>
    public Guid? ClaimedByStaffId { get; private set; }

    public StaffMember? ClaimedByStaff { get; private set; }

    /// <summary>
    /// A waiter says "I'm on it". The first claim stands; a second, or one on a call already answered or replaced, is a
    /// no-op answering false. The guest is not told and the call does not leave the list.
    /// </summary>
    /// <remarks>Raises <see cref="ServiceRequestClaimed"/> when it claimed: the History's <c>table.call-claimed</c>.</remarks>
    public bool Claim(DateTime claimedAtUtc, Guid staffId)
    {
        if (!IsOpen || ClaimedAtUtc is not null)
        {
            return false;
        }

        ClaimedAtUtc = Guard.NotLocalTime(claimedAtUtc, nameof(claimedAtUtc));
        ClaimedByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        Raise(new ServiceRequestClaimed(this, staffId, ClaimedAtUtc.Value));

        return true;
    }

    /// <summary>
    /// When the same participant asked for the bill again on this tab, and this request stopped being
    /// the one to bring. Null while it still is, and on every request that is not for the bill.
    /// </summary>
    public DateTime? SupersededAtUtc { get; private set; }

    /// <summary>Still waiting for somebody: neither seen by a waiter nor replaced by a later ask.</summary>
    public bool IsOpen => AcknowledgedAtUtc is null && SupersededAtUtc is null;

    private ServiceRequest()
    {
    }

    public ServiceRequest(
        Guid tabId,
        Guid branchId,
        Guid diningTableId,
        Guid? requestedByParticipantId,
        ServiceRequestPreset preset,
        DateTime createdAtUtc,
        string? note = null,
        BillQuote? bill = null)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        RequestedByParticipantId = requestedByParticipantId;
        Preset = Guard.Defined(preset, nameof(preset));
        Note = Guard.OptionalText(note, nameof(note), FieldLengths.ServiceNote);
        StampCreatedAt(createdAtUtc);

        if (bill is null)
        {
            return;
        }

        if (Preset != ServiceRequestPreset.TheBill)
        {
            throw new ArgumentException("Only a request for the bill carries an amount.", nameof(bill));
        }

        if ((bill.TipPercent is null) != (bill.TipAmd is null))
        {
            throw new ArgumentException("A tip is a percentage and an amount, or neither.", nameof(bill));
        }

        BillScope = Guard.Defined(bill.Scope, nameof(bill));
        BillAmountAmd = bill.AmountAmd > 0L
            ? bill.AmountAmd
            : throw new ArgumentOutOfRangeException(
                nameof(bill), bill.AmountAmd, "A bill to bring is for a positive amount.");
        TipPercent = bill.TipPercent is { } percent ? Guard.NotNegative(percent, nameof(bill)) : null;
        TipAmd = bill.TipAmd is { } tip ? Guard.NotNegativeAmd(tip, nameof(bill)) : null;
    }

    /// <summary>
    /// Puts the built call on the floor's list and raises <see cref="ServiceRequestRaised"/>, which is what tells the
    /// table's stream (<c>ServiceRequested</c>) and buzzes the floor's phones (the <c>tableCalls</c> staff push).
    /// </summary>
    /// <remarks>
    /// Apart from the constructor, as <c>TabOrder.Place</c> is, so a test can seed a call as history - at an instant of
    /// its choosing - without telling anybody.
    /// </remarks>
    /// <exception cref="DomainStateException">The call was already sent, or is no longer open.</exception>
    public void Send()
    {
        if (_sent || !IsOpen)
        {
            throw new DomainStateException("This call is already on the floor's list.");
        }

        _sent = true;

        Raise(new ServiceRequestRaised(this, CreatedAtUtc));
    }

    /// <summary>
    /// Refuses bill fields a request cannot carry, every one of them at once.
    /// </summary>
    /// <remarks>
    /// The shape only - whether there is anything left to pay is the price's question, answered by
    /// <see cref="BillQuote.For"/> against the tab. Field names are the wire ones.
    /// </remarks>
    /// <exception cref="FieldValidationException">
    /// <c>billScope</c> or <c>tipPercent</c> on a request that is not for the bill; a scope that is
    /// not one; a tip outside 0 to <see cref="BillQuote.MaxTipPercent"/>; or a tip with no scope to
    /// take it from.
    /// </exception>
    public static void ValidateBillFields(ServiceRequestPreset preset, BillScope? billScope, int? tipPercent)
    {
        var violations = new List<FieldViolation>();

        if (preset != ServiceRequestPreset.TheBill)
        {
            if (billScope is not null)
            {
                violations.Add(new FieldViolation(
                    "billScope",
                    "Only a request for the bill (preset 3) says whose part of it.",
                    FieldBounds.Conflict,
                    Value: (int)billScope.Value));
            }

            if (tipPercent is not null)
            {
                violations.Add(new FieldViolation(
                    "tipPercent",
                    "Only a request for the bill (preset 3) carries a tip.",
                    FieldBounds.Conflict,
                    Value: tipPercent.Value));
            }
        }
        else
        {
            if (billScope is { } scope && !Enum.IsDefined(scope))
            {
                violations.Add(new FieldViolation(
                    "billScope",
                    "Not a defined bill scope. 1 WholeBill, 2 MyShare.",
                    FieldBounds.Required,
                    Value: (int)scope));
            }
            else if (billScope is null && tipPercent is not null)
            {
                violations.Add(new FieldViolation(
                    "billScope",
                    "A tip is a percentage of a bill: say whose, 1 WholeBill or 2 MyShare.",
                    FieldBounds.Required));
            }

            if (tipPercent is < 0 or > BillQuote.MaxTipPercent)
            {
                violations.Add(new FieldViolation(
                    "tipPercent",
                    $"A tip is between 0 and {BillQuote.MaxTipPercent} percent.",
                    FieldBounds.Range,
                    Min: 0,
                    Max: BillQuote.MaxTipPercent,
                    Value: tipPercent.Value));
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }
    }

    /// <summary>A waiter has seen it. Acknowledging twice is a no-op, not an error.</summary>
    /// <remarks>
    /// Two waiters tapping the same request at the same moment is the normal case, not a race worth
    /// reporting: both saw it, which is the outcome the diner wanted. The first acknowledgement
    /// stands so the record says who actually went.
    /// <para>
    /// Raises <see cref="ServiceRequestAcknowledged"/> - the table's stream hears it - and, for a call nobody had
    /// claimed, <see cref="ServiceRequestClaimed"/> just before it: whoever answered had it, and that is the response
    /// time.
    /// </para>
    /// </remarks>
    public bool Acknowledge(DateTime acknowledgedAtUtc, Guid staffId)
    {
        var unclaimed = ClaimedAtUtc is null;

        if (!Answer(acknowledgedAtUtc, staffId))
        {
            return false;
        }

        if (unclaimed)
        {
            Raise(new ServiceRequestClaimed(this, staffId, AcknowledgedAtUtc!.Value));
        }

        Raise(new ServiceRequestAcknowledged(this, staffId, AcknowledgedAtUtc!.Value));

        return true;
    }

    /// <summary>
    /// The waiter who came over to a scan nobody admitted found nobody and turned the tab away: its open calls are
    /// answered by that person at that moment, so the rail drops them.
    /// </summary>
    /// <remarks>
    /// Raises nothing. What happened is the tab being turned away, and that is told once, by the tab: its
    /// <c>TabAdmissionRejected</c> on the stream and <c>table.tab-turned-away</c> in the History. A call to an empty
    /// table is not a waiter's response time, and nobody is left on the closed tab to hear it answered.
    /// </remarks>
    /// <returns>True when this call was open and is now answered.</returns>
    public bool AnswerForTurnedAwayTab(DateTime answeredAtUtc, Guid staffId) => Answer(answeredAtUtc, staffId);

    /// <summary>Records the answer - and the claim, when nobody had it - on an open call. False on one that is not.</summary>
    private bool Answer(DateTime acknowledgedAtUtc, Guid staffId)
    {
        if (!IsOpen)
        {
            return false;
        }

        AcknowledgedAtUtc = Guard.NotLocalTime(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        AcknowledgedByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        // Answered without anybody claiming it first: whoever answered had it.
        ClaimedAtUtc ??= AcknowledgedAtUtc;
        ClaimedByStaffId ??= AcknowledgedByStaffId;

        return true;
    }

    /// <summary>
    /// The same participant asked for the bill again: this request is no longer the one to bring.
    /// </summary>
    /// <remarks>
    /// Not an acknowledgement - nobody went - so <see cref="AcknowledgedAtUtc"/> stays empty and the
    /// record still says the table waited unanswered. A request a waiter already picked up stays
    /// picked up: superseding one that is not open is a no-op, as acknowledging twice is.
    /// </remarks>
    /// <returns>True when this request was open and is now superseded.</returns>
    /// <exception cref="DomainStateException">The request is not for the bill.</exception>
    public bool Supersede(DateTime supersededAtUtc)
    {
        if (Preset != ServiceRequestPreset.TheBill)
        {
            throw new DomainStateException(
                $"Only a request for the bill is replaced by asking again; this one is for {Preset}.");
        }

        if (!IsOpen)
        {
            return false;
        }

        SupersededAtUtc = Guard.NotLocalTime(supersededAtUtc, nameof(supersededAtUtc));

        return true;
    }
}
