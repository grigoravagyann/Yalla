namespace Yalla.Domain.Enums;

/// <summary>Lifecycle of a tab.</summary>
public enum TabStatus
{
    /// <summary>Accepting orders and payments.</summary>
    Open = 1,

    /// <summary>The bill has been asked for: settlement in progress, no new orders.</summary>
    Closing = 2,

    /// <summary>Fully settled.</summary>
    Closed = 3,

    /// <summary>Given up on without full settlement; kept for reporting rather than deleted.</summary>
    Abandoned = 4,
}

/// <summary>How the people on a tab agreed to split the bill.</summary>
public enum SettlementMode
{
    HostPaysEverything = 1,
    EveryonePaysOwnItems = 2,
    AnyonePaysAnyAmount = 3,
}

/// <summary>Whether a participant opened the tab or joined it.</summary>
public enum ParticipantRole
{
    Host = 1,
    Guest = 2,
}

/// <summary>Whether a participant is allowed on the tab.</summary>
public enum ParticipantStatus
{
    PendingApproval = 1,
    Approved = 2,
    Removed = 3,
}

/// <summary>Kitchen-facing progress of one order placed against a tab.</summary>
public enum TabOrderStatus
{
    New = 1,
    InKitchen = 2,
    Ready = 3,
    Served = 4,
    Voided = 5,
}

/// <summary>Payment instrument. Idram and Telcell are the local Armenian wallets.</summary>
public enum PaymentMethod
{
    Idram = 1,
    Telcell = 2,
    Card = 3,
    Cash = 4,
}

/// <summary>Lifecycle of a payment attempt.</summary>
public enum PaymentStatus
{
    /// <summary>
    /// The amount has been atomically held against the tab's remaining balance before anything
    /// was sent to a provider. Two people cannot reserve the same remaining dram.
    /// </summary>
    Reserved = 1,

    Succeeded = 2,
    Failed = 3,

    /// <summary>The hold was given back to the tab's remaining balance without being charged.</summary>
    Released = 4,
}

/// <summary>Why money is coming off a bill.</summary>
/// <remarks>
/// Two words a manager uses differently, so they are two values rather than one "reduction".
/// A <see cref="Discount"/> is commercial - a regular, a voucher, a slow Tuesday. A
/// <see cref="Comp"/> is an apology: the dish was wrong, the wait was long. Reporting that cannot
/// tell them apart cannot answer the only question worth asking, which is how much the venue is
/// giving away because something went wrong.
/// </remarks>
public enum AdjustmentKind
{
    Discount = 1,
    Comp = 2,
}

/// <summary>What a table is asking for. Presets only - see <c>docs/tabs.md</c>.</summary>
/// <remarks>
/// Deliberately not free text with a preset alongside. A text box creates the expectation of a
/// reply, and during the Friday rush nobody answers it; a fixed list is a signal a waiter can act
/// on at a glance from across the room.
/// </remarks>
public enum ServiceRequestPreset
{
    Napkins = 1,
    Water = 2,
    TheBill = 3,
    Other = 4,
}

/// <summary>
/// What happened on a tab, for the catch-up stream.
/// </summary>
/// <remarks>
/// Numbers are persisted and pinned, like every other enum here - see <c>SCHEMA.md</c>. A client
/// that does not recognise a type must ignore it and keep its sequence position rather than
/// failing: new types will be added, and an old app build must not break on a tab that used one.
/// </remarks>
public enum TabEventType
{
    TabOpened = 1,
    ParticipantJoined = 2,
    ParticipantApproved = 3,
    ParticipantRejected = 4,
    ParticipantRemoved = 5,
    ParticipantPermissionsChanged = 6,
    ParticipantRenamed = 7,
    HostReassigned = 8,
    SettlementModeChanged = 9,
    OrderPlaced = 10,
    OrderStatusChanged = 11,
    LineVoided = 12,
    AdjustmentAdded = 13,
    AdjustmentVoided = 14,
    PaymentRecorded = 15,
    ServiceRequested = 16,
    ServiceRequestAcknowledged = 17,
    TabClosing = 18,
    TabClosed = 19,
    TabAbandoned = 20,
}
