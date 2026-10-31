using Yalla.Api.Authorization;
using Yalla.Api.Middleware;
using Yalla.Application.Auth;
using Yalla.Application.Notifications;
using Yalla.Application.Staff;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// What a staff device does about itself, with a person signed in on it: its Web Push subscription and switches, and
/// a phone unpairing itself.
/// </summary>
/// <remarks>
/// Addressed by nothing: the device and the person are the session token's own claims, so there is no id a caller
/// could change to act on somebody else's device.
/// </remarks>
public static class StaffDeviceEndpoints
{
    public static IEndpointRouteBuilder MapStaffDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/staff/push-config", GetPushConfig)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.StaffDeviceToken)
            .WithName("getStaffPushConfig")
            .WithSummary("Whether staff pushes are sent, and the key to subscribe with")
            .WithDescription(
                "`{ enabled, publicKey }`. `publicKey` is the VAPID public key, base64url - the "
                + "`applicationServerKey` for `pushManager.subscribe`. **When it differs from the key the browser's "
                + "current subscription was made with, unsubscribe and subscribe again**: a Development server "
                + "generates a new pair every time it starts, and a key rotated in a deployment does the same.\n\n"
                + "`enabled: false` (no `publicKey`) means this server has no VAPID keys and logs pushes instead of "
                + "sending them - offer no switch. Readable with the device token alone, so the install screen can "
                + "say so before anybody signs in.")
            .Produces<StaffPushConfig>()
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No staff device token, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a staff device or a PIN session on one.");

        app.MapGet("/api/staff/today", GetTodayAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .WithName("getStaffToday")
            .WithSummary("The manager's Today: one branch's day so far, for the staff phone")
            .WithDescription(
                "One read for the phone's Today board: `revenue` (`totalAmd`, `hallAmd` - the Overview's closed tabs - "
                + "`takeawayAmd` and `takeawayOrders` collected, all since `sinceUtc`, the service day's first opening), "
                + "`guests` (`total`, `withBooking`, `walkIn`: people seated since then), `bookings` (`total`, `came` = "
                + "seated + completed, `expected` = confirmed not yet seated, `late`, `noShow`, `pendingApproval` - the "
                + "Overview's counts for the same service day), `room` (`occupiedTables` of `tables`, `people`), "
                + "`needsYou` (`pendingApprovals` the floor can answer now with `oldestApprovalRequestedAtUtc` and "
                + "`soonestApprovalDeadlineUtc`; `unansweredMessages` with `oldestUnansweredMessageAtUtc` - the Messages "
                + "tab's rule) and `shifts` (the service date's shifts at the branch and last night's still running: "
                + "`fullName`, `role`, `startsAt`/`endsAt`, `endsNextDay`, the instants, `onShiftNow`, and `signedIn` - a "
                + "live PIN session on one of the branch's devices).\n\n"
                + "**Which branch.** `branchId` picks it; absent, the session's branch, else the caller's own. A branch "
                + "manager reads their own branch only. An owner or general manager reads any branch of their venue - "
                + "`branches` lists them for the switcher - from their own phone or the console. **On a shared tablet** "
                + "a manager's PIN session reads that tablet's branch only, as every PIN session there is confined; "
                + "`branches` is then that one. Read-only; the floor's actions stay on their own routes.")
            .Produces<StaffToday>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not a manager or above, a branch the caller does not reach, or another branch from a tablet.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        var device = app.MapGroup("/api/staff/device")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.StaffDeviceSession);

        device.MapPut("/push-subscription", SaveSubscriptionAsync)
            .WithName("saveStaffPushSubscription")
            .WithSummary("Store this device's Web Push subscription")
            .WithDescription(
                "Send `PushSubscription.toJSON()` - `{ endpoint, keys: { p256dh, auth } }` - after subscribing, and "
                + "again whenever the browser's subscription changes. One subscription per device: this replaces the "
                + "device's earlier one, and a subscription another device row held (the phone was paired again) "
                + "moves here. Phones and tablets alike; it acts on the device the PIN session is open on.\n\n"
                + "`endpoint` must be an https URL on a known push service (Google's, Mozilla's, Apple's, Microsoft's - "
                + "`WebPush:AllowedEndpointHosts`), `keys.p256dh` a P-256 public key and `keys.auth` a 16-byte secret, "
                + "both base64url; otherwise `422 validation-failed` naming the field. A subscription the push "
                + "service later answers 404 or 410 for is deleted by the server; subscribe again.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No session, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a PIN session on a staff device.")
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity,
                "`endpoint`, `keys.p256dh` or `keys.auth` is not usable - `context.field` names it.");

        device.MapDelete("/push-subscription", DeleteSubscriptionAsync)
            .AllowWhenSurfaceBlocked()
            .WithName("deleteStaffPushSubscription")
            .WithSummary("Forget this device's Web Push subscription")
            .WithDescription(
                "After `pushSubscription.unsubscribe()`, or when the person turns notifications off. Answers 204 "
                + "whether or not there was one.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No session, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a PIN session on a staff device.");

        device.MapGet("/notification-preferences", GetPreferencesAsync)
            .WithName("getStaffNotificationPreferences")
            .WithSummary("This device's notification switches")
            .WithDescription(
                "`{ approvals, guestMessages, tableCalls, takeaway, kitchenReady, onlyDuringShift, locale, subscribed, "
                + "isDefault }`. Until somebody chooses (`isDefault: true`) everything is on except `kitchenReady`, and "
                + "`onlyDuringShift` is on for a waiter's phone and off for a manager's and above. On a tablet "
                + "`onlyDuringShift` is always false: it has no person and so no shift. `locale` is the language "
                + "pushes are written in (absent: Armenian); `subscribed` whether a push subscription is on file.")
            .Produces<StaffNotificationSettings>()
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No session, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a PIN session on a staff device.");

        device.MapPut("/notification-preferences", SavePreferencesAsync)
            .WithName("saveStaffNotificationPreferences")
            .WithSummary("Set this device's notification switches")
            .WithDescription(
                "All six switches every time - `{ approvals, guestMessages, tableCalls, takeaway, kitchenReady, "
                + "onlyDuringShift }` - and optionally `locale` (`hy`, `ru`, `en`) when the person changes the app's "
                + "language, so pushes are written in it. Answers the settings as `GET` does. A device's switches are "
                + "the device's: on a phone, its person's.")
            .Produces<StaffNotificationSettings>()
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No session, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a PIN session on a staff device.")
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity, "A switch is missing, or `locale` is not hy, ru or en.");

        device.MapPost("/unpair", UnpairOwnPhoneAsync)
            .AllowWhenSurfaceBlocked()
            .WithName("unpairOwnStaffPhone")
            .WithSummary("Unpair this phone - the phone's own \"unpair\"")
            .WithDescription(
                "Revokes the personal phone the session is open on and ends its session, as a manager's "
                + "`revokeBranchDevice` would; pairing it again needs a new code from a manager. The client then "
                + "forgets its device token. Answers 204 for a phone already unpaired.\n\n"
                + "A **tablet** is refused with `409 conflicting-state`: a shared device is a manager's to revoke, "
                + "not whoever happens to be signed in on it. Audited as `staff.phone-unpaired` with "
                + "`byItsOwner: true`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "No session, or the device was revoked.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a PIN session on a staff device.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The device is a tablet.");

        return app;
    }

    private static IResult GetPushConfig(IStaffPushService service) => Results.Ok(service.GetConfig());

    private static async Task<IResult> GetTodayAsync(
        Guid? branchId,
        HttpContext http,
        IStaffTodayQuery today,
        CancellationToken cancellationToken)
    {
        // The device a PIN session is open on - a tablet confines it to its branch. An admin-panel token has none.
        var sessionDevice = http.User.PrincipalType() == PrincipalType.StaffSession
            ? http.User.Guid(YallaClaims.DeviceId)
            : null;

        return Results.Ok(await today.GetAsync(
            branchId, sessionDevice, http.User.Guid(YallaClaims.BranchId), cancellationToken));
    }

    private static async Task<IResult> SaveSubscriptionAsync(
        SaveStaffPushSubscriptionRequest request,
        HttpContext http,
        IStaffPushService service,
        CancellationToken cancellationToken)
    {
        if (http.User.Guid(YallaClaims.DeviceId) is not { } deviceId)
        {
            return Results.Unauthorized();
        }

        await service.SaveSubscriptionAsync(
            deviceId,
            new StaffPushSubscriptionInput(request.Endpoint, request.Keys?.P256dh!, request.Keys?.Auth!),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSubscriptionAsync(
        HttpContext http,
        IStaffPushService service,
        CancellationToken cancellationToken)
    {
        if (http.User.Guid(YallaClaims.DeviceId) is not { } deviceId)
        {
            return Results.Unauthorized();
        }

        await service.DeleteSubscriptionAsync(deviceId, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> GetPreferencesAsync(
        HttpContext http,
        IStaffPushService service,
        CancellationToken cancellationToken)
    {
        if (http.User.Guid(YallaClaims.DeviceId) is not { } deviceId
            || http.User.Guid(YallaClaims.StaffMemberId) is not { } staffMemberId)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await service.GetSettingsAsync(deviceId, staffMemberId, cancellationToken));
    }

    private static async Task<IResult> SavePreferencesAsync(
        SaveStaffNotificationPreferencesRequest request,
        HttpContext http,
        IStaffPushService service,
        CancellationToken cancellationToken)
    {
        if (http.User.Guid(YallaClaims.DeviceId) is not { } deviceId
            || http.User.Guid(YallaClaims.StaffMemberId) is not { } staffMemberId)
        {
            return Results.Unauthorized();
        }

        // The validation filter has refused a missing switch before this runs.
        var preferences = new StaffNotificationPreferences(
            request.Approvals!.Value,
            request.GuestMessages!.Value,
            request.TableCalls!.Value,
            request.Takeaway!.Value,
            request.KitchenReady!.Value,
            request.OnlyDuringShift!.Value,

            // Left out by a client from before the switch: on, its default.
            request.BookingToCall ?? true);

        return Results.Ok(await service.SaveSettingsAsync(deviceId, staffMemberId, preferences, request.Locale, cancellationToken));
    }

    private static async Task<IResult> UnpairOwnPhoneAsync(
        HttpContext http,
        IStaffAuthService service,
        CancellationToken cancellationToken)
    {
        var (deviceId, staffMemberId) = (http.User.Guid(YallaClaims.DeviceId), http.User.Guid(YallaClaims.StaffMemberId));

        if (deviceId is null || staffMemberId is null)
        {
            return Results.Unauthorized();
        }

        await service.UnpairOwnPhoneAsync(deviceId.Value, staffMemberId.Value, cancellationToken);

        return Results.NoContent();
    }
}
