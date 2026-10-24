using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// What a staff device does about itself, with a person signed in on it: a phone unpairing itself.
/// </summary>
/// <remarks>
/// Addressed by nothing: the device and the person are the session token's own claims, so there is no id a caller
/// could change to act on somebody else's device.
/// </remarks>
public static class StaffDeviceEndpoints
{
    public static IEndpointRouteBuilder MapStaffDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var device = app.MapGroup("/api/staff/device")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.StaffDeviceSession);

        device.MapPost("/unpair", UnpairOwnPhoneAsync)
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
