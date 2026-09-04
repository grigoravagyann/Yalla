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
