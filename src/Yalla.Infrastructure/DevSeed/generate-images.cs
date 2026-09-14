#:package SkiaSharp@3.116.1

// Draws the three Development seed pictures for the demo branch: cover.jpg, gallery-1.jpg and
// gallery-2.jpg. Every pixel comes from the shapes below - no photograph, no font, no third-party
// artwork - so the files carry no licence but this repository's own.
//
// Run with a .NET 10 SDK, from a folder outside the repository (its global.json pins .NET 9):
//
//   dotnet run generate-images.cs -- <output folder>
//
// The cover is laid out to match the seed: each table is drawn where DevListingSeeder places its
// pin - x = 0.1 + 0.8 * (floor centre x / 1000), y = 0.25 + 0.6 * (floor centre y / 700).

using SkiaSharp;

var output = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
Directory.CreateDirectory(output);

const int W = 1500;
const int H = 1000;

Save("cover.jpg", DrawCover);
Save("gallery-1.jpg", DrawBreakfast);
Save("gallery-2.jpg", DrawTerrace);

void Save(string name, Action<SKCanvas> draw)
{
    using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
    draw(surface.Canvas);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
    File.WriteAllBytes(Path.Combine(output, name), data.ToArray());
    Console.WriteLine($"{name}: {data.Size} bytes");
}

static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

static SKPaint Stroke(SKColor color, float width) =>
    new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };

static SKPaint Shadow(float blur) =>
    new() { Color = new SKColor(0, 0, 0, 70), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(blur, blur) };

// ------------------------------------------------------------------ cover: the room from above

static void DrawCover(SKCanvas c)
{
    var random = new Random(7);

    // Back wall with three windows.
    c.DrawRect(0, 0, W, 230, Fill(new SKColor(0xEF, 0xE4, 0xD2)));

    for (var i = 0; i < 3; i++)
    {
        var left = 170 + (i * 430);
        var window = new SKRect(left, 30, left + 300, 190);

        using var sky = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, window.Top), new SKPoint(0, window.Bottom),
                [new SKColor(0x9C, 0xC9, 0xE8), new SKColor(0xE3, 0xF1, 0xF8)], SKShaderTileMode.Clamp),
        };

        c.DrawRect(window, sky);
        c.DrawRect(window, Stroke(new SKColor(0x6B, 0x4F, 0x3A), 10));
        c.DrawLine(window.MidX, window.Top, window.MidX, window.Bottom, Stroke(new SKColor(0x6B, 0x4F, 0x3A), 6));
        c.DrawLine(window.Left, window.MidY, window.Right, window.MidY, Stroke(new SKColor(0x6B, 0x4F, 0x3A), 6));
    }

    c.DrawRect(0, 222, W, 16, Fill(new SKColor(0x8A, 0x6A, 0x4E)));

    // Window-side floor: wooden planks.
    c.DrawRect(0, 238, W, 272, Fill(new SKColor(0xC8, 0x9B, 0x6C)));

    for (var y = 238; y < 510; y += 34)
    {
        c.DrawLine(0, y, W, y, Stroke(new SKColor(0xA9, 0x7E, 0x55), 2));

        for (var x = random.Next(0, 240); x < W; x += 240 + random.Next(0, 120))
        {
            c.DrawLine(x, y, x, Math.Min(y + 34, 510), Stroke(new SKColor(0xA9, 0x7E, 0x55), 2));
        }
    }

    // Terrace: stone tiles, behind a low planter.
    c.DrawRect(0, 520, W, H - 520, Fill(new SKColor(0xDD, 0xD3, 0xC4)));

    for (var y = 520; y < H; y += 80)
    {
        c.DrawLine(0, y, W, y, Stroke(new SKColor(0xC9, 0xBD, 0xAB), 2));
    }

    for (var x = 0; x < W; x += 80)
    {
        c.DrawLine(x, 520, x, H, Stroke(new SKColor(0xC9, 0xBD, 0xAB), 2));
    }

    c.DrawRoundRect(new SKRect(0, 500, W, 540), 10, 10, Fill(new SKColor(0x7A, 0x5C, 0x44)));

    for (var x = 20; x < W; x += 46)
    {
        var r = 20 + random.Next(0, 10);
        c.DrawCircle(x, 505, r, Fill(new SKColor(0x5E, 0x8C, 0x4A)));
        c.DrawCircle(x + 10, 498, r * 0.6f, Fill(new SKColor(0x78, 0xA8, 0x5C)));
    }

    // Tables 1-4: round two-seaters by the windows.
    foreach (var px in new[] { 0.200f, 0.312f, 0.424f, 0.536f })
    {
        RoundTable(c, px * W, 0.366f * H, 54);
    }

    // Tables 5-7: four-seaters on the terrace.
    foreach (var px in new[] { 0.236f, 0.396f, 0.556f })
    {
        RectTable(c, px * W, 0.636f * H, 168, 86, chairsPerSide: 2);
    }

    // Table 8: the long table for ten.
    RectTable(c, 0.756f * W, 0.636f * H, 288, 120, chairsPerSide: 4);

    // A counter in the far corner and two big pots on the terrace, so the room is not only tables.
    c.DrawRoundRect(new SKRect(1180, 250, 1480, 330), 12, 12, Fill(new SKColor(0x6B, 0x4F, 0x3A)));
    c.DrawRoundRect(new SKRect(1190, 258, 1470, 300), 8, 8, Fill(new SKColor(0xE9, 0xDC, 0xC6)));

    foreach (var (x, y) in new[] { (90f, 900f), (1420f, 900f) })
    {
        c.DrawCircle(x, y, 58, Fill(new SKColor(0xB5, 0x6A, 0x3F)));
        c.DrawCircle(x, y, 46, Fill(new SKColor(0x4E, 0x7D, 0x3E)));
        c.DrawCircle(x - 14, y - 12, 22, Fill(new SKColor(0x6E, 0xA0, 0x55)));
    }
}

static void RoundTable(SKCanvas c, float cx, float cy, float r)
{
    var chair = new SKColor(0x4A, 0x3A, 0x2E);

    // Above and below, not beside: the four two-seaters stand closer together than two chairs are wide.
    foreach (var angle in new[] { 270f, 90f })
    {
        var radians = angle * MathF.PI / 180f;
        c.DrawCircle(cx + (MathF.Cos(radians) * (r + 22)), cy + (MathF.Sin(radians) * (r + 22)) + 4, 24, Shadow(6));
        c.DrawCircle(cx + (MathF.Cos(radians) * (r + 22)), cy + (MathF.Sin(radians) * (r + 22)), 24, Fill(chair));
    }

    c.DrawCircle(cx + 5, cy + 8, r, Shadow(10));
    c.DrawCircle(cx, cy, r, Fill(new SKColor(0x9A, 0x6B, 0x43)));
    c.DrawCircle(cx, cy, r - 8, Fill(new SKColor(0xA8, 0x77, 0x4C)));
    c.DrawCircle(cx - 16, cy - 6, 13, Fill(SKColors.White));
    c.DrawCircle(cx + 18, cy + 10, 9, Fill(new SKColor(0xF4, 0xEF, 0xE6)));
    c.DrawCircle(cx + 18, cy + 10, 5, Fill(new SKColor(0x5C, 0x3B, 0x24)));
}

static void RectTable(SKCanvas c, float cx, float cy, float w, float h, int chairsPerSide)
{
    var chair = new SKColor(0x4A, 0x3A, 0x2E);
    var step = w / chairsPerSide;

    for (var i = 0; i < chairsPerSide; i++)
    {
        var x = cx - (w / 2) + (step / 2) + (i * step);

        foreach (var y in new[] { cy - (h / 2) - 22, cy + (h / 2) + 22 })
        {
            var seat = new SKRect(x - 22, y - 16, x + 22, y + 16);
            c.DrawRoundRect(new SKRect(seat.Left, seat.Top + 4, seat.Right, seat.Bottom + 4), 8, 8, Shadow(6));
            c.DrawRoundRect(seat, 8, 8, Fill(chair));
        }
    }

    var top = new SKRect(cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2));

    c.DrawRoundRect(new SKRect(top.Left + 6, top.Top + 10, top.Right + 6, top.Bottom + 10), 14, 14, Shadow(12));
    c.DrawRoundRect(top, 14, 14, Fill(new SKColor(0x8C, 0x5E, 0x3A)));
    c.DrawRoundRect(new SKRect(top.Left + 8, top.Top + 8, top.Right - 8, top.Bottom - 8), 10, 10, Fill(new SKColor(0x9E, 0x6C, 0x44)));

    for (var i = 0; i < chairsPerSide; i++)
    {
        var x = cx - (w / 2) + (step / 2) + (i * step);
        c.DrawCircle(x, cy - (h / 4), 11, Fill(SKColors.White));
        c.DrawCircle(x, cy + (h / 4), 11, Fill(SKColors.White));
    }
}

// ------------------------------------------------------------------ gallery 1: breakfast from above

static void DrawBreakfast(SKCanvas c)
{
    var random = new Random(11);

    c.DrawRect(0, 0, W, H, Fill(new SKColor(0xB0, 0x82, 0x57)));

    for (var y = 0; y < H; y += 110)
    {
        c.DrawRect(0, y, W, 106, Fill(new SKColor((byte)(0xB0 + random.Next(-8, 8)), (byte)(0x82 + random.Next(-8, 8)), 0x57)));
        c.DrawLine(0, y + 107, W, y + 107, Stroke(new SKColor(0x8E, 0x66, 0x42), 5));

        for (var g = 0; g < 6; g++)
        {
            var gy = y + 15 + random.Next(0, 80);
            c.DrawLine(random.Next(0, W), gy, random.Next(0, W), gy + random.Next(-3, 3), Stroke(new SKColor(0xA2, 0x76, 0x4D), 2));
        }
    }

    // A linen napkin.
    c.Save();
    c.RotateDegrees(-8, 380, 520);
    c.DrawRoundRect(new SKRect(120, 180, 640, 860), 18, 18, Shadow(14));
    c.DrawRoundRect(new SKRect(110, 170, 630, 850), 18, 18, Fill(new SKColor(0xEE, 0xE6, 0xD8)));

    for (var x = 150; x < 620; x += 40)
    {
        c.DrawLine(x, 170, x, 850, Stroke(new SKColor(0xE0, 0xD5, 0xC2), 3));
    }

    c.Restore();

    // The plate, with a boat-shaped cheese bread and its yolk.
    c.DrawCircle(760, 520, 330, Shadow(22));
    c.DrawCircle(750, 505, 330, Fill(new SKColor(0xFA, 0xF8, 0xF4)));
    c.DrawCircle(750, 505, 280, Stroke(new SKColor(0xE6, 0xE1, 0xD8), 6));

    using (var boat = new SKPath())
    {
        boat.MoveTo(520, 505);
        boat.QuadTo(750, 300, 980, 505);
        boat.QuadTo(750, 710, 520, 505);
        boat.Close();

        c.DrawPath(boat, Fill(new SKColor(0xC9, 0x8A, 0x3E)));
    }

    c.DrawOval(new SKRect(600, 430, 900, 580), Fill(new SKColor(0xF3, 0xD9, 0x7A)));
    c.DrawCircle(750, 505, 52, Fill(new SKColor(0xF6, 0xB0, 0x1E)));
    c.DrawCircle(736, 490, 14, Fill(new SKColor(0xFB, 0xD3, 0x6B)));

    // Coffee.
    c.DrawCircle(1230, 250, 130, Shadow(16));
    c.DrawCircle(1220, 240, 130, Fill(new SKColor(0xF4, 0xF1, 0xEA)));
    c.DrawCircle(1220, 240, 92, Fill(SKColors.White));
    c.DrawCircle(1220, 240, 74, Fill(new SKColor(0x6B, 0x3E, 0x22)));
    c.DrawOval(new SKRect(1180, 215, 1250, 255), Fill(new SKColor(0xC8, 0x9B, 0x6C)));
    c.DrawRoundRect(new SKRect(1300, 222, 1360, 258), 16, 16, Fill(SKColors.White));

    // A bowl of greens and tomatoes.
    c.DrawCircle(1230, 740, 150, Shadow(16));
    c.DrawCircle(1220, 730, 150, Fill(new SKColor(0x3F, 0x6E, 0x8C)));
    c.DrawCircle(1220, 730, 124, Fill(new SKColor(0x6E, 0xA0, 0x55)));

    for (var i = 0; i < 14; i++)
    {
        var angle = i * 0.9;
        var distance = 30 + (i * 5);
        c.DrawCircle(
            1220 + (float)(Math.Cos(angle) * distance),
            730 + (float)(Math.Sin(angle) * distance),
            22,
            Fill(i % 3 == 0 ? new SKColor(0xD9, 0x4B, 0x3B) : new SKColor(0x8C, 0xBF, 0x6A)));
    }
}

// ------------------------------------------------------------------ gallery 2: the terrace at dusk

static void DrawTerrace(SKCanvas c)
{
    using (var sky = new SKPaint
           {
               IsAntialias = true,
               Shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, 700),
                   [new SKColor(0x2B, 0x3A, 0x67), new SKColor(0x8E, 0x5B, 0x8A), new SKColor(0xF2, 0xA6, 0x5A)],
                   [0f, 0.6f, 1f],
                   SKShaderTileMode.Clamp),
           })
    {
        c.DrawRect(0, 0, W, 700, sky);
    }

    // Distant rooftops.
    var roofs = new SKColor(0x3A, 0x2F, 0x45);
    var random = new Random(3);

    for (var x = 0; x < W; x += 90 + random.Next(0, 60))
    {
        var height = 90 + random.Next(0, 140);
        c.DrawRect(x, 700 - height, 80 + random.Next(0, 50), height, Fill(roofs));
    }

    // The terrace floor and railing.
    c.DrawRect(0, 700, W, 300, Fill(new SKColor(0x5A, 0x44, 0x3A)));
    c.DrawRect(0, 640, W, 14, Fill(new SKColor(0x2A, 0x20, 0x22)));

    for (var x = 20; x < W; x += 60)
    {
        c.DrawRect(x, 654, 8, 50, Fill(new SKColor(0x2A, 0x20, 0x22)));
    }

    // String lights on two sagging wires.
    foreach (var (startY, sag) in new[] { (120f, 140f), (60f, 110f) })
    {
        using var wire = new SKPath();
        wire.MoveTo(0, startY);
        wire.QuadTo(W / 2f, startY + (sag * 2), W, startY);
        c.DrawPath(wire, Stroke(new SKColor(0x1E, 0x18, 0x1C), 3));

        using var measure = new SKPathMeasure(wire);

        for (var d = 30f; d < measure.Length; d += 70f)
        {
            if (measure.GetPosition(d, out var point))
            {
                using var glow = new SKPaint
                {
                    Color = new SKColor(0xFF, 0xD2, 0x7A, 120),
                    IsAntialias = true,
                    ImageFilter = SKImageFilter.CreateBlur(12, 12),
                };

                c.DrawCircle(point.X, point.Y + 14, 20, glow);
                c.DrawCircle(point.X, point.Y + 14, 8, Fill(new SKColor(0xFF, 0xE6, 0xA8)));
            }
        }
    }

    // Tables and chairs in silhouette.
    var dark = new SKColor(0x22, 0x1A, 0x1D);

    foreach (var x in new[] { 260f, 750f, 1240f })
    {
        c.DrawOval(new SKRect(x - 150, 905, x + 150, 945), Shadow(10));
        c.DrawRoundRect(new SKRect(x - 140, 780, x + 140, 800), 6, 6, Fill(dark));
        c.DrawRect(x - 8, 800, 16, 120, Fill(dark));
        c.DrawRect(x - 60, 912, 120, 10, Fill(dark));

        foreach (var side in new[] { -1f, 1f })
        {
            var chairX = x + (side * 200);
            c.DrawRect(chairX - 40, 840, 80, 12, Fill(dark));
            c.DrawRect(chairX + (side * 32) - 5, 740, 10, 110, Fill(dark));
            c.DrawRect(chairX - 38, 852, 8, 80, Fill(dark));
            c.DrawRect(chairX + 30, 852, 8, 80, Fill(dark));
        }

        // A candle on each table.
        using var flame = new SKPaint
        {
            Color = new SKColor(0xFF, 0xC8, 0x5A, 160),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(10, 10),
        };

        c.DrawCircle(x, 760, 22, flame);
        c.DrawRect(x - 7, 758, 14, 22, Fill(new SKColor(0xF4, 0xEF, 0xE6)));
        c.DrawOval(new SKRect(x - 4, 742, x + 4, 758), Fill(new SKColor(0xFF, 0xD9, 0x80)));
    }
}
