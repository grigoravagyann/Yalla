using Yalla.Application.Media;
using Yalla.Domain.Enums;

namespace Yalla.Application.Diners;

// ------------------------------------------------------------------ reviews

/// <summary>Body of the diner's review routes.</summary>
/// <param name="Rating">1-5 stars.</param>
/// <param name="Text">Optional, at most 1000 characters. Blank clears it.</param>
public sealed record SubmitBranchReviewCommand(int Rating, string? Text = null);

/// <summary>The signed-in diner's own review of one branch.</summary>
public sealed record DinerReviewView(
    Guid ReviewId,
    Guid BranchId,
    int Rating,
    string? Text,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// A diner rating a branch. One review per diner per branch; phone-verified accounts only.
/// </summary>
public interface IBranchReviewService
{
    /// <summary>The caller's review of this branch.</summary>
    /// <exception cref="KeyNotFoundException">No such published branch, or no review by this diner.</exception>
    Task<DinerReviewView> GetMineAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Writes a first review.</summary>
    /// <exception cref="Yalla.Domain.DomainStateException">This diner has already reviewed the branch - revise it instead.</exception>
    /// <exception cref="Yalla.Domain.Identity.PhoneNotVerifiedException">The account's number was never proved.</exception>
    Task<DinerReviewView> CreateAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the caller's review, writing it if there was none.</summary>
    /// <exception cref="Yalla.Domain.Identity.PhoneNotVerifiedException">The account's number was never proved.</exception>
    Task<(DinerReviewView Review, bool Created)> UpsertAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------ orders

/// <summary>
/// The diner app's order statuses, and how the kitchen rail maps onto them.
/// </summary>
/// <remarks>
/// The rail is <c>New → InKitchen → Ready → Served</c>, with <c>Voided</c> off the first two. The app
/// has one more status, <c>inProgress</c>, for a takeaway on its way; there is no takeaway in the
/// domain, so it is never produced.
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
/// <param name="TabId">The tab it was placed against.</param>
/// <param name="BranchId">The app's <c>placeId</c>.</param>
/// <param name="VenueName">The brand name.</param>
/// <param name="BranchName">Which location.</param>
/// <param name="CoverPhoto">The branch's cover, or absent.</param>
/// <param name="Kind">Always <c>"dineIn"</c>: every order in the domain is placed against a table's tab.</param>
/// <param name="Status">App status - see <see cref="DinerOrderStatuses"/>.</param>
/// <param name="KitchenStatus">The raw rail: 1 New, 2 InKitchen, 3 Ready, 4 Served, 5 Voided.</param>
/// <param name="TableLabel">The table's label.</param>
/// <param name="PartySize">The seated party's size.</param>
/// <param name="PlacedAtUtc">When it was sent to the kitchen.</param>
/// <param name="EstimatedReadyAtUtc">The kitchen's estimate, or absent.</param>
/// <param name="TotalAmd">Sum of the non-voided lines, whole dram, before service charge.</param>
/// <param name="Items">Every line, voided ones included.</param>
/// <param name="Timeline">Oldest first; the last entry is the current status.</param>
/// <param name="CanCancel">
/// Always false. Voiding an order is a staff action on the kitchen rail; a diner has no cancel.
/// </param>
public sealed record DinerOrderView(
    Guid OrderId,
    Guid TabId,
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
    bool CanCancel);

/// <summary>The signed-in diner's orders.</summary>
public interface IDinerOrderQuery
{
    /// <summary>Newest first, at most 100.</summary>
    /// <param name="status"><c>active</c>, <c>history</c>, or null for both.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException">An unknown <paramref name="status"/>.</exception>
    Task<IReadOnlyList<DinerOrderView>> ListAsync(string? status, CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such order, or not one of this diner's.</exception>
    Task<DinerOrderView> GetAsync(Guid orderId, CancellationToken cancellationToken = default);
}
