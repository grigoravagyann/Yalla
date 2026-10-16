using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Media;

namespace Yalla.UnitTests;

/// <summary>
/// A phone photo is stored the way it is meant to be seen, not the way the sensor read it out.
/// </summary>
/// <remarks>
/// A phone held upright writes a landscape-shaped JPEG and an EXIF Orientation tag saying "turn me".
/// Decoding drops the tag with every other metadata block, so unless the storage applies it first
/// the photo is stored sideways with its width and height swapped - and a room photo's table pins,
/// which are fractions of the stored image, land in the wrong places.
/// </remarks>
public sealed class PhotoOrientationTests : IDisposable
{
    private const int SourceWidth = 40;
    private const int SourceHeight = 20;

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-photo-orientation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The source is 40x20 in four quadrants: red top-left, blue top-right, green bottom-left,
    /// yellow bottom-right. Each case says where the red and blue quadrants belong once upright.
    /// </summary>
    /// <remarks>
    /// The corners follow from the EXIF definition of each tag - which visual side the stored first
    /// row and first column are - rather than from any rotation code, so they cannot agree with a
    /// bug by sharing it.
    /// </remarks>
    [Theory]
    [InlineData(1, 40, 20, Corner.TopLeft, Corner.TopRight)]
    [InlineData(3, 40, 20, Corner.BottomRight, Corner.BottomLeft)]
    [InlineData(6, 20, 40, Corner.TopRight, Corner.BottomRight)]
    [InlineData(8, 20, 40, Corner.BottomLeft, Corner.TopLeft)]
    [InlineData(5, 20, 40, Corner.TopLeft, Corner.BottomLeft)]
    [InlineData(2, 40, 20, Corner.TopRight, Corner.TopLeft)]
    [InlineData(4, 40, 20, Corner.BottomLeft, Corner.BottomRight)]
    [InlineData(7, 20, 40, Corner.BottomRight, Corner.TopRight)]
    public async Task A_photo_is_stored_upright_whatever_its_exif_orientation(
        int orientation, int width, int height, Corner red, Corner blue)
    {
        var storage = new LocalDiskPhotoStorage(
            new PhotoStorageOptions { RootPath = root }, NullLogger<LocalDiskPhotoStorage>.Instance);

        await using var content = new MemoryStream(QuadrantJpeg(orientation));
        var stored = await storage.SaveAsync(
            PhotoRules.OwnerKeyForBranch(Guid.CreateVersion7()), content, "image/jpeg");

        Assert.Equal((width, height), (stored.Width, stored.Height));

        await using var file = await storage.OpenAsync(stored.FullPath);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);

        using var full = SKBitmap.Decode(buffer.ToArray());

        Assert.Equal((width, height), (full.Width, full.Height));
        Assert.Equal(Hue.Red, HueAt(full, red));
        Assert.Equal(Hue.Blue, HueAt(full, blue));
    }

    public enum Corner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private enum Hue
    {
        Red,
        Blue,
        Green,
        Yellow,
        Other,
    }

    /// <summary>The colour at the centre of a quadrant, coarse enough to survive JPEG and WebP.</summary>
    private static Hue HueAt(SKBitmap bitmap, Corner corner)
    {
        var x = corner is Corner.TopLeft or Corner.BottomLeft ? bitmap.Width / 4 : bitmap.Width * 3 / 4;
        var y = corner is Corner.TopLeft or Corner.TopRight ? bitmap.Height / 4 : bitmap.Height * 3 / 4;

        var c = bitmap.GetPixel(x, y);

        return (c.Red > 160, c.Green > 160, c.Blue > 160) switch
        {
            (true, false, false) => Hue.Red,
            (false, false, true) => Hue.Blue,
            (false, true, false) => Hue.Green,
            (true, true, false) => Hue.Yellow,
            _ => Hue.Other,
        };
    }

    /// <summary>A 40x20 quadrant JPEG with a real EXIF Orientation tag spliced in after SOI.</summary>
    private static byte[] QuadrantJpeg(int orientation)
    {
        using var bitmap = new SKBitmap(SourceWidth, SourceHeight);

        for (var y = 0; y < SourceHeight; y++)
        {
            for (var x = 0; x < SourceWidth; x++)
            {
                var left = x < SourceWidth / 2;
                var top = y < SourceHeight / 2;

                bitmap.SetPixel(x, y, (left, top) switch
                {
                    (true, true) => new SKColor(255, 0, 0),
                    (false, true) => new SKColor(0, 0, 255),
                    (true, false) => new SKColor(0, 255, 0),
                    _ => new SKColor(255, 255, 0),
                });
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95);

        var body = jpeg.ToArray();
        var exif = OrientationApp1(orientation);
        var result = new byte[body.Length + exif.Length];

        body.AsSpan(0, 2).CopyTo(result);
        exif.CopyTo(result.AsSpan(2));
        body.AsSpan(2).CopyTo(result.AsSpan(2 + exif.Length));

        // The fixture really is tagged as intended, or every case below proves nothing.
        using var codec = SKCodec.Create(new MemoryStream(result));
        Assert.Equal((SKEncodedOrigin)orientation, codec.EncodedOrigin);

        return result;
    }

    /// <summary>
    /// APP1 "Exif" holding a big-endian TIFF header and one IFD with one entry: Orientation (0x0112),
    /// SHORT, count 1.
    /// </summary>
    private static byte[] OrientationApp1(int orientation)
    {
        byte[] payload =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,     // TIFF header, IFD0 at offset 8
            0, 1,                                        // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1,                // Orientation, SHORT, count 1
            0, (byte)orientation, 0, 0,                  // value, padded to four bytes
            0, 0, 0, 0,                                  // no next IFD
        ];

        var length = payload.Length + 2;

        return [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF), .. payload];
    }
}
