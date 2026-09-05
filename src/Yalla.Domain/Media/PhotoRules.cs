namespace Yalla.Domain.Media;

/// <summary>An upload that is not an image this system will store.</summary>
/// <remarks>
/// Its own type because the client's response differs from a generic bad request: the venue owner
/// needs to be told <i>which</i> of the two things went wrong - "that is not a photo" and "that photo
/// is too big" have different fixes, and neither is "try again".
/// </remarks>
public sealed class UnsupportedImageException(string reason, string? detectedFormat = null)
    : DomainStateException(reason)
{
    /// <summary>What the bytes actually turned out to be, when that is known.</summary>
    public string? DetectedFormat { get; } = detectedFormat;
}

/// <summary>The three sizes every photo is stored at, and the limits on what will be accepted.</summary>
/// <remarks>
/// <para>
/// Constants rather than configuration. A venue cannot usefully choose its own thumbnail size - the
/// diner app lays out against these numbers - and making them settings would mean every consumer
/// handling the case where they are something else.
/// </para>
/// <para>
/// Every variant is WebP. One format means one decoder path in every client, and it is materially
/// smaller than JPEG at the same quality over a phone connection in a basement restaurant.
/// </para>
/// </remarks>
public static class PhotoRules
{
    /// <summary>Longest edge of the thumbnail, in pixels. Lists and the floor screen.</summary>
    public const int ThumbnailEdge = 160;

    /// <summary>Longest edge of the card. What a diner actually looks at.</summary>
    public const int CardEdge = 640;

    /// <summary>Longest edge of the full variant. Not the original - see <see cref="Photo"/>.</summary>
    public const int FullEdge = 1600;

    /// <summary>WebP quality. 82 is where the artefacts stop being visible on food photography.</summary>
    public const int Quality = 82;

    /// <summary>
    /// Largest upload accepted, in bytes.
    /// </summary>
    /// <remarks>
    /// Eight megabytes is a generous phone photo and a long way below anything that threatens the
    /// process. The cap is enforced while reading, not after: a limit checked once the bytes are
    /// already in memory has not limited anything.
    /// </remarks>
    public const long MaxUploadBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Largest image accepted, in pixels per edge.
    /// </summary>
    /// <remarks>
    /// A decompression bomb is a small file that decodes to an enormous bitmap, so the pixel budget
    /// is checked from the header before anything is decoded. 12,000 clears any real camera.
    /// </remarks>
    public const int MaxSourceEdge = 12_000;

    /// <summary>The variant file names, which are also the last path segment.</summary>
    public const string ThumbnailName = "thumbnail.webp";

    public const string CardName = "card.webp";

    public const string FullName = "full.webp";

    /// <summary>
    /// What these bytes actually are, read from their magic numbers.
    /// </summary>
    /// <remarks>
    /// <b>The declared content type and the file extension are both attacker-controlled</b> and
    /// neither is consulted. A polyglot file that claims to be a PNG and decodes as something else is
    /// exactly the upload worth refusing, and the only way to refuse it is to look.
    /// </remarks>
    public static string? SniffFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "jpeg";
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (header.Length >= 8 && header[..8].SequenceEqual(png))
        {
            return "png";
        }

        // RIFF....WEBP - the four size bytes in between are not part of the signature.
        if (header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return null;
    }
}
