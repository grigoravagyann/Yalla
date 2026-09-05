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
        string? note = null)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        RequestedByParticipantId = requestedByParticipantId;
        Preset = Guard.Defined(preset, nameof(preset));
        Note = Guard.OptionalText(note, nameof(note), FieldLengths.ServiceNote);
        StampCreatedAt(createdAtUtc);
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
