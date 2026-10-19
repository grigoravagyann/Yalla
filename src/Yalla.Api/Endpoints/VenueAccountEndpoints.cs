using System.ComponentModel.DataAnnotations;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>PATCH /api/venue/me</c>.</summary>
/// <param name="FullName">The name the console shows and the activity feed records. 1-200 characters.</param>
public sealed record UpdateVenueUserProfileRequest([Required] string? FullName);

/// <summary>Body of <c>POST /api/venue/me/password</c>.</summary>
/// <param name="CurrentPassword">The password they sign in with now.</param>
/// <param name="NewPassword">At least 12 characters. Length is the only rule.</param>
public sealed record ChangeVenueUserPasswordRequest(
    [Required] string? CurrentPassword,
    [Required] string? NewPassword);

/// <summary>
/// The venue console's own account: Իմ հաշիվը for an owner, general manager or branch manager.
/// </summary>
/// <remarks>
/// <para>
/// Every route is <c>/api/venue/me</c> with no id in it: the account is the one the token names, read by
/// the service, so there is no parameter through which one person could reach another's row.
/// <c>VenueStaffAccount</c> on the group - an admin-panel token of one of the three venue ranks. The
/// platform admin has <c>/api/platform/me</c>; a tablet session has no account screen.
/// </para>
/// <para>
/// The tablet PIN is not here: a staff member already sets their own through
/// <c>POST /api/venues/{venueId}/staff/{staffMemberId}/pin</c>, with the ids <c>GET /api/venue/me</c> answers.
/// </para>
/// </remarks>
public static class VenueAccountEndpoints
{
    public static IEndpointRouteBuilder MapVenueAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/venue/me")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.VenueStaffAccount);

        group.MapGet("/", GetMeAsync)
            .WithName("getVenueUserProfile")
            .WithSummary("The signed-in owner's, general manager's or branch manager's own account")
            .WithDescription(
                "`fullName` is theirs to change. `email` is the sign-in address and is not: whoever outranks "
                + "them gives them a new sign-in link for a new address (`issueStaffSignIn`), and an owner's is "
                + "changed by the platform. `role` 1 Owner, 6 GeneralManager, 2 Manager; `branchId` is a branch "
                + "manager's home branch and is absent for the venue-wide ranks (an owner's row may name a "
                + "branch; they still run every one). `staffMemberId` and `venueId` are "
                + "what `setStaffPin` takes for their own tablet PIN.")
            .Produces<VenueUserProfile>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not an owner, general manager or branch manager signed in to the admin panel.");

        group.MapPatch("/", UpdateMeAsync)
            .WithName("updateVenueUserProfile")
            .WithSummary("Change the signed-in venue user's name")
            .WithDescription(
                "`fullName`, 1-200 characters after trimming. **Audited** as `account.rename`, with the "
                + "venue; a name that does not change writes nothing.")
            .Produces<VenueUserProfile>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "`fullName` missing, blank or over 200 characters.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not an owner, general manager or branch manager signed in to the admin panel.");

        group.MapPost("/password", ChangeMyPasswordAsync)
            .WithName("changeVenueUserPassword")
            .WithSummary("Change the signed-in venue user's password")
            .WithDescription(
                "The platform admin's change, for the venue console. `currentPassword` must be right and "
                + "`newPassword` at least 12 characters - the one rule every console password follows. "
                + "Everything wrong is reported in one 422: a wrong current password is `context.fields` "
                + "naming `currentPassword` with `bound` `conflict`, not a 401, so the console stays signed in "
                + "and shows it under the field.\n\n"
                + "**Answers a fresh token pair - store it.** The change ends every session the account has, "
                + "at once: the session generation moves, so every admin-panel access token it holds - the one "
                + "this request was sent with included - is refused from now on with `401 session-revoked`, "
                + "every refresh token is revoked and every unused reset link is spent. The body "
                + "(`accessToken`, `refreshToken`, `expiresInSeconds`, the refresh endpoint's shape) is how "
                + "the sign-in that made the change carries on.\n\n"
                + "**Wrong current passwords are counted per account**: each is audited as "
                + "`account.password-change-failed`, and after 10 inside 15 minutes the route answers "
                + "`429 too-many-attempts` with `Retry-After` (and `context.retryAfterSeconds`) until the oldest "
                + "leaves the window - right password or not. A successful change starts the count again. The "
                + "route is also under the `auth` rate limit. **Audited** as `account.password-change`, with "
                + "nothing about either password.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
            .Produces<RefreshResult>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`currentPassword` missing or wrong, or `newPassword` missing or too short - every one at once.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "Too many wrong current passwords, or the `auth` rate limit; `Retry-After` says how long to wait.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not an owner, general manager or branch manager signed in to the admin panel.");

        group.MapGet("/sessions", ListSessionsAsync)
            .WithName("listVenueUserSessions")
            .WithSummary("Where the signed-in venue user is signed in")
            .WithDescription(
                "One row per admin-panel sign-in that can still carry on - a refresh chain neither revoked "
                + "nor expired: `id`, `signedInAtUtc` (when the password was typed), `lastActiveAtUtc` (when it "
                + "last renewed its access token, which a console in use does about every 15 minutes) and "
                + "`isCurrent`, the sign-in this request's token belongs to. The current one first, then the most "
                + "recently active.\n\n"
                + "**No device, browser or place**: nothing records them, so nothing here claims them.")
            .Produces<VenueUserSessions>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not an owner, general manager or branch manager signed in to the admin panel.");

        group.MapPost("/sessions/sign-out-others", SignOutOthersAsync)
            .WithName("signOutOtherVenueUserSessions")
            .WithSummary("Sign the venue user out everywhere else")
            .WithDescription(
                "Ends every other sign-in now: their refresh tokens are revoked and the session generation "
                + "moves, so their access tokens are refused with `401 session-revoked` at once rather than when "
                + "they expire. `endedSessions` counts the sign-ins that could still carry on and now cannot; 0 is "
                + "an answer.\n\n"
                + "**This request's access token ends too, and its refresh token is kept**: refresh "
                + "(`refreshVenueUserToken`) with the one you hold and carry on. No fresh pair is answered, on "
                + "purpose - an access token alone must not be able to mint a new 30-day refresh token. **Audited** "
                + "as `account.sign-out-others`.\n\n"
                + "A token that names no sign-in (no refresh-chain claim, from before the claim existed) "
                + "cannot say which one to keep: `409 sign-in-unknown`, and nothing is changed - sign in "
                + "again and retry.")
            .Produces<SignedOutElsewhere>()
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`sign-in-unknown`: the token names no sign-in to keep. Nothing changed.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not an owner, general manager or branch manager signed in to the admin panel.");

        return app;
    }

    private static async Task<IResult> GetMeAsync(IVenueUserAccountService account, CancellationToken cancellationToken) =>
        Results.Ok(await account.GetAsync(cancellationToken));

    private static async Task<IResult> UpdateMeAsync(
        UpdateVenueUserProfileRequest request,
        IVenueUserAccountService account,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.UpdateAsync(new UpdateVenueUserProfileCommand(request.FullName), cancellationToken));

    private static async Task<IResult> ChangeMyPasswordAsync(
        ChangeVenueUserPasswordRequest request,
        IVenueUserAccountService account,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.ChangePasswordAsync(
            new ChangeVenueUserPasswordCommand(request.CurrentPassword, request.NewPassword),
            cancellationToken));

    private static async Task<IResult> ListSessionsAsync(
        IVenueUserAccountService account,
        HttpContext http,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.ListSessionsAsync(YallaClaims.ReadRefreshChainId(http.User), cancellationToken));

    private static async Task<IResult> SignOutOthersAsync(
        IVenueUserAccountService account,
        HttpContext http,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.SignOutOthersAsync(YallaClaims.ReadRefreshChainId(http.User), cancellationToken));
}
