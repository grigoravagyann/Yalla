using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yalla.Application.Abstractions;
using Yalla.Domain.Media;

namespace Yalla.Infrastructure.Media;

/// <summary>Where the photo folder lives.</summary>
/// <remarks>
/// The default is a gitignored folder beside the solution, so a fresh clone works with no
/// configuration and no chance of a developer's lunch photos reaching the repository.
/// </remarks>
public sealed class PhotoStorageOptions
{
    public const string SectionName = "PhotoStorage";

    /// <summary>Absolute or relative path to the root folder.</summary>
    public string RootPath { get; set; } = ".photos";
}

/// <summary>
/// Photos on local disk: validated by sniffing, stripped of metadata, stored as three WebP variants.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout:</b> <c>{root}/{branchId}/{contentHash}/{variant}.webp</c>. The branch prefix is what
/// makes an object store a drop-in later - the same keys work with no rethinking - and the hash
/// segment is what makes replacing a photo need no cache bust and identical uploads deduplicate for
/// nothing.
/// </para>
/// <para>
/// <b>The original bytes never reach the disk.</b> They are decoded into a bitmap and re-encoded,
/// which drops every EXIF, XMP and ICC block on the way through - there is no code path here that
/// could write them by accident, because the encoder is only ever handed pixels.
/// </para>
/// </remarks>
internal sealed class LocalDiskPhotoStorage : IPhotoStorage
{
    private readonly string root;

    private readonly ILogger<LocalDiskPhotoStorage> logger;

    public LocalDiskPhotoStorage(PhotoStorageOptions options, ILogger<LocalDiskPhotoStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.logger = logger;
        root = Path.GetFullPath(options.RootPath);

        EnsureUsable();
    }

    /// <summary>
    /// Creates the folder and proves it can be written to, loudly, at construction.
    /// </summary>
    /// <remarks>
    /// A storage root that is missing or read-only is not a failure anybody notices here: it surfaces
    /// three screens later as a menu whose photos will not upload, and the person seeing it is a
    /// venue owner who has no idea what a permission is. Better to refuse to start.
    /// </remarks>
    private void EnsureUsable()
    {
        try
        {
            Directory.CreateDirectory(root);

            var probe = Path.Combine(root, $".writable-{Guid.NewGuid():N}");

            File.WriteAllText(probe, "yalla");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"The photo storage root '{root}' cannot be written to. Set "
                + $"{PhotoStorageOptions.SectionName}:RootPath to a writable folder. Without it, menu "
                + "photos fail to upload and the venue sees a broken menu rather than an error.",
                ex);
        }

        logger.LogInformation("Photo storage root is {Root}.", root);
    }

    public async Task<StoredPhoto> SaveAsync(
        Guid branchId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var original = await ReadCappedAsync(content, cancellationToken);

        // Sniffed, never trusted. contentType is here only so it can be logged when it disagrees.
        var format = PhotoRules.SniffFormat(original.AsSpan(0, Math.Min(16, original.Length)))
                     ?? throw new UnsupportedImageException(
                         "That file is not a JPEG, PNG or WebP image. Upload a photo taken on a phone "
                         + "or exported from an editor.");

        if (!contentType.Contains(format, StringComparison.OrdinalIgnoreCase))
        {
            // Worth a line: the usual cause is a renamed file, and the unusual one is interesting.
            logger.LogInformation(
                "Upload declared {DeclaredContentType} and its bytes are {DetectedFormat}. The bytes win.",
                contentType, format);
        }

        using var decoded = Decode(original, format);

        var variants = new (string Name, int Edge)[]
        {
            (PhotoRules.ThumbnailName, PhotoRules.ThumbnailEdge),
            (PhotoRules.CardName, PhotoRules.CardEdge),
            (PhotoRules.FullName, PhotoRules.FullEdge),
        };

        var encoded = new Dictionary<string, byte[]>(variants.Length);

        foreach (var (name, edge) in variants)
        {
            encoded[name] = Encode(decoded, edge);
        }

        // The hash is of the processed bytes, not the upload. Two phones photographing the same dish
        // produce different files; the same file uploaded twice produces the same variants, and that
        // is the case worth deduplicating.
        var hash = Hash(encoded[PhotoRules.FullName]);
        var folder = Path.Combine(root, branchId.ToString(), hash);

        var deduplicated = Directory.Exists(folder)
                           && variants.All(v => File.Exists(Path.Combine(folder, v.Name)));

        if (deduplicated)
        {
            logger.LogInformation(
                "Photo {ContentHash} for branch {BranchId} is already stored; nothing was written.",
                hash, branchId);
        }
        else
        {
            Directory.CreateDirectory(folder);

            foreach (var (name, _) in variants)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, name), encoded[name], cancellationToken);
            }
        }

        var full = Decode(encoded[PhotoRules.FullName], "webp");

        return new StoredPhoto(
            hash,
            Key(branchId, hash, PhotoRules.ThumbnailName),
            Key(branchId, hash, PhotoRules.CardName),
            Key(branchId, hash, PhotoRules.FullName),
            full.Width,
            full.Height,
            encoded.Values.Sum(b => (long)b.Length),
            deduplicated);
    }

    public Task<Stream> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);

        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"No stored photo at '{path}'.", path);
        }

        return Task.FromResult<Stream>(
            new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);

        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }

        // Take the hash folder with the last variant in it, so a swept photo leaves nothing behind.
        var folder = Path.GetDirectoryName(resolved);

        if (folder is not null && Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
        {
            Directory.Delete(folder);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a stored key into a path inside the root, refusing anything that escapes it.
    /// </summary>
    /// <remarks>
    /// The keys are ours and the endpoint that serves them takes a photo id rather than a path, so
    /// there is no route by which a caller supplies one today. The check is here anyway, because
    /// "nothing untrusted reaches this method" is a property of the current call sites rather than of
    /// this method, and path traversal is not a mistake worth making twice.
    /// </remarks>
    private string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var combined = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"'{path}' resolves outside the photo storage root.");
        }

        return combined;
    }

    /// <summary>Forward slashes in the stored key, whatever this machine's separator is.</summary>
    private static string Key(Guid branchId, string hash, string variant) =>
        string.Create(CultureInfo.InvariantCulture, $"{branchId}/{hash}/{variant}");

    /// <summary>
    /// Reads the upload, stopping the moment it goes over the cap.
    /// </summary>
    /// <remarks>
    /// Stopping matters. Reading it all and then measuring has already spent the memory the limit was
    /// there to protect, which is the whole attack.
    /// </remarks>
    private static async Task<byte[]> ReadCappedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];

        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > PhotoRules.MaxUploadBytes)
            {
                throw new UnsupportedImageException(
                    $"That photo is larger than {PhotoRules.MaxUploadBytes / 1024 / 1024} MB. "
                    + "A photo taken on a phone is well under it.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new UnsupportedImageException("The upload was empty.");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes to pixels, which is also what removes every metadata block.
    /// </summary>
    /// <remarks>
    /// The dimension check reads the header first and never decodes an oversized image: a
    /// decompression bomb is a small file that expands to an enormous bitmap, so checking after
    /// decoding is checking after the damage.
    /// </remarks>
    private static SKBitmap Decode(byte[] bytes, string format)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);

        if (codec is null)
        {
            throw new UnsupportedImageException(
                "That file looks like an image but could not be read. It may be truncated.", format);
        }

        var info = codec.Info;

        if (info.Width > PhotoRules.MaxSourceEdge || info.Height > PhotoRules.MaxSourceEdge)
        {
            throw new UnsupportedImageException(
                $"That image is {info.Width} by {info.Height} pixels, which is larger than this "
                + $"system accepts ({PhotoRules.MaxSourceEdge} per edge).",
                format);
        }

        return SKBitmap.Decode(codec)
               ?? throw new UnsupportedImageException(
                   "That file looks like an image but could not be decoded.", format);
    }

    /// <summary>Scales the longest edge down to <paramref name="edge"/> and encodes WebP.</summary>
    /// <remarks>
    /// Never scales up. A small photo stays small rather than being blown up into a blurry "full"
    /// variant that is larger on disk and worse to look at.
    /// </remarks>
    private static byte[] Encode(SKBitmap source, int edge)
    {
        var longest = Math.Max(source.Width, source.Height);
        var scale = longest <= edge ? 1d : (double)edge / longest;

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
                            ?? throw new UnsupportedImageException("That image could not be resized.");

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, PhotoRules.Quality)
                            ?? throw new UnsupportedImageException("That image could not be re-encoded.");

        return encoded.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
