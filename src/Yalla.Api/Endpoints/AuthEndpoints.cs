using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The sign-in flows for all four identity types.
/// </summary>
/// <remarks>
/// <para>
/// Four flows, because there are genuinely four kinds of caller and forcing them into one model
/// breaks the most important of them. The walk-in who scans a QR code has no account and is never
/// going to be asked for one; the diner booking a table proves a phone number because the booking
/// needs it; the waiter taps four digits on a tablet that signed in weeks ago; the owner types an
/// email and a password into a browser. <c>docs/auth.md</c> covers why, one short section each.
/// </para>
/// <para>
/// Everything here is anonymous except the PIN exchange, which is authenticated by the tablet's
/// device token. Generating an enrolment code is a manager's action and lives with the other
/// admin routes in <see cref="AdminDeviceEndpoints"/>.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags(EndpointConventions.AuthTag)
            .AllowAnonymous();

        // The PIN exchange is the one route here that starts from a credential the caller already
        // holds, so it needs authentication - and it cannot live in the group above.
        // AllowAnonymous is not a default that a later RequireAuthorization overrides: the
        // authorization middleware treats an IAllowAnonymous anywhere in an endpoint's metadata as
        // "succeeded", whatever else is attached. Putting it in the anonymous group and asking for
        // authorization on the route would look protected and be open.
        var authenticated = app.MapGroup("/api/auth")
            .WithTags(EndpointConventions.AuthTag)
            .RequireAuthorization();

        // Identity type 1 - the tab participant with no account - is minted by POST /api/tabs/open
        // and /api/tabs/join, in TabEndpoints: the token is a by-product of scanning a table, and
        // the four table-state cases a scan can land in belong with the tab, not with sign-in.
        MapDinerFlow(group);
        MapStaffFlow(group, authenticated);
        MapVenueUserFlow(group);

        return app;
    }

    // ---------------------------------------------------------------------------------------
    // Identity type 2 - diner with a reservation: phone number, one-time code, no password.
    // ---------------------------------------------------------------------------------------
    private static void MapDinerFlow(RouteGroupBuilder group)
    {
        group.MapPost("/diner/request-code", RequestDinerCodeAsync)
            .WithName("requestDinerCode")
            .WithSummary("Send a one-time code to a phone number")
            .WithDescription(
                "Issues a six-digit code, good for five minutes and five attempts, and hands it to "
                + "the configured sender.\n\n"
                + "The response is **identical** whether or not the number already has an account. "
                + "Nothing here reads the account table, so there is no field and no timing "
                + "difference to tell the two apart - this endpoint cannot be used to ask who has "
                + "a Yalla account.\n\n"
                + "Rate limited per address by the pipeline and per phone number by the service. "
                + "Codes cost money to send, so an unlimited request endpoint is an invoice "
                + "generator.\n\n"
                + "In Development the code comes back in `developmentCode`, so the flow works with "
                + "no SMS provider wired up. That field is always null anywhere else.")
            .Produces<VerificationCodeRequestResult>()
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "Too many codes requested for this number or from this address. Wait, then retry.")
            .RequireRateLimiting(RateLimitingExtensions.CodeRequestPolicy);

        group.MapPost("/diner/verify-code", VerifyDinerCodeAsync)
            .WithName("verifyDinerCode")
            .WithSummary("Exchange a one-time code for tokens")
            .WithDescription(
                "Checks the newest live code for the number. On success the account is created if "
                + "this is the first time - there is no separate registration step, because a "
                + "separate registration step is a step people abandon.\n\n"
                + "A wrong code spends one of five attempts. The sixth attempt is refused outright "
                + "with 429: the code is dead and a new one is needed.")
            .Produces<DinerSignInResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "The code was wrong or has expired. Codes last five minutes.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "The code is out of attempts. Request a new one.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/diner/refresh", RefreshDinerAsync)
            .WithName("refreshDinerToken")
            .WithSummary("Rotate a diner refresh token")
            .WithDescription(
                "Spends the handle you send and returns its successor. Sending a handle that was "
                + "already spent means two parties hold it, so the whole chain from that sign-in "
                + "is revoked and both must sign in again.")
            .Produces<RefreshResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "The handle is unknown, expired, revoked, or was already spent.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/diner/sign-out", SignOutAsync)
            .WithName("signOutDiner")
            .WithSummary("Revoke a refresh chain")
            .WithDescription("Always succeeds, including for a handle that was never valid.")
            .Produces(StatusCodes.Status204NoContent);
    }

    // ---------------------------------------------------------------------------------------
    // Identity type 3 - staff: device-bound branch token plus a per-person PIN.
    // ---------------------------------------------------------------------------------------
    /// <param name="group">Anonymous routes: enrolment, renewal, sign-out.</param>
    /// <param name="authenticated">
    /// Routes needing a token already in hand. Only the PIN exchange, which is authenticated by
    /// the tablet's device token.
    /// </param>
    private static void MapStaffFlow(RouteGroupBuilder group, RouteGroupBuilder authenticated)
    {
        group.MapPost("/staff/enrol", RedeemEnrolmentCodeAsync)
            .WithName("enrolStaffDevice")
            .WithSummary("Redeem a one-time enrolment code for a device token")
            .WithDescription(
                "Turns a code a manager generated into a long-lived token bound to one tablet and "
                + "one branch. The code works exactly once; a second attempt is a 409, and a "
                + "manager who sees one should check the branch's device list for a tablet they "
                + "did not enrol.\n\n"
                + "The device token can do exactly one thing - offer a PIN. It carries a branch "
                + "but no person, so an enrolled tablet with nobody signed in cannot seat a table.")
            .Produces<DeviceEnrolmentResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized, "The code is unknown or has expired.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "The code has already been redeemed.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        authenticated.MapPost("/staff/pin", StaffPinSignInAsync)
            .WithName("signInStaffWithPin")
            .WithSummary("Exchange a device token and a PIN for a staff session")
            .WithDescription(
                "The whole point of the staff model: **a waiter never types an email during a "
                + "Friday rush**. The tablet signs in once and stays signed in; a person becomes "
                + "present with four taps.\n\n"
                + "Send the device token as the bearer. The session token that comes back carries "
                + "the staff member, their role and the tablet's branch, and lasts thirty minutes; "
                + "the renewal handle keeps it alive while the tablet is in use and stops working "
                + "after thirty minutes of inactivity.\n\n"
                + "PINs are per person, never shared. A shared PIN is faster and destroys the "
                + "audit trail that answers 'who gave away my reserved table'.")
            .Produces<StaffSessionResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "Wrong PIN, or the tablet is no longer enrolled. The two are not distinguished.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Too many wrong PINs. A manager clears the lockout - see the admin endpoints.")
            .RequireRateLimiting(RateLimitingExtensions.PinPolicy);

        group.MapPost("/staff/renew", RenewStaffSessionAsync)
            .WithName("renewStaffSession")
            .WithSummary("Keep a staff session alive")
            .WithDescription(
                "Rotates the renewal handle and pushes the inactivity window forward. Refused once "
                + "the session has been idle for thirty minutes, once its shift-length cap runs "
                + "out, or as soon as the tablet is revoked - in every case the answer is to tap "
                + "the PIN again.")
            .Produces<StaffSessionResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized, "The session timed out or the tablet was revoked.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/staff/sign-out", StaffSignOutAsync)
            .WithName("signOutStaffSession")
            .WithSummary("End a staff session")
            .WithDescription("The tablet's sign-out button. Always succeeds.")
            .Produces(StatusCodes.Status204NoContent);
    }

    // ---------------------------------------------------------------------------------------
    // Identity type 4 - venue owner and manager: email and password.
    // ---------------------------------------------------------------------------------------
    private static void MapVenueUserFlow(RouteGroupBuilder group)
    {
        group.MapPost("/venue/sign-in", VenueUserSignInAsync)
            .WithName("signInVenueUser")
            .WithSummary("Sign in to the admin panel with email and password")
            .WithDescription(
                "The conventional flow, because this is a desktop browser session holding prices, "
                + "refunds and staff accounts.\n\n"
                + "Unknown address, wrong password and deactivated account all answer identically, "
                + "so this form cannot be used to discover which addresses have accounts.")
            .Produces<VenueUserSignInResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized, "The email and password do not match an account.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/venue/refresh", RefreshVenueUserAsync)
            .WithName("refreshVenueUserToken")
            .WithSummary("Rotate an admin-panel refresh token")
            .WithDescription("As the diner refresh: rotating, and reuse revokes the whole chain.")
            .Produces<RefreshResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "The handle is unknown, expired, revoked, or was already spent.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/venue/sign-out", SignOutAsync)
            .WithName("signOutVenueUser")
            .WithSummary("Revoke a refresh chain")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/venue/request-password-reset", RequestPasswordResetAsync)
            .WithName("requestPasswordReset")
            .WithSummary("Email a password-reset link")
            .WithDescription(
                "Answers 202 whether or not the address has an account, for the same reason "
                + "`request-code` answers identically to everyone.")
            .Produces(StatusCodes.Status202Accepted)
            .RequireRateLimiting(RateLimitingExtensions.CodeRequestPolicy);

        group.MapPost("/venue/reset-password", ResetPasswordAsync)
            .WithName("resetPassword")
            .WithSummary("Complete a password reset")
            .WithDescription(
                "Consumes the link and revokes every session the account had open - the usual "
                + "reason to reset a password is that somebody else might have had it.\n\n"
                + "Only a minimum length is enforced. No composition rules.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest, "The new password is shorter than the minimum.")
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized, "The link is unknown, spent or expired.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);
    }

    private static async Task<IResult> RequestDinerCodeAsync(
        RequestDinerCodeRequest request,
        IDinerAuthService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await service.RequestCodeAsync(
            request.PhoneE164,
            request.LocaleCode ?? http.Request.Headers.AcceptLanguage.ToString(),
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> VerifyDinerCodeAsync(
        VerifyDinerCodeRequest request,
        IDinerAuthService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await service.VerifyCodeAsync(
            request.PhoneE164,
            request.Code,
            request.LocaleCode ?? http.Request.Headers.AcceptLanguage.ToString(),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> RefreshDinerAsync(
        RefreshTokenRequest request,
        ITokenRefreshService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.RefreshDinerAsync(request.RefreshToken, cancellationToken));

    private static async Task<IResult> RefreshVenueUserAsync(
        RefreshTokenRequest request,
        ITokenRefreshService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.RefreshVenueUserAsync(request.RefreshToken, cancellationToken));

    private static async Task<IResult> SignOutAsync(
        RefreshTokenRequest request,
        ITokenRefreshService service,
        CancellationToken cancellationToken)
    {
        await service.SignOutAsync(request.RefreshToken, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> RedeemEnrolmentCodeAsync(
        RedeemEnrolmentCodeRequest request,
        IStaffAuthService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.RedeemEnrolmentCodeAsync(
            request.Code, request.DeviceName, cancellationToken));

    /// <summary>
    /// The one endpoint that reads a claim in the handler, because the device is the credential
    /// being exchanged rather than something being authorised.
    /// </summary>
    private static async Task<IResult> StaffPinSignInAsync(
        StaffPinRequest request,
        IStaffAuthService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var deviceId = http.User.Guid(YallaClaims.DeviceId);

        if (deviceId is null)
        {
            return Results.Unauthorized();
        }

        var result = await service.SignInWithPinAsync(
            deviceId.Value, request.StaffMemberId, request.Pin, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> RenewStaffSessionAsync(
        RenewStaffSessionRequest request,
        IStaffAuthService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.RenewSessionAsync(request.RenewalToken, cancellationToken));

    private static async Task<IResult> StaffSignOutAsync(
        RenewStaffSessionRequest request,
        IStaffAuthService service,
        CancellationToken cancellationToken)
    {
        await service.SignOutSessionAsync(request.RenewalToken, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> VenueUserSignInAsync(
        VenueUserSignInRequest request,
        IVenueUserAuthService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.SignInAsync(request.Email, request.Password, cancellationToken));

    private static async Task<IResult> RequestPasswordResetAsync(
        RequestPasswordResetRequest request,
        IVenueUserAuthService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        await service.RequestPasswordResetAsync(
            request.Email,
            request.LocaleCode ?? http.Request.Headers.AcceptLanguage.ToString(),
            cancellationToken);

        // 202, not 200: whether anything was actually sent is precisely what this endpoint must
        // not say.
        return Results.Accepted();
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IVenueUserAuthService service,
        CancellationToken cancellationToken)
    {
        await service.ResetPasswordAsync(request.ResetToken, request.NewPassword, cancellationToken);

        return Results.NoContent();
    }
}
