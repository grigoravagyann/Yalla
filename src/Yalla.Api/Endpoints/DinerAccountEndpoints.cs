using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>PUT /api/diner/me</c>. Only the fields sent change.</summary>
/// <remarks>
/// No field here can be cleared: absent and null are the same on the wire, and all three are
/// things the account is recognised by - the name at the door, the username and email at sign-in.
/// </remarks>
/// <param name="DisplayName">A new name, 1-100 characters.</param>
/// <param name="Username">A new sign-in name, under the same rules as registration. Lowercased on the way in.</param>
/// <param name="Email">A new sign-in address, under the same rules as registration. Lowercased on the way in.</param>
public sealed record UpdateDinerProfileRequest(
    string? DisplayName = null,
    string? Username = null,
    string? Email = null);

/// <summary>Body of <c>PUT /api/diner/me/password</c>.</summary>
/// <param name="CurrentPassword">
/// Required when the account has a password; ignored when it has none yet. The profile's
/// <c>hasPassword</c> says which.
/// </param>
/// <param name="NewPassword">8-128 characters, and not the username or the email. No composition rules.</param>
public sealed record SetDinerPasswordRequest(
    [Required] string NewPassword,
    string? CurrentPassword = null);

/// <summary>Body of <c>DELETE /api/diner/me</c>: the proof that the account holder is asking.</summary>
/// <param name="Password">Required when the account has a password (the profile's <c>hasPassword</c>).</param>
/// <param name="Code">
/// Required when it has none: a one-time code from <c>POST /api/auth/diner/request-code</c> for the
/// account's own number.
/// </param>
public sealed record DeleteDinerAccountRequest(
    string? Password = null,
    string? Code = null);

/// <summary>
/// A diner's own account: reading it, editing it, the password, and the profile picture.
/// </summary>
/// <remarks>
/// <para>
/// Every route here is <c>/api/diner/me</c> with no id in it. The account acted on is the one the
/// bearer token names, read by the service from <c>ICurrentActor</c>, so there is no parameter
/// through which one diner could reach another's row - and nothing for a handler to forget to
/// check.
/// </para>
/// <para>
/// <c>VerifiedDiner</c> is the policy on all of them, which despite its name now admits any
/// diner token - an account that registered with a password holds one before its number is
/// proved. What that policy still refuses is a tab participant: a phone at a table has no
/// account and therefore no profile. See <see cref="YallaPolicies.VerifiedDiner"/>.
/// </para>
/// </remarks>
public static class DinerAccountEndpoints
{
    /// <summary>The upload cap, matched to <see cref="PhotoRules.MaxUploadBytes"/> exactly as the branch upload is.</summary>
    private const long MaxUploadBytes = PhotoRules.MaxUploadBytes;

    public static IEndpointRouteBuilder MapDinerAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diner/me", GetAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .WithName("getDinerProfile")
            .WithSummary("The signed-in diner's own account")
            .WithDescription(
                "What the profile screen shows. Two fields say what is *missing*: `phoneVerified` "
                + "false means the number was typed at sign-up and never proved - show a *verify* "
                + "link into the code flow; `hasPassword` false means the account was created by a "
                + "code and can set a password without giving a current one.\n\n"
                + "`photo` is absent when there is no picture.")
            .Produces<DinerProfileView>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "Needs a diner token. `session-revoked` when the token's session has ended - the account was "
                + "deactivated or deleted, the password was set or changed, or the number's owner proved it. "
                + "Every `/api/diner` route answers that the same way: refresh once, and sign out if the "
                + "refresh is refused too.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "The token is not a diner's - a tab participant has no account.");

        app.MapPut("/api/diner/me", UpdateAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .WithName("updateDinerProfile")
            .WithSummary("Change the name, username or email")
            .WithDescription(
                "Only the fields sent change; a null leaves the current value alone, and nothing here "
                + "can be cleared. The username and email are under exactly the rules and the 409s "
                + "of `register`, so the profile screen and the sign-up form cannot disagree.\n\n"
                + "Answers the whole profile, not just what changed.")
            .Produces<DinerProfileView>()
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest,
                "A field is malformed. `context.field` names it: `displayName`, `username` or `email`.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblem<IdentifierTakenProblem>(
                StatusCodes.Status409Conflict,
                "`username-taken` or `email-taken`, with `context.field` naming the input.");

        app.MapPut("/api/diner/me/password", SetPasswordAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .WithName("setDinerPassword")
            .WithSummary("Set a first password, or change the current one")
            .WithDescription(
                "An account the code flow created has no password and sets one without "
                + "`currentPassword` - the bearer token is the proof. An account that has one must "
                + "send it, and a wrong one is `401 invalid-credentials`.\n\n"
                + "The new password is under the same rule as registration: 8-128 characters, and "
                + "not the username or the email.\n\n"
                + "**Every access token the account holds ends, this one included**: the next call with "
                + "it answers `401 session-revoked`. Refresh tokens are not revoked, so the app refreshes "
                + "and carries on.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest,
                "The new password breaks the rule. `context.field` is `newPassword`.")
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "`invalid-credentials`: the current password is wrong or was needed and not sent. "
                + "`session-revoked`: the token's session has ended. Or no diner token.");

        app.MapDelete("/api/diner/me", DeleteAccountAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
            .WithName("deleteDinerAccount")
            .WithSummary("Delete the signed-in diner's account")
            .WithDescription(
                "Proof first: `password` when the account has one (`hasPassword`), otherwise `code` - a "
                + "one-time code from `POST /api/auth/diner/request-code` for the account's own number.\n\n"
                + "**In one transaction**: the account becomes a tombstone - number, username, email, name, "
                + "password and verification stamp cleared, so all three can register again; every session "
                + "ends (access and refresh tokens); push devices, every review by the account and its "
                + "pictures (files included) are deleted. The venue keeps its records with the link to the "
                + "person removed: bookings keep the guest name and number typed into them, places at a "
                + "table become `Guest`, orders stay. An audit row `diner.delete` is written.\n\n"
                + "Rate limited per address, and per account like a password sign-in: ten attempts in "
                + "fifteen minutes, right or wrong.")
            .Accepts<DeleteDinerAccountRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized,
                "`invalid-credentials`: the password or code is wrong, or the code has expired. "
                + "`session-revoked`: the token's session has ended. Or no diner token.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`validation-failed` naming `password` or `code`, bound `required`: the proof this account needs was not sent.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "`too-many-attempts`: the account's attempts, or the code's, are spent. Or `rate-limited` per address.");

        app.MapPost("/api/diner/me/photo", UploadPhotoAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .DisableAntiforgery()
            .WithName("uploadDinerPhoto")
            .WithSummary("Set the profile picture")
            .WithDescription(
                "Multipart upload of a single `file` part, through the same pipeline as a branch "
                + "photo: **the bytes are sniffed, never trusted** - JPEG, PNG and WebP are accepted "
                + "and everything else refused whatever the declared type says; **EXIF is stripped**; "
                + "three WebP variants are kept and the original is discarded. Same size cap.\n\n"
                + "Replaces whatever picture was there. The same bytes uploaded twice by the same "
                + "person are one photo; the picture replaced is deleted by the orphan sweep after a "
                + "day, so its old URL keeps working for that long and then does not.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<PhotoView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "No `file` part in the upload.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`unsupported-image`: not a JPEG, PNG or WebP, or larger than this system accepts.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "`rate-limited`: more than the `diner-write` budget - ten writes a minute per account by default.")
            .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

        app.MapDelete("/api/diner/me/photo", RemovePhotoAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .WithName("removeDinerPhoto")
            .WithSummary("Remove the profile picture")
            .WithDescription(
                "Succeeds when there was none. The picture is deleted at once, files included: its "
                + "`/api/photos/{id}/…` links answer 404 from the next request.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        IDinerProfileService profile,
        CancellationToken cancellationToken) =>
        Results.Ok(await profile.GetAsync(cancellationToken));

    private static async Task<IResult> UpdateAsync(
        UpdateDinerProfileRequest request,
        IDinerProfileService profile,
        CancellationToken cancellationToken) =>
        Results.Ok(await profile.UpdateAsync(
            new UpdateDinerProfileCommand(request.DisplayName, request.Username, request.Email),
            cancellationToken));

    private static async Task<IResult> SetPasswordAsync(
        SetDinerPasswordRequest request,
        IDinerProfileService profile,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        // An unreadable claim cannot have come from this system; -1 never matches a generation.
        var generation = YallaClaims.TryReadSessionGeneration(http.User, out var read) ? read : -1;

        await profile.SetPasswordAsync(request.CurrentPassword, request.NewPassword, generation, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAccountAsync(
        [FromBody] DeleteDinerAccountRequest? request,
        IDinerProfileService profile,
        CancellationToken cancellationToken)
    {
        // A missing body is the same as one with neither field: the service names what is needed.
        await profile.DeleteAccountAsync(request?.Password, request?.Code, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> UploadPhotoAsync(
        HttpRequest request,
        IDinerProfileService profile,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Send the photo as a multipart form upload." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No file was attached to the upload." });
        }

        await using var content = file.OpenReadStream();

        var view = await profile.SetPhotoAsync(
            content, file.ContentType ?? "application/octet-stream", cancellationToken);

        return Results.Created(view.CardUrl, view);
    }

    private static async Task<IResult> RemovePhotoAsync(
        IDinerProfileService profile,
        CancellationToken cancellationToken)
    {
        await profile.RemovePhotoAsync(cancellationToken);

        return Results.NoContent();
    }
}
