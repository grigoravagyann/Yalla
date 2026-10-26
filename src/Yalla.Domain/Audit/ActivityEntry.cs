using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Audit;

/// <summary>
/// One thing somebody did at a venue: who, from which device, what, to what, and - for money - how much. The console's
/// History page, the bill timeline and a person's recent work all read these. See <c>docs/activity.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written in the same unit of work as the change it records</b>, through <c>IActivityLog</c>: a change cannot land
/// without its entry, and an entry cannot survive a change that rolled back.
/// </para>
/// <para>
/// <b>Names are copied, not joined.</b> <see cref="ActorName"/>, <see cref="DeviceLabel"/>, <see cref="TableLabel"/> and
/// <see cref="GuestName"/> are what the people and things were called when it happened - a history of last month reads
/// with last month's names, and nothing it names can be deleted out from under it. No foreign keys, like
/// <see cref="PlatformAuditLog"/>: the log outlives what it names.
/// </para>
/// <para>
/// <b>Separate from <see cref="PlatformAuditLog"/></b>, which stays the platform tier's record (read by Yalla, and by
/// the password-reset rules). The venue-facing actions that also write one there - a sign-in issued, a QR code
/// regenerated - write an entry here as well, for the venue's own History.
/// </para>
/// <para>
/// <b>Kept a year, money three.</b> The daily purge deletes entries older than
/// <see cref="RetentionDays"/> days, except those with <see cref="IsMoney"/> set, which go after
/// <see cref="MoneyRetentionDays"/>.
/// </para>
/// </remarks>
public sealed class ActivityEntry : Entity
{
    /// <summary>How long an ordinary entry is kept.</summary>
    public const int RetentionDays = 365;

    /// <summary>How long an entry about money - a payment, a discount, a comp, a void, a write-off - is kept.</summary>
    public const int MoneyRetentionDays = 3 * 365;

    /// <summary>The longest summary kept, as JSON. A summary is a handful of parameters, never a document.</summary>
    public const int MaxSummaryLength = 4000;

    /// <summary>The longest before or after snapshot kept, as JSON.</summary>
    public const int MaxSnapshotLength = 4000;

    public Guid VenueId { get; private set; }

    /// <summary>The branch it happened at; null for something venue-wide - an owner's account, say.</summary>
    public Guid? BranchId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public ActivityActorKind ActorKind { get; private set; }

    /// <summary>The staff member who acted; null unless <see cref="ActorKind"/> is staff or a platform admin.</summary>
    public Guid? StaffMemberId { get; private set; }

    /// <summary>What the actor was called then: a staff member's name, the guest's. Null for the system.</summary>
    public string? ActorName { get; private set; }

    /// <summary>The tablet or phone the request came from; null from the console, and for the guest and the system.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>
    /// What that device was called then - "Hall tablet", "Aram's phone" - or <c>Console</c> for staff signed in to the
    /// console. English for a phone and the console: kept for the CSV export and for entries written before
    /// <see cref="DeviceKind"/>; a client words those two from <see cref="DeviceKind"/> and <see cref="DeviceOwnerName"/>.
    /// </summary>
    public string? DeviceLabel { get; private set; }

    /// <summary>
    /// Whether <see cref="DeviceId"/> was a tablet or a phone then. Null from the console, for the guest and the system,
    /// for a device that was not found, and on entries written before 2026-10-26 (their <see cref="DeviceLabel"/> is all
    /// there is).
    /// </summary>
    public StaffDeviceKind? DeviceKind { get; private set; }

    /// <summary>A phone's person then, by full name: who "Aram's phone" is. Null for anything but a phone.</summary>
    public string? DeviceOwnerName { get; private set; }

    public ActivityCategory Category { get; private set; }

    /// <summary>A stable code, <c>category.verb</c>: <c>booking.approved</c>, <c>payment.cash</c>. See <c>docs/activity.md</c>.</summary>
    public string Action { get; private set; } = null!;

    /// <summary>What it was done to: <c>reservation</c>, <c>tab</c>, <c>takeaway</c>, <c>menu-item</c>, <c>staff</c>...</summary>
    public string SubjectType { get; private set; } = null!;

    public Guid SubjectId { get; private set; }

    /// <summary>
    /// The sitting it belongs to, when it belongs to one: a seat, an order, a void, a payment, a transfer. A bill's
    /// timeline is every entry of its sitting - the seat came before the tab existed, so the tab alone would miss it.
    /// </summary>
    public Guid? TableSessionId { get; private set; }

    /// <summary>The subject's own short code when it has one: a takeaway order's <c>T-482</c>, a booking's code.</summary>
    public string? SubjectCode { get; private set; }

    public string? TableLabel { get; private set; }

    public string? GuestName { get; private set; }

    /// <summary>The dishes it names, comma-joined - what a search for an item name matches.</summary>
    public string? ItemNames { get; private set; }

    /// <summary>The amount the entry is about, in whole dram, when it is about one.</summary>
    public long? AmountAmd { get; private set; }

    /// <summary>About money - kept three years rather than one.</summary>
    public bool IsMoney { get; private set; }

    public string? BeforeJson { get; private set; }

    public string? AfterJson { get; private set; }

    /// <summary>Why, in the words somebody typed: a void's reason, a discount's.</summary>
    public string? Reason { get; private set; }

    /// <summary>The entry's parameters as JSON, for the client to render in its own language. Never prose.</summary>
    public string SummaryJson { get; private set; } = null!;

    private ActivityEntry()
    {
    }

    public ActivityEntry(
        Guid venueId,
        Guid? branchId,
        DateTime occurredAtUtc,
        ActivityActorKind actorKind,
        Guid? staffMemberId,
        string? actorName,
        Guid? deviceId,
        string? deviceLabel,
        ActivityCategory category,
        string action,
        string subjectType,
        Guid subjectId,
        string summaryJson,
        string? subjectCode = null,
        string? tableLabel = null,
        string? guestName = null,
        string? itemNames = null,
        long? amountAmd = null,
        bool isMoney = false,
        string? beforeJson = null,
        string? afterJson = null,
        string? reason = null,
        Guid? tableSessionId = null,
        StaffDeviceKind? deviceKind = null,
        string? deviceOwnerName = null)
        : base(Guid.CreateVersion7())
    {
        VenueId = Guard.NotEmpty(venueId, nameof(venueId));
        BranchId = branchId == Guid.Empty ? null : branchId;
        OccurredAtUtc = Guard.NotLocalTime(occurredAtUtc, nameof(occurredAtUtc));
        ActorKind = Guard.Defined(actorKind, nameof(actorKind));
        Category = Guard.Defined(category, nameof(category));
        Action = Guard.NotBlank(action, nameof(action), FieldLengths.ActivityAction);
        SubjectType = Guard.NotBlank(subjectType, nameof(subjectType), FieldLengths.ActivitySubjectType);
        SubjectId = Guard.NotEmpty(subjectId, nameof(subjectId));
        TableSessionId = tableSessionId == Guid.Empty ? null : tableSessionId;

        if (actorKind is ActivityActorKind.Staff or ActivityActorKind.PlatformAdmin && staffMemberId is null)
        {
            throw new ArgumentException("An entry made by staff names them.", nameof(staffMemberId));
        }

        StaffMemberId = actorKind is ActivityActorKind.Staff or ActivityActorKind.PlatformAdmin ? staffMemberId : null;

        // Labels are clipped, never refused: the change this records has already passed its own guards, and losing
        // the entry over the length of a name would lose the record of something that did happen.
        ActorName = Clip(actorName, FieldLengths.PersonName);
        DeviceId = deviceId == Guid.Empty ? null : deviceId;
        DeviceLabel = Clip(deviceLabel, FieldLengths.DeviceName);
        DeviceKind = DeviceId is not null && deviceKind is { } kind && Enum.IsDefined(kind) ? kind : null;
        DeviceOwnerName = DeviceKind == StaffDeviceKind.Phone ? Clip(deviceOwnerName, FieldLengths.PersonName) : null;
        SubjectCode = Clip(subjectCode, FieldLengths.ActivitySubjectCode);
        TableLabel = Clip(tableLabel, FieldLengths.TableLabel);
        GuestName = Clip(guestName, FieldLengths.PersonName);
        ItemNames = Clip(itemNames, FieldLengths.ActivityItemNames);
        AmountAmd = amountAmd;
        IsMoney = isMoney;
        BeforeJson = ClipJson(beforeJson, MaxSnapshotLength);
        AfterJson = ClipJson(afterJson, MaxSnapshotLength);
        Reason = Clip(reason, FieldLengths.Reason);
        SummaryJson = ClipJson(summaryJson, MaxSummaryLength) ?? "{}";
        StampCreatedAt(OccurredAtUtc);
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>
    /// JSON kept whole or not at all: a snapshot cut mid-token would not parse, so one too long for its column is
    /// replaced by an object saying so.
    /// </summary>
    private static string? ClipJson(string? json, int max) =>
        string.IsNullOrWhiteSpace(json) ? null : json.Length <= max ? json : """{"truncated":true}""";
}
