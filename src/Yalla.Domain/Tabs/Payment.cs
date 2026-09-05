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

    /// <summary>
    /// A tip, in whole dram, handed over with this payment.
    /// </summary>
    /// <remarks>
    /// <b>Outside the balance.</b> A tip is not part of what the table owes: it is excluded from
    /// <c>Tab.PaidAmd</c> and <c>Tab.RemainingAmd</c> entirely. Folding it in makes a 10,000 AMD
    /// bill settled with 12,000 AMD look overpaid by 2,000, and every subsequent number - the
    /// remaining balance, each person's share, whether the tab may close - becomes arithmetic
    /// nobody at the table or in the office can follow. It is recorded here because the venue has
    /// to reconcile the cash drawer against it, not because the bill knows about it.
    /// </remarks>
    public long TipAmd { get; private set; }

    /// <summary>The provider's own identifier for this transaction, for reconciliation.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Set when the attempt reached a terminal state.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>
    /// The caller's own id for the command that took this money.
    /// </summary>
    /// <remarks>
    /// Unique across the table. A waiter tapping "take 5,000 in cash" twice on a tablet that showed
    /// no response must not take 10,000 off the balance, and the index - not a check in the service -
    /// is what settles it, because two taps can race and a check-then-insert lets both through.
    /// </remarks>
    public Guid ClientCommandId { get; private set; }

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
        DateTime? completedAtUtc = null,
        long tipAmd = 0L,
        Guid? clientCommandId = null)
        : base(Guid.CreateVersion7())
    {
        TipAmd = Guard.NotNegativeAmd(tipAmd, nameof(tipAmd));

        // A caller with no command id - a fixture, a seeder - gets a synthetic one so the unique
        // index still holds. A caller that has one must not pass Guid.Empty and mean it.
        ClientCommandId = clientCommandId is { } given
            ? Guard.NotEmpty(given, nameof(clientCommandId))
            : Guid.CreateVersion7();
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
        Guid? tabParticipantId = null,
        long tipAmd = 0L,
        Guid? clientCommandId = null) =>
        new(tabId, amountAmd, method, PaymentStatus.Reserved, createdAtUtc, tabParticipantId,
            tipAmd: tipAmd, clientCommandId: clientCommandId);

    /// <summary>
    /// The reserved hold actually went through.
    /// </summary>
    /// <remarks>
    /// For cash this happens in the same call as the reserve, because the money is already on the
    /// table. The two steps exist so that Idram and Telcell can sit in <see cref="PaymentStatus.Reserved"/>
    /// while the provider is called - the hold against the tab is taken before anything leaves the
    /// building, so two people paying at once cannot both claim the same remaining dram.
    /// </remarks>
    public void MarkSucceeded(DateTime completedAtUtc, string? providerReference = null)
    {
        if (Status != PaymentStatus.Reserved)
        {
            throw new DomainStateException($"Only a reserved payment can succeed; this one is {Status}.");
        }

        Status = PaymentStatus.Succeeded;
        CompletedAtUtc = Guard.NotLocalTime(completedAtUtc, nameof(completedAtUtc));

        if (providerReference is not null)
        {
            ProviderReference = Guard.OptionalText(
                providerReference, nameof(providerReference), FieldLengths.ProviderReference);
        }
    }
}
