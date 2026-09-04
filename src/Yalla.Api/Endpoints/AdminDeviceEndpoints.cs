using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Managing a branch's tablets and PIN lockouts from the admin panel.
/// </summary>
/// <remarks>
/// Manager or owner only, and branch-scoped: a manager of the Abovyan branch cannot enrol or
/// revoke a tablet at Northern Avenue.
/// </remarks>
public static class AdminDeviceEndpoints
{
    public static IEndpointRouteBuilder MapAdminDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapPost("/devices/enrolment-codes", CreateEnrolmentCodeAsync)
            .WithName("createDeviceEnrolmentCode")
            .WithSummary("Generate a one-time code for enrolling a tablet")
            .WithDescription(
                "The code is returned **once**. Only its hash is stored, so a manager who loses it "
                + "issues another rather than looking it up.\n\n"
                + "It is good for 24 hours and exactly one redemption. Codes get read out across a "
                + "bar and will be overheard; single use is what makes that survivable, because a "
                + "second redemption fails and the manager sees a tablet in the list they did not "
                + "enrol.")
            .Produces<DeviceEnrolmentCodeResult>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not a manager or owner, or not for this branch.");

        group.MapGet("/devices", ListDevicesAsync)
            .WithName("listBranchDevices")
            .WithSummary("Every tablet enrolled to this branch")
            .WithDescription("Revoked devices are included, so the list is an audit trail rather than a roster.")
            .Produces<IReadOnlyList<StaffDeviceSummary>>();

        group.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync)
            .WithName("revokeBranchDevice")
            .WithSummary("Kill a lost or stolen tablet")
            .WithDescription(
                "Takes effect on the tablet's next request, not when its token expires - every "
                + "request carrying a device or session token checks this row. Any session open on "
                + "the tablet is ended at the same time, because the session is the thing that can "
                + "act.\n\n"
                + "Permanent. A tablet that turns up again is enrolled afresh.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound, "No such device at this branch.");

        group.MapPost("/staff/{staffMemberId:guid}/clear-pin-lockout", ClearPinLockoutAsync)
            .WithName("clearStaffPinLockout")
            .WithSummary("Unlock a staff member's PIN")
            .WithDescription(
                "The path that actually gets used mid-service. A waiter who fat-fingered their PIN "
                + "during a rush cannot be made to wait out a timer, so a manager clears it.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound, "No such staff member in this branch's venue.");

        return app;
    }

    private static async Task<IResult> CreateEnrolmentCodeAsync(
        Guid branchId,
        HttpContext http,
        IStaffAuthService service,
        CancellationToken cancellationToken)
    {
        var staffMemberId = http.User.Guid(YallaClaims.StaffMemberId);

        if (staffMemberId is null)
        {
            return Results.Forbid();
        }

        var result = await service.CreateEnrolmentCodeAsync(
            branchId, staffMemberId.Value, cancellationToken);

        return Results.Created($"/api/branches/{branchId}/devices", result);
    }

    private static async Task<IResult> ListDevicesAsync(
        Guid branchId,
        IStaffAuthService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListDevicesAsync(branchId, cancellationToken));

    private static async Task<IResult> RevokeDeviceAsync(
        Guid branchId,
        Guid deviceId,
        IStaffAuthService service,
        CancellationToken cancellationToken) =>
        await service.RevokeDeviceAsync(branchId, deviceId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> ClearPinLockoutAsync(
        Guid branchId,
        Guid staffMemberId,
        IStaffAuthService service,
        CancellationToken cancellationToken) =>
        await service.ClearPinLockoutAsync(branchId, staffMemberId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
}
