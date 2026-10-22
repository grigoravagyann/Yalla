using Yalla.Application.Media;
using Yalla.Application.Reviews;
using Yalla.Domain.Enums;

namespace Yalla.Application.Diners;

// ------------------------------------------------------------------ reviews

/// <summary>Body of the diner's review routes.</summary>
/// <param name="Rating">1-5 stars.</param>
/// <param name="Text">Optional, at most 1000 characters. Blank clears it.</param>
public sealed record SubmitBranchReviewCommand(int Rating, string? Text = null);

/// <summary>The signed-in diner's own review of one branch.</summary>
/// <param name="ReviewId">The review.</param>
/// <param name="BranchId">The branch it is about.</param>
/// <param name="Rating">1-5.</param>
/// <param name="Text">Absent when stars only.</param>
/// <param name="CreatedAtUtc">First written.</param>
/// <param name="UpdatedAtUtc">When the rating or text last changed. Re-sending the same review does not move it.</param>
/// <param name="PublicAuthorName">The name the public list shows it under - see <c>BranchReview.PublicAuthorName</c>.</param>
/// <param name="Hidden">Taken down by moderation: the diner still sees it here, nobody else sees it anywhere.</param>
/// <param name="Reply">
/// The venue's public reply, or null, signed as the public reads it ("Aram H."). The author sees it here
/// even while the review is hidden.
/// </param>
public sealed record DinerReviewView(
    Guid ReviewId,
    Guid BranchId,
    int Rating,
    string? Text,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string PublicAuthorName,
    bool Hidden,
    PublicReviewReplyView? Reply = null);

/// <summary>Body of <c>POST /api/diner/reviews/{reviewId}/report</c>.</summary>
/// <param name="Reason"><c>spam</c>, <c>offensive</c>, <c>not-a-visit</c>, <c>personal-info</c> or <c>other</c>.</param>
/// <param name="Note">Optional detail, at most 500 characters.</param>
public sealed record ReportReviewCommand(string? Reason, string? Note = null);

/// <summary>
/// A diner rating a branch. One review per diner per branch; phone-verified accounts that have
/// visited only (K8).
/// </summary>
public interface IBranchReviewService
{
    /// <summary>The caller's review of this branch.</summary>
    /// <exception cref="KeyNotFoundException">No such published branch, or no review by this diner.</exception>
    Task<DinerReviewView> GetMineAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Writes a first review.</summary>
    /// <exception cref="Yalla.Domain.DomainStateException">This diner has already reviewed the branch - revise it instead.</exception>
    /// <exception cref="Yalla.Domain.Identity.PhoneNotVerifiedException">The account's number was never proved.</exception>
    /// <exception cref="Yalla.Domain.Venues.ReviewNeedsVisitException">No visit to the branch in the window.</exception>
    Task<DinerReviewView> CreateAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the caller's review, writing it if there was none. An identical revision writes
    /// nothing and leaves <c>updatedAtUtc</c> where it was.
    /// </summary>
    /// <exception cref="Yalla.Domain.Identity.PhoneNotVerifiedException">The account's number was never proved.</exception>
    /// <exception cref="Yalla.Domain.Venues.ReviewNeedsVisitException">A first write, with no visit in the window.</exception>
    Task<(DinerReviewView Review, bool Created)> UpsertAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports somebody else's published review. A repeat by the same diner is a success that writes nothing.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">A missing or unknown reason, or a note over the limit.</exception>
    /// <exception cref="KeyNotFoundException">No such review, a hidden one, or one at a branch that is not published.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The review is the caller's own.</exception>
    Task ReportAsync(Guid reviewId, ReportReviewCommand command, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------ orders

/// <summary>
/// The diner app's order statuses, and how the kitchen rail maps onto them.
/// </summary>
/// <remarks>
/// The rail is <c>New → InKitchen → Ready → Served</c>, with <c>Voided</c> off the first two. A takeaway order maps
/// onto the same words (<see cref="FromTakeaway"/>): <c>inProgress</c> while it waits for the venue's answer,
/// <c>preparing</c> once accepted, <c>ready</c>, <c>completed</c> once collected, and <c>cancelled</c> for every end
/// that is not a pickup - declined, cancelled by the guest, or not collected.
/// </remarks>
public static class DinerOrderStatuses
{
    public const string Confirmed = "confirmed";
    public const string Preparing = "preparing";
    public const string InProgress = "inProgress";
    public const string Ready = "ready";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    /// <summary>The query-string filter values.</summary>
    public const string ActiveFilter = "active";

    public const string HistoryFilter = "history";

    public static string From(TabOrderStatus status) => status switch
    {
        TabOrderStatus.New => Confirmed,
        TabOrderStatus.InKitchen => Preparing,
        TabOrderStatus.Ready => Ready,
        TabOrderStatus.Served => Completed,
        TabOrderStatus.Voided => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a kitchen status."),
    };

    /// <summary>A takeaway order's status in the app's words.</summary>
    public static string FromTakeaway(Yalla.Domain.Takeaway.TakeawayStatus status) => status switch
    {
        Yalla.Domain.Takeaway.TakeawayStatus.Sent => InProgress,
        Yalla.Domain.Takeaway.TakeawayStatus.Accepted => Preparing,
        Yalla.Domain.Takeaway.TakeawayStatus.Ready => Ready,
        Yalla.Domain.Takeaway.TakeawayStatus.Collected => Completed,
        _ => Cancelled,
    };

    /// <summary>Still moving: the kitchen has not served or voided it.</summary>
    public static bool IsActive(TabOrderStatus status) =>
        status is TabOrderStatus.New or TabOrderStatus.InKitchen or TabOrderStatus.Ready;
}

/// <summary>One line on a diner's order.</summary>
/// <param name="LineId">The line's id.</param>
/// <param name="Name">The item name as it read when ordered.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPriceAmd">Whole dram per unit, as it stood when ordered.</param>
/// <param name="LineTotalAmd">Quantity × unit price, or 0 once voided.</param>
/// <param name="Note">The kitchen note, or absent.</param>
/// <param name="IsVoided">Removed by staff. Kept on the order, at zero, rather than vanishing.</param>
public sealed record DinerOrderItem(
    Guid LineId,
    string Name,
    int Quantity,
    long UnitPriceAmd,
    long LineTotalAmd,
    string? Note,
    bool IsVoided);

/// <summary>When the order reached a status.</summary>
/// <param name="Status">An app status - see <see cref="DinerOrderStatuses"/>.</param>
/// <param name="AtUtc">When.</param>
public sealed record DinerOrderTimelineEntry(string Status, DateTime AtUtc);

/// <summary>
/// One order the diner is on, receipt-shaped, for the Orders tab.
/// </summary>
/// <param name="OrderId">The order's id.</param>
/// <param name="TabId">The tab it was placed against. <b>Absent</b> on a takeaway order, which has no tab.</param>
/// <param name="BranchId">The app's <c>placeId</c>.</param>
/// <param name="VenueName">The brand name.</param>
/// <param name="BranchName">Which location.</param>
/// <param name="CoverPhoto">The branch's cover, or absent.</param>
/// <param name="Kind"><c>"dineIn"</c> for an order on a table's tab, <c>"takeaway"</c> for a takeaway order.</param>
/// <param name="Status">App status - see <see cref="DinerOrderStatuses"/>.</param>
/// <param name="KitchenStatus">
/// The raw rail: 1 New, 2 InKitchen, 3 Ready, 4 Served, 5 Voided. A takeaway order maps Sent to New, Accepted to
/// InKitchen, Ready to Ready, Collected to Served and every other end to Voided; <c>takeaway.status</c> is exact.
/// </param>
/// <param name="TableLabel">The table's label. Empty on a takeaway order.</param>
/// <param name="PartySize">The seated party's size. 0 on a takeaway order.</param>
/// <param name="PlacedAtUtc">When it was sent to the kitchen.</param>
/// <param name="EstimatedReadyAtUtc">The kitchen's estimate, or absent.</param>
/// <param name="TotalAmd">Sum of the non-voided lines, whole dram, before service charge.</param>
/// <param name="Items">Every line, voided ones included.</param>
/// <param name="Timeline">Oldest first; the last entry is the current status.</param>
/// <param name="CanCancel">
/// False on a dine-in order: voiding it is a staff action on the kitchen rail. True on a takeaway order while the venue
/// has not answered it - <c>POST /api/diner/takeaway-orders/{id}/cancel</c>.
/// </param>
/// <param name="Takeaway">The takeaway order's own facts - code, exact status, pickup and ready times. Absent on dine-in.</param>
public sealed record DinerOrderView(
    Guid OrderId,
    Guid? TabId,
    Guid BranchId,
    string VenueName,
    string BranchName,
    PhotoView? CoverPhoto,
    string Kind,
    string Status,
    TabOrderStatus KitchenStatus,
    string TableLabel,
    int PartySize,
    DateTime PlacedAtUtc,
    DateTime? EstimatedReadyAtUtc,
    long TotalAmd,
    IReadOnlyList<DinerOrderItem> Items,
    IReadOnlyList<DinerOrderTimelineEntry> Timeline,
    bool CanCancel,
    DinerTakeawaySummary? Takeaway = null);

/// <summary>A takeaway order on the Orders tab - the card's code, status and times. The whole order is <c>getDinerTakeawayOrder</c>.</summary>
/// <param name="Code">"T-482".</param>
/// <param name="Status">1 Sent, 2 Accepted, 3 Ready, 4 Collected, 5 Declined, 6 Cancelled, 7 NoShow.</param>
/// <param name="PickupAsap">Ordered for as soon as possible.</param>
/// <param name="RequestedPickupUtc">When the guest asked for it.</param>
/// <param name="ReadyAtUtc">When the venue promised it. Absent until accepted.</param>
/// <param name="ItemCount">How many dishes in all - "4 items".</param>
public sealed record DinerTakeawaySummary(
    string Code,
    Yalla.Domain.Takeaway.TakeawayStatus Status,
    bool PickupAsap,
    DateTime RequestedPickupUtc,
    DateTime? ReadyAtUtc,
    int ItemCount);

/// <summary>The signed-in diner's orders.</summary>
public interface IDinerOrderQuery
{
    /// <summary>Active orders first, then newest first, at most 100 - dine-in and takeaway together.</summary>
    /// <param name="status"><c>active</c>, <c>history</c>, or null for both.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException">An unknown <paramref name="status"/>.</exception>
    Task<IReadOnlyList<DinerOrderView>> ListAsync(string? status, CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such order, or not one of this diner's.</exception>
    Task<DinerOrderView> GetAsync(Guid orderId, CancellationToken cancellationToken = default);
}
