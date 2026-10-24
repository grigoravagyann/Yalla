using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Services;

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
            .WithSummary("Every tablet and personal phone enrolled to this branch")
            .WithDescription(
                "Revoked devices are included, so the list is an audit trail rather than a roster.\n\n"
                + "`kind` narrows it: `1` tablets, `2` personal phones; absent, both. Each item says its `kind`; a "
                + "phone also names its person in `boundStaffMember` (`id`, `fullName`, `role`), and its `name` is "
                + "what the phone called itself when it paired - its platform and browser, \"iPhone · Safari\". "
                + "`lastSeenAtUtc` moves with every PIN sign-in and session renewal on the device; `revokedAtUtc` "
                + "is when it was revoked or unpaired.")
            .Produces<IReadOnlyList<StaffDeviceSummary>>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`kind` is not 1 or 2.");

        group.MapPost("/staff/{staffMemberId:guid}/phone-pairings", CreatePhonePairingAsync)
            .WithName("createStaffPhonePairing")
            .WithSummary("Show a QR that pairs one person's own phone to them")
            .WithDescription(
                "Returns `{ code, expiresAtUtc, pairingUrl, staffMemberId, branchId }` **once**: only the code's "
                + "hash is stored. `code` is ten characters shown as `XXXXX-XXXXX`; `pairingUrl` is what the QR "
                + "carries - the staff app's `/staff/pair#code=XXXXX-XXXXX` on the console's own origin, the code "
                + "in the fragment so it never reaches a server log. The phone redeems it with the person's own PIN "
                + "(`POST /api/auth/staff/phone-pair`).\n\n"
                + "**Ten minutes and one use.** Issuing another code for the same person supersedes their unused "
                + "one, so *show a new code* takes a lost one back.\n\n"
                + "**Who may.** Manager or above, from their stored row, by the staff-admin rules: the person must "
                + "be someone the caller may manage, or the caller themselves - a general manager pairs branch "
                + "managers, waiters and themselves, never an owner. A branch manager pairs at their own branch, "
                + "and only their own branch's people. The person must sign in at this branch (at it, or "
                + "venue-wide) - anybody else is 404 here - and be active.\n\n"
                + "**Not kitchen staff**: `409 kitchen-phone-not-allowed`. The kitchen stays on its tablet.\n\n"
                + "Audited as `staff.phone-pairing-issued` (who, for whom, which branch, the expiry - never the code). "
                + "Rate limited like the other credential routes (`auth`).")
            .Produces<PhonePairingCodeResult>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not a manager or above here, a rank the caller may not manage, or a branch manager reaching past "
                + "their own branch.")
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound, "No such branch, or nobody by that id signs in at it.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "`kitchen-phone-not-allowed`, or the person is deactivated.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests, "Too many codes issued by this caller. Wait, then retry.")

            // A way onto a phone is a credential, so it is limited as one - as issuing a sign-in link is - rather
            // than by the global budget sized for a busy floor.
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync)
            .WithName("revokeBranchDevice")
            .WithSummary("Kill a lost or stolen tablet, or unpair somebody's phone")
            .WithDescription(
                "Takes effect on the tablet's next request, not when its token expires - every "
                + "request carrying a device or session token checks this row. Any session open on "
                + "the tablet is ended at the same time, because the session is the thing that can "
                + "act.\n\n"
                + "Permanent. A tablet that turns up again is enrolled afresh.\n\n"
                + "The same for a personal phone (`kind` 2): unpairing it ends its session now, and it is paired "
                + "again only with a new code. A phone's unpairing is audited as `staff.phone-unpaired`.")
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
        IDeviceAdminActivity activity,
        CancellationToken cancellationToken)
    {
        var staffMemberId = http.User.Guid(YallaClaims.StaffMemberId);

        if (staffMemberId is null)
        {
            return Results.Forbid();
        }

        var result = await service.CreateEnrolmentCodeAsync(
            branchId, staffMemberId.Value, cancellationToken);

        await activity.EnrolmentCodeCreatedAsync(branchId, result.ExpiresAtUtc, cancellationToken);

        return Results.Created($"/api/branches/{branchId}/devices", result);
    }

    private static async Task<IResult> ListDevicesAsync(
        Guid branchId,
        StaffDeviceKind? kind,
        IStaffAuthService service,
        CancellationToken cancellationToken)
    {
        if (kind is { } wanted && !Enum.IsDefined(wanted))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), (int)wanted, "kind is 1 (tablets) or 2 (phones).");
        }

        return Results.Ok(await service.ListDevicesAsync(branchId, kind, cancellationToken));
    }

    private static async Task<IResult> CreatePhonePairingAsync(
        Guid branchId,
        Guid staffMemberId,
        HttpContext http,
        IStaffAuthService service,
        CancellationToken cancellationToken)
    {
        var actingId = http.User.Guid(YallaClaims.StaffMemberId);

        if (actingId is null)
        {
            return Results.Forbid();
        }

        var result = await service.CreatePhonePairingCodeAsync(
            branchId, staffMemberId, actingId.Value, cancellationToken);

        return Results.Created($"/api/branches/{branchId}/devices?kind=2", result);
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid branchId,
        Guid deviceId,
        IStaffAuthService service,
        IDeviceAdminActivity activity,
        CancellationToken cancellationToken)
    {
        if (!await service.RevokeDeviceAsync(branchId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        await activity.DeviceRevokedAsync(branchId, deviceId, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ClearPinLockoutAsync(
        Guid branchId,
        Guid staffMemberId,
        IStaffAuthService service,
        IDeviceAdminActivity activity,
        CancellationToken cancellationToken)
    {
        if (!await service.ClearPinLockoutAsync(branchId, staffMemberId, cancellationToken))
        {
            return Results.NotFound();
        }

        await activity.PinLockoutClearedAsync(branchId, staffMemberId, cancellationToken);

        return Results.NoContent();
    }
}
