using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// One entry in a diner's notifications feed (K12): something that happened to their booking, their
/// order or their review, kept so the app can list it after the push has gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written where the push is enqueued, in the same unit of work</b>, whether or not the diner has a
/// device to push to. The outbox row is the delivery; this row is the record the app lists. A booking
/// that commits has both, and one that does not has neither.
/// </para>
/// <para>
/// <b>No prose.</b> <see cref="Kind"/> and <see cref="ParamsJson"/> are what the app writes its own words
/// from, in the diner's language - the server never renders a sentence into this table.
/// </para>
/// <para>
/// <b><see cref="Entity.CreatedAtUtc"/> is when the entry appears.</b> For almost every kind that is the
/// moment it was written. A booking reminder is written when the booking is made, hours before it is
/// due, so it carries the moment the reminder is due and the feed does not show it before then.
/// Cancelling the booking deletes a reminder that has not appeared yet, alongside its outbox message.
/// </para>
/// <para>
/// <b><see cref="Sequence"/></b> is a database-assigned number that breaks ties between entries with the
/// same instant, so the feed has a total order and a cursor that never skips or repeats one.
/// </para>
/// </remarks>
public sealed class DinerNotification : Entity
{
    /// <summary>How long an entry is kept. Older rows are deleted by the outbox loop's hourly feed sweep.</summary>
    public const int RetentionDays = 90;

    /// <summary>The widest the parameter JSON may be. The largest kind uses a fraction of it.</summary>
    public const int ParamsMaxLength = 2000;

    public Guid DinerUserId { get; private set; }

    /// <summary>One of <see cref="DinerNotificationKinds"/>.</summary>
    public string Kind { get; private set; } = null!;

    /// <summary>A flat JSON object of string values: names, a date, a time, a table label.</summary>
    public string ParamsJson { get; private set; } = null!;

    /// <summary>The place it is about, when there is one. The app opens it on tap.</summary>
    public Guid? BranchId { get; private set; }

    public Guid? ReservationId { get; private set; }

    public Guid? TabId { get; private set; }

    public Guid? OrderId { get; private set; }

    /// <summary>When the diner saw it, or null while unread.</summary>
    public DateTime? ReadAtUtc { get; private set; }

    /// <summary>Assigned by the database on insert; the feed's tie-breaker. Never shown.</summary>
    public long Sequence { get; private set; }

    public bool IsRead => ReadAtUtc is not null;

    private DinerNotification()
    {
    }

    /// <param name="dinerUserId">Whose feed.</param>
    /// <param name="kind">One of <see cref="DinerNotificationKinds"/>.</param>
    /// <param name="paramsJson">The app's parameters for its own text.</param>
    /// <param name="showAtUtc">When it appears - now, or when a reminder is due.</param>
    /// <param name="branchId">The place.</param>
    /// <param name="reservationId">The booking.</param>
    /// <param name="tabId">The tab.</param>
    /// <param name="orderId">The order.</param>
    public DinerNotification(
        Guid dinerUserId,
        string kind,
        string paramsJson,
        DateTime showAtUtc,
        Guid? branchId = null,
        Guid? reservationId = null,
        Guid? tabId = null,
        Guid? orderId = null)
        : base(Guid.CreateVersion7())
    {
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));

        Kind = DinerNotificationKinds.All.Contains(kind, StringComparer.Ordinal)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a notification kind.");

        ParamsJson = Guard.NotBlank(paramsJson, nameof(paramsJson), ParamsMaxLength);
        BranchId = branchId;
        ReservationId = reservationId;
        TabId = tabId;
        OrderId = orderId;
        StampCreatedAt(showAtUtc);
    }
}

/// <summary>What a feed entry is about. Stable slugs; the app keys its text on them.</summary>
public static class DinerNotificationKinds
{
    /// <summary>A booking is coming up. Appears when the reminder push is due.</summary>
    public const string BookingReminder = "booking-reminder";

    /// <summary>A booking that was waiting for the venue was approved.</summary>
    public const string BookingConfirmed = "booking-confirmed";

    /// <summary>A booking that was waiting for the venue was declined.</summary>
    public const string BookingDeclined = "booking-declined";

    /// <summary>The venue let an accepted booking go.</summary>
    public const string BookingCancelledByVenue = "booking-cancelled-by-venue";

    /// <summary>The diner's order is ready, at a branch that sends that push.</summary>
    public const string OrderReady = "order-ready";

    /// <summary>Moderation took the diner's review down.</summary>
    public const string ReviewHidden = "review-hidden";

    /// <summary>
    /// The venue answered the diner's review. Written when a reply is first published, not on an edit;
    /// taken back while unread if the venue removes the reply.
    /// </summary>
    public const string ReviewReplied = "review-replied";

    /// <summary>
    /// Somebody would like the diner's table after their booking, and asks whether they will have left
    /// in time. Answered from the booking's <c>incomingTableRequest</c>; the entry is the record.
    /// </summary>
    public const string TableRequest = "table-request";

    /// <summary>The diner's answer to a request for their table took effect: they promised a time to leave by, or said no.</summary>
    public const string TableRequestDecided = "table-request-decided";

    /// <summary>
    /// The booking was changed from its manage link - by whoever holds the link, which goes round group
    /// chats - and now stands at <c>date</c>/<c>time</c> for <c>partySize</c>, with <c>status</c> (the
    /// <c>ReservationStatus</c> number: 1 waiting for the venue, 2 confirmed). Never written for the
    /// diner's own change in the app.
    /// </summary>
    public const string BookingChangedByLink = "booking-changed-by-link";

    /// <summary>The booking was cancelled from its manage link. Never written for the diner's own cancel in the app.</summary>
    public const string BookingCancelledByLink = "booking-cancelled-by-link";

    /// <summary>
    /// The guest missed three bookings in three days: every booking of theirs not yet started was cancelled and
    /// they cannot book until <c>blockedUntilUtc</c>. Params <c>blockedUntilUtc</c> (ISO 8601 UTC) and
    /// <c>cancelledCount</c>.
    /// </summary>
    public const string BookingsBlockedNoShows = "bookings-blocked-no-shows";

    /// <summary>
    /// A venue marked one of the guest's bookings as a no-show - so a mistake can be put right with the venue
    /// before it matters. Params are the booking's (<c>venueName</c>, <c>branchName</c>, <c>date</c>, <c>time</c>, ...).
    /// </summary>
    public const string BookingNoShow = "booking-no-show";

    /// <summary>
    /// The guest's no-show block was lifted early - a no-show withdrawn, or a platform admin. Params
    /// <c>liftedBecause</c> (1 platform admin, 2 a no-show withdrawn). Nothing it cancelled came back.
    /// </summary>
    public const string BookingsUnblocked = "bookings-unblocked";

    /// <summary>The column width. The longest slug is well inside it.</summary>
    public const int MaxLength = 40;

    public static readonly IReadOnlyList<string> All =
    [
        BookingReminder, BookingConfirmed, BookingDeclined, BookingCancelledByVenue, OrderReady, ReviewHidden,
        TableRequest, TableRequestDecided, ReviewReplied, BookingChangedByLink, BookingCancelledByLink,
        BookingsBlockedNoShows, BookingNoShow, BookingsUnblocked,
    ];
}
