using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// Dishes a guest chose ahead of their booking, for the kitchen to have ready when they arrive. One per booking.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is stored is only what people did</b>: the lines and the note the guest saved, and whether a waiter sent it
/// to the kitchen or marked the party not coming. Whether it can still be edited, and whether it is cancelled, are
/// <i>read</i> - <see cref="PreOrderRules.StateOf"/> - from the booking's status and the clock. A booking is cancelled
/// in five places and a no-show withdrawn in a sixth; a stored "cancelled" would have to be remembered by all of them,
/// and undoing a no-show would have to remember to revive it. Derived, it cannot drift.
/// </para>
/// <para>
/// <b>Its id is also its kitchen ticket's id.</b> Sent before the party sits, the pre-order <i>is</i> the ticket (a held
/// ticket: <see cref="KitchenStatus"/> moves along the rail with no tab under it, because a tab needs a seated party).
/// When the party is seated on the booking it becomes a <c>TabOrder</c> with the same id on their tab
/// (<see cref="TabOrderId"/>), so the kitchen screen's ticket does not change identity under the cook.
/// </para>
/// </remarks>
public sealed class PreOrder : Entity
{
    private readonly List<PreOrderLine> _lines = [];

    public Guid ReservationId { get; private set; }

    public Reservation Reservation { get; private set; } = null!;

    /// <summary>The booking's branch, denormalised for the kitchen's list of held tickets.</summary>
    public Guid BranchId { get; private set; }

    /// <summary>A booking-level note: "salad first", "it's a birthday". Null when none.</summary>
    public string? Note { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? SentAtUtc { get; private set; }

    public Guid? SentByStaffId { get; private set; }

    /// <summary>
    /// Where the held ticket is on the kitchen rail, from the send until it becomes an order on the party's tab. Null
    /// before it is sent; after that the tab order's own status is the truth and this is left as it was.
    /// </summary>
    public TabOrderStatus? KitchenStatus { get; private set; }

    /// <summary>The order on the party's tab it became - its own id. Null while held, or never sent.</summary>
    public Guid? TabOrderId { get; private set; }

    public DateTime? AttachedAtUtc { get; private set; }

    /// <summary>A waiter marked the party "not coming / no answer". Sending stays possible until the booking ends.</summary>
    public DateTime? NotComingAtUtc { get; private set; }

    public Guid? NotComingByStaffId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<PreOrderLine> Lines => _lines;

    public bool IsSent => SentAtUtc is not null;

    /// <summary>Sent before the party sat, and not yet on their tab.</summary>
    public bool IsHeld => IsSent && TabOrderId is null;

    public long TotalAmd => _lines.Sum(l => l.LineTotalAmd);

    private PreOrder()
    {
    }

    public PreOrder(Guid reservationId, Guid branchId, DateTime createdAtUtc)
        : base(Guid.CreateVersion7())
    {
        ReservationId = Guard.NotEmpty(reservationId, nameof(reservationId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        StampCreatedAt(createdAtUtc);
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Replaces the lines and the note with what the guest saved, keeping each dish's locked price.
    /// </summary>
    /// <remarks>
    /// A dish keeps the price it had when it <b>first</b> entered the pre-order: changing its quantity or note, or
    /// adding another dish, does not reprice it. A dish new to the pre-order is priced now. Matched by menu item, so
    /// the same dish twice (two notes) carries the same locked price on both lines. The caller has already checked the
    /// guest may edit, the dishes and the bounds.
    /// </remarks>
    public void Replace(IReadOnlyList<PreOrderLineInput> lines, string? note, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (IsSent)
        {
            throw new PreOrderAlreadySentException(ReservationId, SentAtUtc!.Value);
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("A pre-order has at least one line; clearing it removes it.", nameof(lines));
        }

        var locked = _lines
            .GroupBy(l => l.MenuItemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Position).First().UnitPriceAmdSnapshot);

        _lines.Clear();

        for (var i = 0; i < lines.Count; i++)
        {
            var input = lines[i];
            var price = locked.TryGetValue(input.MenuItemId, out var kept) ? kept : input.CurrentPriceAmd;

            _lines.Add(new PreOrderLine(Id, i, input.MenuItemId, input.Name, price, input.Quantity, input.Note));
        }

        Note = Guard.OptionalText(note, nameof(note), PreOrderRules.MaxNoteLength);
        UpdatedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
    }

    /// <summary>"Coming → send to kitchen": a ticket at once, at <c>InKitchen</c>, as a waiter's own order is.</summary>
    public void MarkSent(Guid staffId, DateTime nowUtc)
    {
        if (IsSent)
        {
            throw new PreOrderAlreadySentException(ReservationId, SentAtUtc!.Value);
        }

        SentAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        SentByStaffId = Guard.NotEmpty(staffId, nameof(staffId));
        KitchenStatus = TabOrderStatus.InKitchen;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>"Not coming / no answer": nothing is sent, and sending stays possible.</summary>
    public void MarkNotComing(Guid staffId, DateTime nowUtc)
    {
        if (IsSent)
        {
            throw new PreOrderAlreadySentException(ReservationId, SentAtUtc!.Value);
        }

        NotComingAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        NotComingByStaffId = Guard.NotEmpty(staffId, nameof(staffId));
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// The booking moved: a waiter's "not coming" was about the old time, so it goes. Refused once sent - the kitchen
    /// has it, and a move would leave the food at the old time.
    /// </summary>
    public void FollowMove(DateTime nowUtc)
    {
        if (IsSent)
        {
            throw new PreOrderAlreadySentException(ReservationId, SentAtUtc!.Value);
        }

        NotComingAtUtc = null;
        NotComingByStaffId = null;
        UpdatedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
    }

    /// <summary>The held ticket moves along the kitchen rail, by the <c>TabOrder</c> rail's own rules.</summary>
    public void MoveKitchenStatus(TabOrderStatus next, DateTime nowUtc)
    {
        if (!IsHeld)
        {
            throw new DomainStateException("This pre-order is not a held kitchen ticket.");
        }

        Guard.Defined(next, nameof(next));
        var from = KitchenStatus!.Value;

        var allowed = from switch
        {
            TabOrderStatus.New => next is TabOrderStatus.InKitchen or TabOrderStatus.Voided,
            TabOrderStatus.InKitchen => next is TabOrderStatus.Ready or TabOrderStatus.Voided,
            TabOrderStatus.Ready => next is TabOrderStatus.Served,
            _ => false,
        };

        if (!allowed)
        {
            throw new DomainStateException($"An order cannot go from {from} to {next}.");
        }

        KitchenStatus = next;
        UpdatedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
    }

    /// <summary>It is now the order with the same id on the party's tab.</summary>
    public void MarkAttached(DateTime nowUtc)
    {
        if (!IsHeld)
        {
            throw new DomainStateException("Only a sent pre-order not yet on a tab can be put on one.");
        }

        TabOrderId = Id;
        AttachedAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        UpdatedAtUtc = nowUtc;
    }
}

/// <summary>One dish on a pre-order, with the name and price it was ordered at.</summary>
public sealed class PreOrderLine : Entity
{
    public Guid PreOrderId { get; private set; }

    /// <summary>Display order, from 0, as the guest listed them.</summary>
    public int Position { get; private set; }

    public Guid MenuItemId { get; private set; }

    public string NameSnapshot { get; private set; } = null!;

    /// <summary>Locked when the dish first entered the pre-order - see <see cref="PreOrder.Replace"/>.</summary>
    public long UnitPriceAmdSnapshot { get; private set; }

    public int Quantity { get; private set; }

    public string? Note { get; private set; }

    public long LineTotalAmd => UnitPriceAmdSnapshot * Quantity;

    private PreOrderLine()
    {
    }

    internal PreOrderLine(
        Guid preOrderId, int position, Guid menuItemId, string name, long unitPriceAmd, int quantity, string? note)
        : base(Guid.CreateVersion7())
    {
        PreOrderId = Guard.NotEmpty(preOrderId, nameof(preOrderId));
        Position = Guard.NotNegative(position, nameof(position));
        MenuItemId = Guard.NotEmpty(menuItemId, nameof(menuItemId));
        NameSnapshot = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        UnitPriceAmdSnapshot = Guard.NotNegativeAmd(unitPriceAmd, nameof(unitPriceAmd));

        if (quantity is < PreOrderRules.MinQuantity or > PreOrderRules.MaxQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A pre-order line is 1 to 20 of a dish.");
        }

        Quantity = quantity;
        Note = Guard.OptionalText(note, nameof(note), PreOrderRules.MaxLineNoteLength);
    }
}

/// <summary>A line as saved, with the dish's current name and price for a dish new to the pre-order.</summary>
public sealed record PreOrderLineInput(Guid MenuItemId, string Name, long CurrentPriceAmd, int Quantity, string? Note);

/// <summary>
/// Where a pre-order stands, as every read says it. Persisted nowhere - derived by <see cref="PreOrderRules.StateOf"/> -
/// but pinned like every wire enum.
/// </summary>
public enum PreOrderState
{
    /// <summary>The guest may still change it: more than two hours before the start.</summary>
    Editable = 1,

    /// <summary>Two hours before the start or later, not sent: the venue's to send.</summary>
    Locked = 2,

    /// <summary>A waiter sent it to the kitchen. Stays so whatever the booking does afterwards.</summary>
    Sent = 3,

    /// <summary>A waiter marked the party "not coming / no answer". Still sendable until the booking ends.</summary>
    NotComing = 4,

    /// <summary>The booking was cancelled, declined or blocked, or ended (a no-show, or finished) without a send.</summary>
    Cancelled = 5,
}

/// <summary>Why a dish could not go on a pre-order: <c>context.items[].reason</c> of 422 <c>pre-order-items-unavailable</c>.</summary>
public enum PreOrderItemRefusal
{
    /// <summary>Not on this branch's menu as a diner sees it: unknown, another branch's, or not complete.</summary>
    NotOnMenu = 1,

    /// <summary>On the menu and sold out ("unavailable").</summary>
    SoldOut = 2,

    /// <summary>The venue excluded it from pre-orders.</summary>
    NotForPreOrder = 3,
}

/// <summary>The rules of a pre-order that are the product's, not a venue's, and the arithmetic of its times.</summary>
public static class PreOrderRules
{
    /// <summary>
    /// The guest may edit until this many hours before the start. The kitchen's lead time is the product's promise to
    /// every venue, so it is not a setting; the venue's setting is when the waiter sees the card.
    /// </summary>
    public const int EditableUntilHoursBefore = 2;

    /// <summary>"Are you on your way?" is asked this long before the start, of bookings with a pre-order.</summary>
    public const int OnYourWayMinutesBefore = 45;

    public const int DefaultMinutesBefore = 30;

    public const int MinMinutesBefore = 5;

    /// <summary>
    /// Never more than the edit lock: a card the waiter could send while the guest can still change it would let the
    /// kitchen cook the wrong thing.
    /// </summary>
    public const int MaxMinutesBefore = EditableUntilHoursBefore * 60;

    public const int MaxLines = 30;

    public const int MinQuantity = 1;

    public const int MaxQuantity = 20;

    public const int MaxTotalQuantity = 60;

    public const int MaxLineNoteLength = 200;

    public const int MaxNoteLength = 500;

    /// <summary>The wire names a refusal of the shape names.</summary>
    public static class Fields
    {
        public const string Lines = "lines";
        public const string Note = "note";
        public const string MinutesBefore = "minutesBefore";
        public const string ExcludedMenuItemIds = "excludedMenuItemIds";

        public static string Quantity(int index) => $"lines[{index}].quantity";

        public static string LineNote(int index) => $"lines[{index}].note";
    }

    /// <summary>From this instant the guest can no longer change it.</summary>
    public static DateTime LockedAtUtc(DateTime startUtc) => startUtc.AddHours(-EditableUntilHoursBefore);

    /// <summary>When the waiter's card appears.</summary>
    public static DateTime DueAtUtc(DateTime startUtc, int minutesBefore) => startUtc.AddMinutes(-minutesBefore);

    /// <summary>When "are you on your way?" is asked.</summary>
    public static DateTime OnYourWayAtUtc(DateTime startUtc) => startUtc.AddMinutes(-OnYourWayMinutesBefore);

    /// <summary>A booking whose guest may change its pre-order, status-wise (the clock is the other half).</summary>
    public static bool BookingTakesEdits(ReservationStatus status) =>
        status is ReservationStatus.PendingApproval or ReservationStatus.Confirmed;

    /// <summary>
    /// Where a pre-order stands. Sent wins over everything - the kitchen has it, whatever the booking did next; then a
    /// booking that is over without one; then the waiter's "not coming"; then the clock.
    /// </summary>
    public static PreOrderState StateOf(
        ReservationStatus bookingStatus, DateTime startUtc, bool sent, bool notComing, DateTime nowUtc)
    {
        if (sent)
        {
            return PreOrderState.Sent;
        }

        if (bookingStatus is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue
            or ReservationStatus.NoShow or ReservationStatus.Completed)
        {
            return PreOrderState.Cancelled;
        }

        if (notComing)
        {
            return PreOrderState.NotComing;
        }

        return nowUtc >= LockedAtUtc(startUtc) || bookingStatus == ReservationStatus.Seated
            ? PreOrderState.Locked
            : PreOrderState.Editable;
    }

    /// <summary>Every way a saved pre-order's shape breaks a bound, named by wire field. Empty when it is fine.</summary>
    public static IReadOnlyList<FieldViolation> Check(IReadOnlyList<(int Quantity, string? Note)> lines, string? note)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var violations = new List<FieldViolation>();

        if (lines.Count is 0 or > MaxLines)
        {
            violations.Add(new FieldViolation(
                Fields.Lines,
                $"A pre-order has 1 to {MaxLines} lines; {lines.Count} were sent. To remove it, clear it.",
                FieldBounds.Range,
                Min: 1,
                Max: MaxLines,
                Value: lines.Count));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var (quantity, lineNote) = lines[i];

            if (quantity is < MinQuantity or > MaxQuantity)
            {
                violations.Add(new FieldViolation(
                    Fields.Quantity(i),
                    $"A line is {MinQuantity} to {MaxQuantity} of a dish; {quantity} was sent.",
                    FieldBounds.Range,
                    Min: MinQuantity,
                    Max: MaxQuantity,
                    Value: quantity));
            }

            if (lineNote is { Length: > MaxLineNoteLength })
            {
                violations.Add(new FieldViolation(
                    Fields.LineNote(i),
                    $"A line's note is at most {MaxLineNoteLength} characters.",
                    FieldBounds.Max,
                    Max: MaxLineNoteLength,
                    Value: lineNote.Length));
            }
        }

        var total = lines.Sum(l => Math.Max(0, l.Quantity));

        if (lines.Count is > 0 and <= MaxLines && total > MaxTotalQuantity)
        {
            violations.Add(new FieldViolation(
                Fields.Lines,
                $"A pre-order is at most {MaxTotalQuantity} dishes in all; {total} were sent.",
                FieldBounds.Max,
                Max: MaxTotalQuantity,
                Value: total));
        }

        if (note is { Length: > MaxNoteLength })
        {
            violations.Add(new FieldViolation(
                Fields.Note,
                $"The note is at most {MaxNoteLength} characters.",
                FieldBounds.Max,
                Max: MaxNoteLength,
                Value: note.Length));
        }

        return violations;
    }
}

/// <summary>Two hours before the start or later: 409 <c>pre-order-locked</c>, <c>context.reservationId</c>, <c>lockedAtUtc</c>.</summary>
public sealed class PreOrderLockedException(Guid reservationId, DateTime lockedAtUtc)
    : DomainStateException("Pre-orders close two hours before the booking. Call the venue to change it.")
{
    public const string ErrorCode = "pre-order-locked";

    public Guid ReservationId { get; } = reservationId;

    public DateTime LockedAtUtc { get; } = lockedAtUtc;
}

/// <summary>The kitchen already has it: 409 <c>pre-order-already-sent</c>, <c>context.reservationId</c>, <c>sentAtUtc</c>.</summary>
public sealed class PreOrderAlreadySentException(Guid reservationId, DateTime sentAtUtc)
    : DomainStateException("This pre-order has already gone to the kitchen.")
{
    public const string ErrorCode = "pre-order-already-sent";

    public Guid ReservationId { get; } = reservationId;

    public DateTime SentAtUtc { get; } = sentAtUtc;
}

/// <summary>The branch does not take pre-orders: 409 <c>pre-orders-not-accepted</c>, <c>context.branchId</c>.</summary>
public sealed class PreOrdersNotAcceptedException(Guid branchId)
    : DomainStateException("This venue does not take pre-orders.")
{
    public const string ErrorCode = "pre-orders-not-accepted";

    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// Not the time or the state to send, or mark not coming: 409 <c>pre-order-not-sendable</c>, <c>context</c>
/// <c>reservationId</c>, <c>status</c>, <c>sendableFromUtc</c>, <c>endUtc</c>.
/// </summary>
public sealed class PreOrderNotSendableException(
    Guid reservationId, ReservationStatus status, DateTime sendableFromUtc, DateTime endUtc, string message)
    : DomainStateException(message)
{
    public const string ErrorCode = "pre-order-not-sendable";

    public Guid ReservationId { get; } = reservationId;

    public ReservationStatus Status { get; } = status;

    public DateTime SendableFromUtc { get; } = sendableFromUtc;

    public DateTime EndUtc { get; } = endUtc;
}

/// <summary>"On my way" for a booking that is not confirmed or seated, or is over: 409 <c>on-my-way-not-open</c>.</summary>
public sealed class OnMyWayNotOpenException(Guid reservationId, ReservationStatus status)
    : DomainStateException("This booking is not expecting you any more.")
{
    public const string ErrorCode = "on-my-way-not-open";

    public Guid ReservationId { get; } = reservationId;

    public ReservationStatus Status { get; } = status;
}

/// <summary>
/// Dishes that cannot be pre-ordered: 422 <c>pre-order-items-unavailable</c>, <c>context.items</c> naming each with its
/// <see cref="PreOrderItemRefusal"/>.
/// </summary>
public sealed class PreOrderItemsUnavailableException(IReadOnlyList<(Guid MenuItemId, PreOrderItemRefusal Reason)> items)
    : ArgumentException("Some of these dishes cannot be pre-ordered. Remove them and save again.")
{
    public const string ErrorCode = "pre-order-items-unavailable";

    public IReadOnlyList<(Guid MenuItemId, PreOrderItemRefusal Reason)> Items { get; } = items;
}
