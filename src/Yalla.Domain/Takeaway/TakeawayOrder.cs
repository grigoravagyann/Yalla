using Yalla.Domain.Common;

namespace Yalla.Domain.Takeaway;

/// <summary>
/// A takeaway order: dishes a guest ordered from the app to pick up later, with no table and nothing paid in the app.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status moves one way</b> - <c>Sent → Accepted → Ready → Collected</c> - through the methods here, each of which
/// refuses a move the order is not in the state for (<see cref="TakeawayStatusConflictException"/>, 409). The three ends
/// off the way are <see cref="TakeawayStatus.Declined"/>, <see cref="TakeawayStatus.Cancelled"/> and
/// <see cref="TakeawayStatus.NoShow"/>. Every move stamps when it happened, so the guest's status screen shows a time
/// per step without an event stream.
/// </para>
/// <para>
/// <b>Not a tab.</b> A tab is a seated party's bill; this is one order for one person who pays at the counter. Its
/// lines keep the name and price the dish had when it was ordered, as every order line does. The payment is the
/// venue's own: staff record the method and the amount, which is the total.
/// </para>
/// <para>
/// <b>The code</b> - "T-482" - is what the guest says at the counter. Three digits, unique per branch per local day
/// (<see cref="LocalDate"/>), by a unique index; nothing is authorised by quoting it.
/// </para>
/// </remarks>
public sealed class TakeawayOrder : Entity
{
    private readonly List<TakeawayOrderLine> _lines = [];

    public Guid BranchId { get; private set; }

    /// <summary>Who ordered. Null only once the account was deleted: the order stays the venue's record.</summary>
    public Guid? DinerUserId { get; private set; }

    /// <summary>"T-482": unique per branch per <see cref="LocalDate"/>.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>The branch's service day the order was placed on - the code's day.</summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>The name the venue calls out - the account's display name when it was ordered.</summary>
    public string GuestName { get; private set; } = null!;

    /// <summary>The proved number the venue may call, as it was when ordered.</summary>
    public string GuestPhone { get; private set; } = null!;

    /// <summary>"As soon as possible" rather than a chosen slot.</summary>
    public bool PickupAsap { get; private set; }

    /// <summary>When the guest wants it: now plus the preparation time for ASAP, else the slot they picked.</summary>
    public DateTime RequestedPickupUtc { get; private set; }

    /// <summary>The same, on the branch's wall clock.</summary>
    public TimeOnly RequestedPickupLocalTime { get; private set; }

    /// <summary>For the venue: "no onion, separate bag". Null when none.</summary>
    public string? Note { get; private set; }

    public TakeawayStatus Status { get; private set; }

    /// <summary>Whether the venue's tablets must answer: the venue answers within this, or the sweep declines. Null once answered, and when auto-accepted.</summary>
    public DateTime? AcceptDeadlineUtc { get; private set; }

    /// <summary>When the venue promised it: the time staff accepted it for (or the requested time, auto-accepted).</summary>
    public DateTime? ReadyAtUtc { get; private set; }

    public DateTime? AcceptedAtUtc { get; private set; }

    /// <summary>Who accepted it. Null when the branch accepts automatically.</summary>
    public Guid? AcceptedByStaffId { get; private set; }

    /// <summary>When the kitchen marked it ready.</summary>
    public DateTime? MarkedReadyAtUtc { get; private set; }

    public Guid? MarkedReadyByStaffId { get; private set; }

    public DateTime? CollectedAtUtc { get; private set; }

    public Guid? CollectedByStaffId { get; private set; }

    /// <summary>How it was paid at the counter. Set with <see cref="CollectedAtUtc"/>.</summary>
    public TakeawayPaymentMethod? PaymentMethod { get; private set; }

    /// <summary>What was taken at the counter: the total. Set with <see cref="CollectedAtUtc"/>.</summary>
    public long? PaidAmountAmd { get; private set; }

    public DateTime? DeclinedAtUtc { get; private set; }

    public Guid? DeclinedByStaffId { get; private set; }

    public TakeawayDeclineCause? DeclinedBecause { get; private set; }

    /// <summary>What staff told the guest, when they said why. Null for a decline without words or nobody's answer.</summary>
    public string? DeclineReason { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public DateTime? NoShowAtUtc { get; private set; }

    public Guid? NoShowByStaffId { get; private set; }

    /// <summary>The app's idempotency key: a retried send answers the order it made.</summary>
    public Guid ClientCommandId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<TakeawayOrderLine> Lines => _lines;

    public long TotalAmd => _lines.Sum(l => l.LineTotalAmd);

    /// <summary>From when "no-show" may be marked; null until it has a ready time.</summary>
    public DateTime? NoShowFromUtc =>
        ReadyAtUtc is { } readyAt ? TakeawayRules.NoShowFromUtc(readyAt, MarkedReadyAtUtc) : null;

    private TakeawayOrder()
    {
    }

    /// <summary>A new order, <see cref="TakeawayStatus.Sent"/>, with five minutes for the venue to answer.</summary>
    /// <param name="branchId">Where.</param>
    /// <param name="dinerUserId">Who.</param>
    /// <param name="code">"T-482", unused at the branch on <paramref name="localDate"/>.</param>
    /// <param name="localDate">The branch's service day.</param>
    /// <param name="guestName">The name to call out.</param>
    /// <param name="guestPhone">The proved number.</param>
    /// <param name="pickupAsap">As soon as possible, rather than a slot.</param>
    /// <param name="requestedPickupUtc">When they want it.</param>
    /// <param name="requestedPickupLocalTime">The same, on the branch's wall clock.</param>
    /// <param name="note">For the venue.</param>
    /// <param name="lines">The dishes with their current names and prices.</param>
    /// <param name="clientCommandId">The app's idempotency key.</param>
    /// <param name="nowUtc">Now.</param>
    public TakeawayOrder(
        Guid branchId,
        Guid dinerUserId,
        string code,
        DateOnly localDate,
        string guestName,
        string guestPhone,
        bool pickupAsap,
        DateTime requestedPickupUtc,
        TimeOnly requestedPickupLocalTime,
        string? note,
        IReadOnlyList<TakeawayLineInput> lines,
        Guid clientCommandId,
        DateTime nowUtc)
        : base(Guid.CreateVersion7())
    {
        ArgumentNullException.ThrowIfNull(lines);

        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));

        if (string.IsNullOrEmpty(code) || code.Length != TakeawayRules.CodeLength || !code.StartsWith(TakeawayRules.CodePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A takeaway code is \"T-\" and three digits.", nameof(code));
        }

        Code = code;
        LocalDate = localDate;
        GuestName = Guard.NotBlank(guestName, nameof(guestName), FieldLengths.PersonName);
        GuestPhone = Guard.NotBlank(guestPhone, nameof(guestPhone), FieldLengths.PhoneE164);
        PickupAsap = pickupAsap;
        RequestedPickupUtc = Guard.NotLocalTime(requestedPickupUtc, nameof(requestedPickupUtc));
        RequestedPickupLocalTime = requestedPickupLocalTime;
        Note = Guard.OptionalText(note, nameof(note), TakeawayRules.MaxNoteLength);
        ClientCommandId = Guard.NotEmpty(clientCommandId, nameof(clientCommandId));

        if (lines.Count is 0 or > TakeawayRules.MaxLines)
        {
            throw new ArgumentException($"A takeaway order has 1 to {TakeawayRules.MaxLines} lines.", nameof(lines));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            _lines.Add(new TakeawayOrderLine(Id, i, line.MenuItemId, line.Name, line.UnitPriceAmd, line.Quantity));
        }

        Status = TakeawayStatus.Sent;
        StampCreatedAt(nowUtc);
        AcceptDeadlineUtc = TakeawayRules.AcceptDeadlineUtc(nowUtc);
    }

    /// <summary>
    /// The venue accepts it, ready at <paramref name="readyAtUtc"/>: it goes to the kitchen. A null
    /// <paramref name="staffId"/> is the branch accepting automatically.
    /// </summary>
    /// <returns>False when it was already accepted - two tablets, one answer.</returns>
    public bool Accept(DateTime readyAtUtc, Guid? staffId, DateTime nowUtc)
    {
        if (Status == TakeawayStatus.Accepted)
        {
            return false;
        }

        Require(TakeawayStatus.Sent, "Only an order still waiting for an answer can be accepted.");

        if (!TakeawayRules.ReadyAtIsAcceptable(Guard.NotLocalTime(readyAtUtc, nameof(readyAtUtc)), nowUtc))
        {
            throw new FieldValidationException(
            [
                new FieldViolation(
                    TakeawayRules.Fields.ReadyAt,
                    $"The ready time is from now to {TakeawayRules.MaxReadyAheadHours} hours ahead.",
                    FieldBounds.Range,
                    Min: nowUtc,
                    Max: nowUtc.AddHours(TakeawayRules.MaxReadyAheadHours),
                    Value: readyAtUtc),
            ]);
        }

        Status = TakeawayStatus.Accepted;
        ReadyAtUtc = readyAtUtc;
        AcceptedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        AcceptedByStaffId = staffId is { } id ? Guard.NotEmpty(id, nameof(staffId)) : null;
        AcceptDeadlineUtc = null;

        return true;
    }

    /// <summary>The venue says no, with or without a reason the guest is shown.</summary>
    /// <returns>False when it was already declined.</returns>
    public bool Decline(string? reason, Guid staffId, DateTime nowUtc)
    {
        if (Status == TakeawayStatus.Declined)
        {
            return false;
        }

        Require(TakeawayStatus.Sent, "Only an order still waiting for an answer can be declined.");

        Status = TakeawayStatus.Declined;
        DeclinedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        DeclinedByStaffId = Guard.NotEmpty(staffId, nameof(staffId));
        DeclinedBecause = TakeawayDeclineCause.StaffDeclined;
        DeclineReason = Guard.OptionalText(reason, nameof(reason), TakeawayRules.MaxDeclineReasonLength);
        AcceptDeadlineUtc = null;

        return true;
    }

    /// <summary>Nobody at the venue answered within the five minutes: declined for them.</summary>
    /// <returns>False when it is no longer waiting, or its deadline has not passed - the sweep leaves it alone.</returns>
    public bool DeclineForNoAnswer(DateTime nowUtc)
    {
        if (Status != TakeawayStatus.Sent || AcceptDeadlineUtc is not { } deadline || nowUtc < deadline)
        {
            return false;
        }

        Status = TakeawayStatus.Declined;
        DeclinedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        DeclinedBecause = TakeawayDeclineCause.NoAnswer;
        AcceptDeadlineUtc = null;

        return true;
    }

    /// <summary>The guest cancels - only while the venue has not answered. After that it is a phone call.</summary>
    /// <returns>False when it was already cancelled.</returns>
    public bool CancelByGuest(DateTime nowUtc)
    {
        if (Status == TakeawayStatus.Cancelled)
        {
            return false;
        }

        Require(TakeawayStatus.Sent, "An order the venue has answered can no longer be cancelled in the app. Call the venue.");

        Status = TakeawayStatus.Cancelled;
        CancelledAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        AcceptDeadlineUtc = null;

        return true;
    }

    /// <summary>The kitchen finished it.</summary>
    /// <returns>False when it was already ready.</returns>
    public bool MarkReady(Guid staffId, DateTime nowUtc)
    {
        if (Status == TakeawayStatus.Ready)
        {
            return false;
        }

        Require(TakeawayStatus.Accepted, "Only an accepted order can be marked ready.");

        Status = TakeawayStatus.Ready;
        MarkedReadyAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        MarkedReadyByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        return true;
    }

    /// <summary>Handed over and paid at the counter, the whole total, by <paramref name="method"/>.</summary>
    /// <returns>False when it was already collected.</returns>
    public bool Collect(TakeawayPaymentMethod method, Guid staffId, DateTime nowUtc)
    {
        if (Status == TakeawayStatus.Collected)
        {
            return false;
        }

        Require(TakeawayStatus.Ready, "Only a ready order can be handed over.");

        Status = TakeawayStatus.Collected;
        PaymentMethod = Guard.Defined(method, nameof(method));
        PaidAmountAmd = TotalAmd;
        CollectedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        CollectedByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        return true;
    }

    /// <summary>Ready, and nobody came for it - from thirty minutes after it was ready.</summary>
    /// <returns>False when it was already marked.</returns>
    /// <exception cref="TakeawayNoShowTooEarlyException">Less than thirty minutes after it was ready.</exception>
    public bool MarkNoShow(Guid staffId, DateTime nowUtc)
    {
        if (Status == TakeawayStatus.NoShow)
        {
            return false;
        }

        Require(TakeawayStatus.Ready, "Only a ready order nobody came for can be marked a no-show.");

        var from = NoShowFromUtc!.Value;

        if (nowUtc < from)
        {
            throw new TakeawayNoShowTooEarlyException(Id, from);
        }

        Status = TakeawayStatus.NoShow;
        NoShowAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        NoShowByStaffId = Guard.NotEmpty(staffId, nameof(staffId));

        return true;
    }

    /// <summary>The account was deleted: the order stays the venue's record, without the link to the person.</summary>
    public void DetachFromAccount() => DinerUserId = null;

    private void Require(TakeawayStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new TakeawayStatusConflictException(Id, Status, message);
        }
    }
}

/// <summary>One dish on a takeaway order, with the name and price it was ordered at.</summary>
public sealed class TakeawayOrderLine : Entity
{
    public Guid TakeawayOrderId { get; private set; }

    /// <summary>Display order, from 0, as the guest listed them.</summary>
    public int Position { get; private set; }

    public Guid MenuItemId { get; private set; }

    public string NameSnapshot { get; private set; } = null!;

    public long UnitPriceAmdSnapshot { get; private set; }

    public int Quantity { get; private set; }

    public long LineTotalAmd => UnitPriceAmdSnapshot * Quantity;

    private TakeawayOrderLine()
    {
    }

    internal TakeawayOrderLine(Guid orderId, int position, Guid menuItemId, string name, long unitPriceAmd, int quantity)
        : base(Guid.CreateVersion7())
    {
        TakeawayOrderId = Guard.NotEmpty(orderId, nameof(orderId));
        Position = Guard.NotNegative(position, nameof(position));
        MenuItemId = Guard.NotEmpty(menuItemId, nameof(menuItemId));
        NameSnapshot = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        UnitPriceAmdSnapshot = Guard.NotNegativeAmd(unitPriceAmd, nameof(unitPriceAmd));

        if (quantity is < TakeawayRules.MinQuantity or > TakeawayRules.MaxQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A takeaway line is 1 to 20 of a dish.");
        }

        Quantity = quantity;
    }
}

/// <summary>A dish as ordered, with its current name and price.</summary>
public sealed record TakeawayLineInput(Guid MenuItemId, string Name, long UnitPriceAmd, int Quantity);
