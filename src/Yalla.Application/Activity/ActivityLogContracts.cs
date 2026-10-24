using Yalla.Domain.Enums;

namespace Yalla.Application.Activity;

/// <summary>
/// Adds an entry to the venue's activity log - who did what, from which device - in the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>It only adds.</b> The caller's own save commits the entry with the change it records: a change cannot land
/// without its entry, and an entry cannot survive a change that rolled back. A path that saves more than once records
/// before the save that commits the change.
/// </para>
/// <para>
/// <b>Who acted is read from the request</b>, never passed in: the staff member and their name (from the stored row),
/// the device their session came from (its stored name), or the guest, or nobody - the system. A sweep with no request
/// is the system. The one thing a caller may say is that the system did it (<see cref="ActivityRecord.BySystem"/>):
/// a deadline passed while somebody's request happened to be running.
/// </para>
/// </remarks>
public interface IActivityLog
{
    /// <summary>Adds the entry. Never throws for a label or a snapshot: those are clipped, the entry is kept.</summary>
    Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken = default);
}

/// <summary>What happened, for <see cref="IActivityLog"/>. Who did it is the log's to read.</summary>
/// <param name="Category">The History filter it belongs under.</param>
/// <param name="Action">A code from <see cref="ActivityActions"/>.</param>
/// <param name="SubjectType">A type from <see cref="ActivitySubjects"/>.</param>
/// <param name="SubjectId">The booking, tab, order, menu item, person or device.</param>
public sealed record ActivityRecord(
    ActivityCategory Category,
    string Action,
    string SubjectType,
    Guid SubjectId)
{
    /// <summary>The branch it happened at. One of this and <see cref="VenueId"/> is required.</summary>
    public Guid? BranchId { get; init; }

    /// <summary>The venue, when the caller knows it or nothing names a branch; otherwise read from the branch.</summary>
    public Guid? VenueId { get; init; }

    /// <summary>The sitting it belongs to - what puts it on a bill's timeline. See <c>docs/activity.md</c>.</summary>
    public Guid? TableSessionId { get; init; }

    public string? SubjectCode { get; init; }

    public string? TableLabel { get; init; }

    public string? GuestName { get; init; }

    /// <summary>The dishes it names - what a search for an item finds.</summary>
    public IReadOnlyList<string>? ItemNames { get; init; }

    public long? AmountAmd { get; init; }

    /// <summary>About money: kept three years rather than one.</summary>
    public bool IsMoney { get; init; }

    /// <summary>What it was, serialised as JSON. Anything serialisable; null for nothing.</summary>
    public object? Before { get; init; }

    /// <summary>What it became.</summary>
    public object? After { get; init; }

    /// <summary>Why, as somebody typed it.</summary>
    public string? Reason { get; init; }

    /// <summary>The parameters a client renders the line from, serialised as JSON. Never prose.</summary>
    public object? Summary { get; init; }

    /// <summary>Nobody did it: a deadline passed. Recorded as the system whoever's request was running.</summary>
    public bool BySystem { get; init; }

    /// <summary>
    /// The guest did it, though the request may carry no diner token - a booking's manage link, say. Named by
    /// <see cref="GuestName"/>.
    /// </summary>
    public bool ByGuest { get; init; }

    /// <summary>When it happened, when not now - a sweep acting on a deadline that passed earlier records now anyway.</summary>
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary>
    /// The staff member this very request proved with their PIN before any token names them, on the device it set up -
    /// a phone being paired. Recorded as that person on that device, read from the stored rows like any staff actor.
    /// The only way besides <see cref="BySystem"/> and <see cref="ByGuest"/> a caller says who acted.
    /// </summary>
    public ActivityProvedActor? ProvedBy { get; init; }
}

/// <summary>Who a request proved itself to be, for <see cref="ActivityRecord.ProvedBy"/>.</summary>
/// <param name="StaffMemberId">The person whose PIN it checked.</param>
/// <param name="DeviceId">The device it set up for them.</param>
public sealed record ActivityProvedActor(Guid StaffMemberId, Guid DeviceId);

/// <summary>The subject types an entry names. Stable: the console routes on them.</summary>
public static class ActivitySubjects
{
    public const string Reservation = "reservation";
    public const string Tab = "tab";
    public const string TableSession = "table-session";
    public const string Table = "table";
    public const string Order = "order";
    public const string ServiceRequest = "service-request";
    public const string Takeaway = "takeaway";
    public const string Conversation = "conversation";
    public const string MenuItem = "menu-item";
    public const string Branch = "branch";
    public const string StaffMember = "staff";
    public const string Device = "device";
}

/// <summary>
/// Every action code the log writes. Stable: clients render on them and filter on them; add codes, never reword one.
/// The list with what each entry's summary carries is in <c>docs/activity.md</c>.
/// </summary>
public static class ActivityActions
{
    // Bookings.
    public const string BookingCreated = "booking.created";
    public const string BookingMoved = "booking.moved";
    public const string BookingCancelled = "booking.cancelled";
    public const string BookingApproved = "booking.approved";
    public const string BookingDeclined = "booking.declined";
    public const string BookingAutoDeclined = "booking.auto-declined";
    public const string BookingSeated = "booking.seated";
    public const string BookingNoShow = "booking.no-show";
    public const string BookingNoShowUndone = "booking.no-show-undone";
    public const string BookingReleased = "booking.released";
    public const string BookingHoldExtended = "booking.hold-extended";
    public const string BookingGuestAnswered = "booking.guest-answered";

    // Tables and their tabs.
    public const string TableSeated = "table.seated";
    public const string TableFreed = "table.freed";
    public const string TableHeld = "table.held";
    public const string TableHoldReleased = "table.hold-released";
    public const string TableOutOfService = "table.out-of-service";
    public const string TableBackInService = "table.back-in-service";
    public const string TableTabOpened = "table.tab-opened";
    public const string TableTabAdmitted = "table.tab-admitted";
    public const string TableTabTurnedAway = "table.tab-turned-away";
    public const string TableTransferred = "table.transferred";
    public const string TableCallClaimed = "table.call-claimed";
    public const string TableBillPresented = "table.bill-presented";
    public const string TableHostReassigned = "table.host-reassigned";

    // Orders.
    public const string OrderSent = "order.sent";
    public const string OrderPlacedByGuest = "order.placed-by-guest";
    public const string OrderInKitchen = "order.in-kitchen";
    public const string OrderReady = "order.ready";
    public const string OrderServed = "order.served";
    public const string OrderPreOrderSent = "order.pre-order-sent";
    public const string OrderLineVoided = "order.line-voided";
    public const string OrderVoided = "order.voided";

    // Money.
    public const string PaymentCash = "payment.cash";
    public const string PaymentDiscount = "payment.discount";
    public const string PaymentComp = "payment.comp";
    public const string PaymentAdjustmentReversed = "payment.adjustment-reversed";
    public const string PaymentUnpaid = "payment.unpaid";

    // Takeaway.
    public const string TakeawayPlaced = "takeaway.placed";
    public const string TakeawayCancelled = "takeaway.cancelled";
    public const string TakeawayAccepted = "takeaway.accepted";
    public const string TakeawayDeclined = "takeaway.declined";
    public const string TakeawayAutoDeclined = "takeaway.auto-declined";
    public const string TakeawayReady = "takeaway.ready";
    public const string TakeawayCollected = "takeaway.collected";
    public const string TakeawayNoShow = "takeaway.no-show";

    // Guest messages.
    public const string MessageSent = "message.sent";
    public const string MessageAskedOnMyWay = "message.asked-on-my-way";

    // Settings.
    public const string SettingsMenuPrice = "settings.menu-price";
    public const string SettingsMenuAvailability = "settings.menu-availability";
    public const string SettingsHours = "settings.hours";
    public const string SettingsSpecialDays = "settings.special-days";
    public const string SettingsPolicy = "settings.policy";
    public const string SettingsTakeaway = "settings.takeaway";
    public const string SettingsMessages = "settings.messages";
    public const string SettingsPreOrders = "settings.pre-orders";

    // The team.
    public const string StaffCreated = "staff.created";
    public const string StaffUpdated = "staff.updated";
    public const string StaffPinSet = "staff.pin-set";
    public const string StaffSignInIssued = "staff.sign-in-issued";
    public const string StaffPinLockoutCleared = "staff.pin-lockout-cleared";

    // Devices.
    public const string DeviceEnrolmentCodeCreated = "device.enrolment-code-created";
    public const string DeviceEnrolled = "device.enrolled";
    public const string DeviceRevoked = "device.revoked";
    public const string DevicePhonePaired = "device.phone-paired";
    public const string DevicePhoneUnpaired = "device.phone-unpaired";

    /// <summary>The money codes - always written with <see cref="ActivityRecord.IsMoney"/>, kept three years.</summary>
    public static readonly IReadOnlySet<string> Money = new HashSet<string>(StringComparer.Ordinal)
    {
        PaymentCash, PaymentDiscount, PaymentComp, PaymentAdjustmentReversed, PaymentUnpaid, OrderLineVoided,
        OrderVoided, TakeawayCollected,
    };
}

/// <summary>The wire words for <see cref="ActivityCategory"/> and <see cref="ActivityActorKind"/>.</summary>
public static class ActivityWords
{
    public static string Of(ActivityCategory category) => category switch
    {
        ActivityCategory.Booking => "booking",
        ActivityCategory.Table => "table",
        ActivityCategory.Order => "order",
        ActivityCategory.Payment => "payment",
        ActivityCategory.Takeaway => "takeaway",
        ActivityCategory.Message => "message",
        ActivityCategory.Settings => "settings",
        ActivityCategory.Staff => "staff",
        ActivityCategory.Device => "device",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Not a category."),
    };

    public static string Of(ActivityActorKind kind) => kind switch
    {
        ActivityActorKind.Staff => "staff",
        ActivityActorKind.Diner => "guest",
        ActivityActorKind.System => "system",
        ActivityActorKind.PlatformAdmin => "platform",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an actor kind."),
    };

    /// <summary>The actor kind a wire word names, or null when it names none.</summary>
    public static ActivityActorKind? ActorKindFrom(string word) =>
        Enum.GetValues<ActivityActorKind>().Cast<ActivityActorKind?>()
            .FirstOrDefault(k => string.Equals(Of(k!.Value), word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The category a wire word names, or null when it names none.</summary>
    public static ActivityCategory? CategoryFrom(string word) =>
        Enum.GetValues<ActivityCategory>().Cast<ActivityCategory?>()
            .FirstOrDefault(c => string.Equals(Of(c!.Value), word, StringComparison.OrdinalIgnoreCase));
}
