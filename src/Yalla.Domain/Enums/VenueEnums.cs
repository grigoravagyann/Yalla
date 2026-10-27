namespace Yalla.Domain.Enums;

/// <summary>What kind of place a venue is. Drives the shipped reservation-policy defaults.</summary>
public enum VenueType
{
    Cafe = 1,
    Restaurant = 2,
    Bar = 3,
}

/// <summary>
/// One feature the platform admin allows or blocks <b>per branch</b>. Replaced the Free/Paid
/// <c>SubscriptionTier</c> on 2026-10-27.
/// </summary>
/// <remarks>
/// <para>
/// Stored per branch, never per venue: a chain with four locations is four customers, each with its
/// own set. The venue-level "apply to every branch" is a convenience over the branches, not a value
/// of its own. See <c>Branch.AllowedFeatures</c> and <c>BranchFeatures</c>.
/// </para>
/// <para>
/// Two layers: the admin allows or blocks a feature; an owner setting where one exists
/// (<c>TakeawayEnabled</c>, <c>AcceptsPreOrders</c>, <c>AcceptsWebBookings</c>, <c>GuestsCanMessage</c>)
/// works only inside that. Blocking a feature never clears the owner's setting, so allowing it again
/// restores it.
/// </para>
/// <para>
/// On the wire as the integer. Persisted as one bit each (<c>1 &lt;&lt; (value - 1)</c>) in
/// <c>Branches.AllowedFeatures</c>, so <b>never renumber a member</b>: the number is the bit.
/// </para>
/// </remarks>
public enum BranchFeature
{
    /// <summary>Diner-app bookings: create and reschedule.</summary>
    AppBookings = 1,

    /// <summary>Bookings from the public page, including the <c>/booking/{token}</c> manage link's reschedule.</summary>
    WebBookings = 2,

    /// <summary>Pre-orders on bookings. Needs <see cref="AppBookings"/> or <see cref="WebBookings"/>.</summary>
    PreOrders = 3,

    /// <summary>Takeaway orders.</summary>
    Takeaway = 4,

    /// <summary>Tabs, QR/table ordering, waiter ordering, bill request and tip, payments - what Paid used to gate.</summary>
    TableService = 5,

    /// <summary>Venue-guest message threads.</summary>
    GuestMessages = 6,

    /// <summary>Diner reviews: writing them, showing them publicly, and console replies.</summary>
    Reviews = 7,

    /// <summary>The staff phone PWA: pairing, sign-in and pushes to staff phones.</summary>
    StaffPhone = 8,

    /// <summary>The kitchen screen and its tickets view.</summary>
    KitchenDisplay = 9,

    /// <summary>Console reports and CSV export.</summary>
    Reports = 10,
}

/// <summary>
/// The <b>physical</b> state of a table: what somebody did to it.
/// </summary>
/// <remarks>
/// <para>
/// Every member here is the result of a person performing an action - seating a party, placing a
/// hold, taking a table out of service. That is the admission rule for this column.
/// </para>
/// <para>
/// There is deliberately <b>no <c>Reserved</c></b> member. "Reserved" is time-dependent: a table
/// becomes reserved at 19:45 because a booking starts at 20:00, and no person performs that
/// transition. Storing it would need a background job flipping rows on a timer, and every failure
/// mode of that job is bad - a stalled sweep leaves tables sellable that are not, a cancellation
/// leaves a table reserved forever, and clock drift between the job and the tablet makes the two
/// disagree. It is derived at read time instead: see <see cref="DerivedTableState"/>.
/// </para>
/// <para>
/// Numeric values are not contiguous. <c>3</c> was <c>Reserved</c> and is left permanently vacant
/// so it can never be silently reused and mean two different things across rows written at
/// different times.
/// </para>
/// <para>
/// This column is a <b>denormalised cache</b> so the floor plan renders in one query. The
/// authoritative record of who is sitting at a table is <c>TableSession</c>; when the two
/// disagree the session wins and this is to be recomputed from it. Every transition is also
/// written to <c>TableStateChange</c>.
/// </para>
/// </remarks>
public enum TableStatus
{
    /// <summary>Nobody is seated and no hold is in force.</summary>
    Free = 1,

    /// <summary>
    /// Held by staff for a party expected imminently - typically a late booking the waiter has
    /// decided to keep the table for.
    /// </summary>
    Held = 2,

    // 3 was Reserved. Permanently retired - see the remarks above. Do not reuse.

    /// <summary>A party is seated: an open <c>TableSession</c> exists.</summary>
    Occupied = 4,

    /// <summary>Withdrawn from service (broken, being repaired, staff-blocked). Never auto-assigned.</summary>
    OutOfService = 5,
}

/// <summary>
/// What a client should draw for a table: its physical <see cref="TableStatus"/> with the
/// reservation overlay applied.
/// </summary>
/// <remarks>
/// A presentation concern computed per request and never stored. It exists so the three clients
/// do not each reimplement "is this table reserved soon?" from raw bookings and then disagree
/// with each other.
/// </remarks>
public enum DerivedTableState
{
    /// <summary>Free, with no booking close enough to matter.</summary>
    Free = 1,

    /// <summary>
    /// Physically free, but a booking starts soon enough that the table should not be sold. The
    /// threshold is the branch's own turnaround buffer, not a constant.
    /// </summary>
    ReservedSoon = 2,

    /// <summary>Held by staff.</summary>
    Held = 3,

    /// <summary>A party is seated.</summary>
    Occupied = 4,

    /// <summary>Withdrawn from service.</summary>
    OutOfService = 5,
}

/// <summary>
/// What a branch change-stream entry signals beyond the table's status, when it is about the tab on the
/// table rather than the table itself. Persisted as its number on <c>TableStateChanges.Signal</c>.
/// </summary>
/// <remarks>
/// Written on a same-status row (Occupied to Occupied) - the precedent is the hold-extended row - or on
/// the reject's own Occupied to Free row, so every tablet following the branch stream learns of it.
/// <see cref="TakeawayChanged"/> alone is on a row with no table at all.
/// </remarks>
public enum TableChangeSignal
{
    /// <summary>A scan opened a tab that waits for a waiter's admission.</summary>
    TabAwaitingAdmission = 1,

    /// <summary>A tab that was waiting was admitted.</summary>
    TabAdmitted = 2,

    /// <summary>A tab that was waiting was turned away.</summary>
    TabAdmissionRejected = 3,

    /// <summary>
    /// A booking on this table was cancelled by the platform: its guest was blocked for three no-shows in three
    /// days. A same-status row naming the booking (<c>ReservationId</c>) - refetch the bookings.
    /// </summary>
    BookingCancelledByBlock = 4,

    /// <summary>
    /// A booking on this table was made, or its status, table, time or party changed - approved, declined, moved
    /// (both tables get one), cancelled, marked a no-show, a no-show withdrawn. A same-status row naming the booking
    /// (<c>ReservationId</c>): refresh the table's next booking (re-read the floor); do not fold its status.
    /// </summary>
    BookingChanged = 5,

    /// <summary>
    /// A booking's pre-order changed: saved, edited, cleared, sent to the kitchen, marked not coming, put on the party's
    /// tab, its held ticket moved by the kitchen - or the guest answered "on my way". A same-status row naming the
    /// booking (<c>ReservationId</c>): re-read the pre-order rail (<c>GET /api/branches/{id}/pre-orders/due</c>); do not
    /// fold its status.
    /// </summary>
    PreOrderChanged = 6,

    /// <summary>
    /// The table was handed to another waiter (<c>POST /api/tabs/{tabId}/transfer</c>). A same-status row naming the tab:
    /// re-read the floor - its <c>serverStaffMemberId</c> and <c>serverName</c> moved. See <c>docs/activity.md</c>.
    /// </summary>
    TableTransferred = 7,

    /// <summary>
    /// A takeaway order at the branch was placed, accepted, declined (by staff or by the sweep), cancelled by the guest,
    /// marked ready, collected or marked not collected (2026-10-26). The one row about the counter rather than a table:
    /// no <c>DiningTableId</c>, both statuses Free and meaning nothing - never fold it. Re-read the takeaway counter
    /// (<c>GET /api/branches/{id}/takeaway-orders</c>) and the kitchen's queue.
    /// </summary>
    TakeawayChanged = 8,

    /// <summary>
    /// A tab at this table was written off - the party left without paying and a manager took the loss
    /// (<c>POST /api/tabs/{tabId}/abandon</c>, 2026-10-27). A same-status row naming the tab (<c>TabId</c>), in the
    /// table's status as it stands - writing a tab off moves no table. Its open calls were answered and its orders left
    /// the kitchen's queue: re-read the calls (<c>GET /api/branches/{id}/service-requests</c>), the queue
    /// (<c>GET /api/branches/{id}/orders</c>) and the floor; do not fold its status.
    /// </summary>
    TabWrittenOff = 9,
}
