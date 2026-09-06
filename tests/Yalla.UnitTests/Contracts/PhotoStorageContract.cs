using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Yalla.Application.Abstractions;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Media;

namespace Yalla.UnitTests.Contracts;

/// <summary>
/// What every <see cref="IPhotoStorage"/> must do, whichever backend it is.
/// </summary>
/// <remarks>
/// <para>
/// There is one implementation today and the interface exists so there can be another - an object
/// store, most likely, once photos outgrow a folder. This suite is what that second implementation
/// will be held to, written now while the rules are still visible in the one that exists rather
/// than reconstructed from it afterwards. It is also what a test double would have to satisfy;
/// there is not one yet, and this is what stops the first one from being written to whatever the
/// test that needed it happened to want.
/// </para>
/// <para>
/// The rules that matter are the ones a caller depends on and cannot check for itself:
/// </para>
/// <list type="bullet">
/// <item><b>Validation happens behind <c>SaveAsync</c>, by sniffing the bytes.</b> The declared
/// content type and the file name are both attacker-controlled, so a backend that trusts either is
/// a backend that stores whatever it is handed.</item>
/// <item><b>The same bytes deduplicate</b>, and say so. An upload that quietly wrote nothing is
/// indistinguishable from one that failed.</item>
/// <item><b>Deleting something that is not there is not an error.</b> The end state is the same,
/// and the orphan sweep would otherwise have to race its own reads.</item>
/// </list>
/// </remarks>
public abstract class PhotoStorageContract
{
    protected abstract IPhotoStorage Storage();

    [Fact]
    public async Task A_saved_photo_reports_three_variants_and_its_content_hash()
    {
        var storage = Storage();
        var branchId = Guid.CreateVersion7();

        using var content = new MemoryStream(PngOf(400, 300));
        var stored = await storage.SaveAsync(branchId, content, "image/png");

        Assert.False(string.IsNullOrWhiteSpace(stored.ThumbnailPath));
        Assert.False(string.IsNullOrWhiteSpace(stored.CardPath));
        Assert.False(string.IsNullOrWhiteSpace(stored.FullPath));

        // Three distinct keys, or two of the variants are the same picture under different names.
        Assert.Equal(
            3,
            new[] { stored.ThumbnailPath, stored.CardPath, stored.FullPath }.Distinct(StringComparer.Ordinal).Count());

        // Lowercase hex SHA-256, because it is part of every path and a case difference between
        // two backends would be a cache miss on every photo.
        Assert.Equal(64, stored.ContentHash.Length);
        Assert.Equal(stored.ContentHash.ToLowerInvariant(), stored.ContentHash);

        Assert.True(stored.Width > 0);
        Assert.True(stored.Height > 0);
        Assert.True(stored.BytesStored > 0L);
        Assert.False(stored.WasDeduplicated);
    }

    [Fact]
    public async Task Every_variant_can_be_opened_again()
    {
        var storage = Storage();

        using var content = new MemoryStream(PngOf(400, 300));
        var stored = await storage.SaveAsync(Guid.CreateVersion7(), content, "image/png");

        foreach (var path in new[] { stored.ThumbnailPath, stored.CardPath, stored.FullPath })
        {
            await using var stream = await storage.OpenAsync(path);
            using var buffer = new MemoryStream();

            await stream.CopyToAsync(buffer);

            Assert.True(buffer.Length > 0, $"{path} opened empty.");
        }
    }

    /// <summary>The same bytes stored twice write nothing the second time, and report that.</summary>
    [Fact]
    public async Task Identical_bytes_deduplicate_and_say_so()
    {
        var storage = Storage();
        var branchId = Guid.CreateVersion7();
        var bytes = PngOf(400, 300);

        using var first = new MemoryStream(bytes);
        var one = await storage.SaveAsync(branchId, first, "image/png");

        using var second = new MemoryStream(bytes);
        var two = await storage.SaveAsync(branchId, second, "image/png");

        Assert.Equal(one.ContentHash, two.ContentHash);
        Assert.Equal(one.FullPath, two.FullPath);
        Assert.True(two.WasDeduplicated);
    }

    /// <summary>
    /// <b>The bytes decide, not the declared type.</b>
    /// </summary>
    /// <remarks>
    /// A caller can label anything <c>image/png</c>, so a backend that believes the label is one
    /// upload away from storing an executable under a name a browser will happily fetch.
    /// </remarks>
    [Fact]
    public async Task Bytes_that_are_not_an_image_are_refused_however_they_are_labelled()
    {
        using var content = new MemoryStream("this is not a picture, whatever the header says"u8.ToArray());

        await Assert.ThrowsAsync<UnsupportedImageException>(
            () => Storage().SaveAsync(Guid.CreateVersion7(), content, "image/png"));
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error()
    {
        var storage = Storage();

        // Twice: the second call is the one the orphan sweep makes after another sweep won.
        await storage.DeleteAsync($"{Guid.CreateVersion7()}/never-stored/full.webp");
        await storage.DeleteAsync($"{Guid.CreateVersion7()}/never-stored/full.webp");
    }

    [Fact]
    public async Task Opening_a_path_that_is_not_there_is_a_file_not_found()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Storage().OpenAsync($"{Guid.CreateVersion7()}/never-stored/full.webp"));
    }

    /// <summary>Something with actual content, so the encoders have work to do.</summary>
    private static byte[] PngOf(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xC1, 0x39, 0x2B));

            using var paint = new SKPaint { Color = new SKColor(0xF4, 0xD0, 0x3F), IsAntialias = true };

            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        return png.ToArray();
    }
}

/// <summary>The only backend that ships today: three WebP variants under a folder per branch.</summary>
public sealed class LocalDiskPhotoStorageContractTests : PhotoStorageContract, IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"yalla-photo-contract-{Guid.NewGuid():N}");

    protected override IPhotoStorage Storage() =>
        new LocalDiskPhotoStorage(
            new PhotoStorageOptions { RootPath = root }, NullLogger<LocalDiskPhotoStorage>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
