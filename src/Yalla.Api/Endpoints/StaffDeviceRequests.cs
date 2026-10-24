using System.ComponentModel.DataAnnotations;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>PUT /api/staff/device/push-subscription</c>: <c>PushSubscription.toJSON()</c>, as the browser gives it.</summary>
/// <param name="Endpoint">The push service URL. https, on Google's, Mozilla's, Apple's or Microsoft's push service.</param>
/// <param name="Keys">The two keys the payload is encrypted to.</param>
public sealed record SaveStaffPushSubscriptionRequest(
    [Required][StringLength(2000, MinimumLength = 1)] string Endpoint,
    [Required] StaffPushSubscriptionKeys Keys);

/// <summary>A push subscription's keys.</summary>
/// <param name="P256dh">The browser's P-256 public key, base64url (87 characters).</param>
/// <param name="Auth">The browser's 16-byte auth secret, base64url (22 characters).</param>
public sealed record StaffPushSubscriptionKeys(
    [Required][StringLength(200, MinimumLength = 1)] string P256dh,
    [Required][StringLength(200, MinimumLength = 1)] string Auth);

/// <summary>Body of <c>PUT /api/staff/device/notification-preferences</c>: every switch, every time.</summary>
/// <param name="Approvals">A booking waits for the floor's yes or no.</param>
/// <param name="GuestMessages">A guest wrote, or answered "are you coming?".</param>
/// <param name="TableCalls">A table called a waiter or asked for the bill.</param>
/// <param name="Takeaway">A new takeaway order waits to be accepted.</param>
/// <param name="KitchenReady">The kitchen marked an order ready for the person's table.</param>
/// <param name="OnlyDuringShift">A phone's pushes wait for its person's shift. Ignored on a tablet.</param>
/// <param name="Locale">
/// Optional: the language pushes are written in - <c>hy</c>, <c>ru</c> or <c>en</c> - sent when the person changes the
/// app's language. Absent leaves it as it was.
/// </param>
public sealed record SaveStaffNotificationPreferencesRequest(
    [Required] bool? Approvals,
    [Required] bool? GuestMessages,
    [Required] bool? TableCalls,
    [Required] bool? Takeaway,
    [Required] bool? KitchenReady,
    [Required] bool? OnlyDuringShift,
    [StringLength(16)] string? Locale = null);
