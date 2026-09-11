using Microsoft.AspNetCore.Mvc;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Media;
using Yalla.Domain.Media;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Uploading a photo, and serving one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Served through an endpoint, not a static-file mapping.</b> Pointing the static-file middleware
/// at the storage folder would work today and stop working the moment the bytes move off local disk;
/// this version streams through <c>IPhotoStorage</c> and does not care where they are.
/// </para>
/// <para>
/// Serving is anonymous on purpose. A menu photo is public - the diner reading it has no account and
/// the QR code on the table is the only credential anyone has. The upload is not: it is
/// <c>BranchScoped</c> like every other admin route addressed by branch, so a manager of one venue
/// cannot put images into another's branch, and the service checks the stored staff row again.
/// </para>
/// </remarks>
public static class PhotoEndpoints
{
    /// <summary>
    /// The upload cap, matched to <see cref="PhotoRules.MaxUploadBytes"/>.
    /// </summary>
    /// <remarks>
    /// Enforced twice, deliberately. Kestrel refuses an oversized body before it reaches a handler,
    /// which is the cheap check; the storage layer stops reading as it goes, which is the one that
    /// still holds when the caller lies about the length.
    /// </remarks>
    private const long MaxUploadBytes = PhotoRules.MaxUploadBytes;

    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/branches/{branchId:guid}/photos", UploadAsync)
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped)
            .DisableAntiforgery()
            .WithName("uploadBranchPhoto")
            .WithSummary("Upload a photo for a menu item or a venue card")
            .WithDescription(
                "Multipart upload of a single `file` part. Returns the photo id and its three "
                + "variant URLs; attach the id to a menu item or a branch.\\n\\n"
                + "**The bytes are sniffed, never trusted.** JPEG, PNG and WebP are accepted and "
                + "everything else is refused, whatever the declared content type or the file name "
                + "says - those are both attacker-controlled.\\n\\n"
                + "**EXIF is stripped.** A menu photo comes off the owner's phone carrying GPS "
                + "coordinates and a device serial; the original bytes are never stored. Three "
                + "variants are generated and the originals are discarded.\\n\\n"
                + "Uploading the same image twice returns the first photo and writes nothing, with "
                + "`wasDeduplicated: true`. A photo nothing attaches to is deleted after 24 hours.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<PhotoUploadResult>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Requires the Manager role, at a branch of the caller's own venue, on an account that is still active.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The upload is not a JPEG, PNG or WebP, or it is larger than this system accepts.")
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

        app.MapGet("/api/photos/{photoId:guid}/{variant}", ServeAsync)
            .WithTags(EndpointConventions.DinerTag)
            .AllowAnonymous()
            .WithName("getPhotoVariant")
            .WithSummary("Serve one variant of a photo")
            .WithDescription(
                "`thumbnail`, `card` or `full`, always WebP. Anonymous: a menu photo is public, and "
                + "the diner reading it has no account.\\n\\n"
                + "The content hash is part of the storage path, so a given URL always returns the "
                + "same bytes - replacing a photo produces a new id rather than changing what this "
                + "one serves, which is why these are safe to cache hard.")
            .Produces<IResult>(StatusCodes.Status200OK, "image/webp")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such photo or variant.");

        return app;
    }

    private static async Task<IResult> UploadAsync(
        Guid branchId,
        HttpRequest request,
        IPhotoService photos,
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

        var result = await photos.UploadAsync(
            branchId, content, file.ContentType ?? "application/octet-stream", cancellationToken);

        return Results.Created(result.Photo.CardUrl, result);
    }

    private static async Task<IResult> ServeAsync(
        Guid photoId,
        string variant,
        IPhotoService photos,
        CancellationToken cancellationToken)
    {
        var (content, contentType) = await photos.OpenVariantAsync(photoId, variant, cancellationToken);

        // Immutable: the content hash is in the storage path, so these bytes never change. A new
        // photo gets a new id, which is what makes replacing one need no cache bust at all.
        return Results.Stream(content, contentType, enableRangeProcessing: true);
    }
}
