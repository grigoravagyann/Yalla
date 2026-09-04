using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One attempt to settle some part of a tab.
/// </summary>
/// <remarks>
/// <para>
/// Schema only at this stage: no provider is called from here, and Armenian fiscal-receipt
/// handling is a separate unresolved question that will shape its own module.
/// </para>
/// <para>
/// <see cref="PaymentStatus.Reserved"/> is the important state. The amount is held against
/// <c>Tab.RemainingAmd</c> under the tab's row version <i>before</i> anything is sent to Idram or
/// Telcell, so two people paying at once cannot both claim the same remaining dram and leave the
/// venue to refund one of them.
/// </para>
/// </remarks>
public sealed class Payment : Entity
{
    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>Who paid. Null for a cash payment the waiter recorded against the table.</summary>
    public Guid? TabParticipantId { get; private set; }

    public TabParticipant? TabParticipant { get; private set; }

    /// <summary>Amount in whole Armenian dram.</summary>
    public long AmountAmd { get; private set; }

    public PaymentMethod Method { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>The provider's own identifier for this transaction, for reconciliation.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Set when the attempt reached a terminal state.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    private Payment()
    {
    }

    public Payment(
        Guid tabId,
        long amountAmd,
        PaymentMethod method,
        PaymentStatus status,
        DateTime createdAtUtc,
        Guid? tabParticipantId = null,
        string? providerReference = null,
        DateTime? completedAtUtc = null)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        Method = Guard.Defined(method, nameof(method));
        Status = Guard.Defined(status, nameof(status));
        TabParticipantId = tabParticipantId;
        ProviderReference = Guard.OptionalText(providerReference, nameof(providerReference), FieldLengths.ProviderReference);
        StampCreatedAt(createdAtUtc);

        if (amountAmd <= 0L)
        {
            throw new ArgumentOutOfRangeException(nameof(amountAmd), amountAmd, "A payment must be for a positive amount.");
        }

        AmountAmd = amountAmd;

        var isTerminal = status is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Released;
        if (isTerminal && completedAtUtc is null)
        {
            throw new ArgumentException(
                "A payment in a terminal state must record when it completed.", nameof(completedAtUtc));
        }

        if (!isTerminal && completedAtUtc is not null)
        {
            throw new ArgumentException(
                "Only a payment in a terminal state has a completion time.", nameof(completedAtUtc));
        }

        CompletedAtUtc = completedAtUtc is null
            ? null
            : Guard.NotLocalTime(completedAtUtc.Value, nameof(completedAtUtc));
    }

    /// <summary>
    /// Holds <paramref name="amountAmd"/> against the tab before contacting a provider.
    /// </summary>
    public static Payment Reserve(
        Guid tabId,
        long amountAmd,
        PaymentMethod method,
        DateTime createdAtUtc,
        Guid? tabParticipantId = null) =>
        new(tabId, amountAmd, method, PaymentStatus.Reserved, createdAtUtc, tabParticipantId);
}
