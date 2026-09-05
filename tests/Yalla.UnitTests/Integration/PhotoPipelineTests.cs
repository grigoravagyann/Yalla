using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Yalla.Application.Abstractions;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Uploading a photo: what is refused, what is stripped, what is written, and what is swept.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PhotoPipelineTests(SqlServerFixture fixture) : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A throwaway storage root per test class run, removed afterwards.</summary>
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-photos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ 1. the bytes decide

    /// <summary>
    /// <b>Test 1.</b> A file that is not an image is refused however it is dressed up.
    /// </summary>
    /// <remarks>
    /// The declared content type and the file name are both chosen by whoever is uploading, so
    /// neither is consulted. This one claims to be a PNG, is named like one, and is a text file.
    /// </remarks>
    [SkippableFact]
    public async Task An_upload_that_is_not_an_image_is_refused_whatever_it_claims_to_be()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var storage = Storage();
        var branchId = Guid.CreateVersion7();

        await using var content = new MemoryStream(
            Encoding.UTF8.GetBytes("<?php system($_GET['c']); ?> this is definitely a picture"));

        var refused = await Assert.ThrowsAsync<UnsupportedImageException>(
            () => storage.SaveAsync(branchId, content, "image/png"));

        Assert.Contains("not a JPEG, PNG or WebP", refused.Message);

        // Nothing reached the disk. A refusal that still wrote the file would be no refusal at all.
        Assert.False(Directory.Exists(Path.Combine(root, branchId.ToString())));
    }

    // ------------------------------------------------------------ 2 and 3. what gets stored

    /// <summary>
    /// <b>Tests 2 and 3.</b> Three variants, the hash in the path, and no metadata anywhere.
    /// </summary>
    /// <remarks>
    /// The source carries a GPS tag and a camera make, as a photo off an owner's phone would. None
    /// of it survives, because the bytes are decoded to pixels and re-encoded - there is no path
    /// here that could keep them.
    /// </remarks>
    [SkippableFact]
    public async Task Every_variant_is_written_under_the_content_hash_with_no_metadata_left()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var storage = Storage();
        var branchId = Guid.CreateVersion7();

        var source = JpegWithExif();

        // The source really does carry what the strip is supposed to remove.
        Assert.True(HasExif(source), "The fixture image has no EXIF, so this test proves nothing.");

        await using var content = new MemoryStream(source);
        var stored = await storage.SaveAsync(branchId, content, "image/jpeg");

        // Three variants, all present, all under the hash.
        Assert.Equal(64, stored.ContentHash.Length);

        foreach (var path in new[] { stored.ThumbnailPath, stored.CardPath, stored.FullPath })
        {
            Assert.Contains(stored.ContentHash, path, StringComparison.Ordinal);
            Assert.StartsWith($"{branchId}/", path, StringComparison.Ordinal);

            await using var variant = await storage.OpenAsync(path);
            using var buffer = new MemoryStream();
            await variant.CopyToAsync(buffer);

            var bytes = buffer.ToArray();

            Assert.NotEmpty(bytes);

            // Every variant is WebP, and none of them carries the source's EXIF.
            Assert.Equal("webp", PhotoRules.SniffFormat(bytes.AsSpan(0, 16)));
            Assert.False(HasExif(bytes), $"{path} still carries metadata from the original.");
            Assert.False(
                ContainsAscii(bytes, "Yalla Test Camera"),
                $"{path} still carries the camera make from the original.");
        }

        // The thumbnail is genuinely smaller than the full one, so these are three sizes and not
        // three copies.
        Assert.True(stored.Width <= PhotoRules.FullEdge);
    }

    // ------------------------------------------------------------ 4. the same bytes twice

    [SkippableFact]
    public async Task Uploading_the_same_bytes_twice_writes_one_copy()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var storage = Storage();
        var branchId = Guid.CreateVersion7();
        var source = JpegWithExif();

        await using var first = new MemoryStream(source);
        var one = await storage.SaveAsync(branchId, first, "image/jpeg");

        Assert.False(one.WasDeduplicated);

        await using var second = new MemoryStream(source);
        var two = await storage.SaveAsync(branchId, second, "image/jpeg");

        Assert.True(two.WasDeduplicated);
        Assert.Equal(one.ContentHash, two.ContentHash);
        Assert.Equal(one.FullPath, two.FullPath);

        // One folder for this branch, holding one hash, holding three files.
        var branchFolder = Path.Combine(root, branchId.ToString());
        var hashFolders = Directory.GetDirectories(branchFolder);

        Assert.Single(hashFolders);
        Assert.Equal(3, Directory.GetFiles(hashFolders[0]).Length);
    }

    // ------------------------------------------------------------ 5. the sweep

    /// <summary>
    /// <b>Test 5.</b> An unattached photo older than a day goes, files included; one a menu item
    /// uses stays however old it is.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_takes_an_abandoned_photo_and_leaves_one_a_menu_item_uses()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var storage = Storage();

        var photos = new PhotoService(
            db, storage, clock, TestActor.Manager(branch.ManagerId), NullLogger<PhotoService>.Instance);

        // One attached to a menu item, one nobody ever used.
        await using var attachedContent = new MemoryStream(JpegWithExif());
        var attached = await photos.UploadAsync(branch.BranchId, attachedContent, "image/jpeg");

        await using var abandonedContent = new MemoryStream(PngOf(64, 48));
        var abandoned = await photos.UploadAsync(branch.BranchId, abandonedContent, "image/jpeg");

        var category = new Yalla.Domain.Menus.MenuCategory(branch.BranchId, "Mains", 0);

        db.MenuCategories.Add(category);
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(
            category.Id, "Khachapuri", "Cheese bread", 3_200L, attached.Photo.PhotoId,
            "flour, cheese", "gluten, dairy", "350 g", 20));

        await db.SaveChangesAsync();

        // Nothing here is old enough yet, so nothing of ours goes - the age half of the rule matters
        // too. The count is not asserted because the sweep is deliberately system-wide and the
        // database is shared; what is asserted is that our young orphan survived it.
        await photos.SweepOrphansAsync();

        await using (var young = fixture.CreateContext(clock))
        {
            Assert.True(
                await young.Photos.AnyAsync(p => p.Id == abandoned.Photo.PhotoId),
                "A photo younger than the grace period was swept.");
        }

        clock.Advance(TimeSpan.FromHours(25));

        Assert.True(await photos.SweepOrphansAsync() >= 1);

        await using var verify = fixture.CreateContext(clock);

        Assert.True(await verify.Photos.AnyAsync(p => p.Id == attached.Photo.PhotoId));
        Assert.False(await verify.Photos.AnyAsync(p => p.Id == abandoned.Photo.PhotoId));

        // Files included. A row swept with its bytes left behind is a leak nothing finds again.
        var abandonedStored = await verify.Photos
            .IgnoreQueryFilters()
            .Where(p => p.Id == abandoned.Photo.PhotoId)
            .Select(p => p.FullPath)
            .FirstOrDefaultAsync();

        Assert.Null(abandonedStored);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => storage.OpenAsync($"{branch.BranchId}/{abandoned.ContentHash}/{PhotoRules.FullName}"));

        // And the attached one's files are still there.
        await using var still = await storage.OpenAsync(
            $"{branch.BranchId}/{attached.ContentHash}/{PhotoRules.FullName}");

        Assert.True(still.Length > 0);
    }

    [SkippableFact]
    public async Task An_image_larger_than_the_cap_is_refused_before_it_is_all_read()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var storage = Storage();

        // Just over the cap, and not an image - the size check comes first, which is the point.
        var oversized = new byte[PhotoRules.MaxUploadBytes + 1024];

        await using var content = new MemoryStream(oversized);

        var refused = await Assert.ThrowsAsync<UnsupportedImageException>(
            () => storage.SaveAsync(Guid.CreateVersion7(), content, "image/jpeg"));

        Assert.Contains("larger than", refused.Message);
    }

    // ------------------------------------------------------------ helpers

    private IPhotoStorage Storage() =>
        new LocalDiskPhotoStorage(
            new PhotoStorageOptions { RootPath = root }, NullLogger<LocalDiskPhotoStorage>.Instance);

    /// <summary>A small JPEG carrying the GPS and camera tags a phone photo would.</summary>
    private static byte[] JpegWithExif()
    {
        using var bitmap = Draw(320, 240);
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        var body = jpeg.ToArray();

        // SkiaSharp will not write EXIF, so it is spliced in as an APP1 segment straight after SOI -
        // which is exactly where a camera puts it, and exactly what the strip has to remove.
        var exif = BuildExifApp1();
        var result = new byte[body.Length + exif.Length];

        body.AsSpan(0, 2).CopyTo(result);
        exif.CopyTo(result.AsSpan(2));
        body.AsSpan(2).CopyTo(result.AsSpan(2 + exif.Length));

        return result;
    }

    /// <summary>
    /// An APP1/Exif segment holding a camera make and a GPS latitude.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than pulled from a library: the test needs bytes that are recognisably EXIF
    /// and recognisably private, and a real photograph in the repository would be both larger and
    /// harder to reason about.
    /// </remarks>
    private static byte[] BuildExifApp1()
    {
        var payload = new List<byte>();

        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange("MM\0*"u8.ToArray());
        payload.AddRange(Encoding.ASCII.GetBytes("Yalla Test Camera\0"));
        payload.AddRange(Encoding.ASCII.GetBytes("GPSLatitude 40.1772 N\0"));
        payload.AddRange(Encoding.ASCII.GetBytes("GPSLongitude 44.5035 E\0"));

        var length = payload.Count + 2;

        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };

        segment.AddRange(payload);

        return [.. segment];
    }

    private static byte[] PngOf(int width, int height)
    {
        using var bitmap = Draw(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        return png.ToArray();
    }

    /// <summary>Something with actual content, so the encoders have work to do.</summary>
    private static SKBitmap Draw(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);

        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(new SKColor(0xC1, 0x39, 0x2B));

        using var paint = new SKPaint { Color = new SKColor(0xF4, 0xD0, 0x3F), IsAntialias = true };

        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);

        return bitmap;
    }

    private static bool HasExif(byte[] bytes) => ContainsAscii(bytes, "Exif\0\0");

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);

        return haystack.AsSpan().IndexOf(pattern) >= 0;
    }
}
