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
/// </remarks>
public sealed class ServiceRequest : Entity
{
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

    public bool IsOpen => AcknowledgedAtUtc is null;

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
    /// </remarks>
    public bool Acknowledge(DateTime acknowledgedAtUtc, Guid staffId)
    {
        if (!IsOpen)
        {
            return false;
        }

        AcknowledgedAtUtc = Guard.NotLocalTime(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        AcknowledgedByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        return true;
    }
}
