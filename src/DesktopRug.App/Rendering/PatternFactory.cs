using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

internal static class PatternFactory
{
    internal static SKBitmap Create(RugSettings settings)
    {
        const int width = 1400, height = 980;
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        if (settings.Pattern == RugPattern.Custom && settings.CustomTexturePath is { } path)
        {
            // Decode once at bounded resolution. Never modify or copy the user's original image.
            using var codec = SKCodec.Create(path) ?? throw new IOException("The selected image cannot be decoded.");
            if ((long)codec.Info.Width * codec.Info.Height > 80_000_000) throw new IOException("Choose an image smaller than 80 megapixels.");
            using var source = SKBitmap.Decode(codec);
            if (source is null) throw new IOException("The selected image cannot be decoded.");
            float scale = Math.Min(width / (float)source.Width, height / (float)source.Height);
            float w = source.Width * scale, h = source.Height * scale;
            canvas.Clear(new SKColor(38, 32, 29));
            canvas.DrawBitmap(source, new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), new SKSamplingOptions(SKFilterMode.Linear));
            Weave(canvas, width, height, settings.TextureDetail);
            return bitmap;
        }
        bool modern = settings.Pattern == RugPattern.Modern, geometric = settings.Pattern == RugPattern.Tabriz;
        SKColor red = geometric ? new(29, 82, 88) : new(115, 33, 43);
        SKColor ink = new(25, 38, 54), cream = new(227, 205, 164), gold = new(187, 145, 85);
        canvas.Clear(modern ? new SKColor(202, 190, 166) : red);
        using var fill = new SKPaint { IsAntialias = true };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        void Frame(float inset, float thickness, SKColor color)
        {
            line.Color = color; line.StrokeWidth = thickness;
            canvas.DrawRect(new SKRect(inset, inset, width - inset, height - inset), line);
        }
        Frame(9, 18, ink); Frame(24, 8, cream); Frame(49, 34, red); Frame(72, 9, gold);
        Frame(116, 75, ink); Frame(158, 8, cream); Frame(174, 17, red); Frame(187, 5, gold);
        if (modern)
        {
            canvas.Clear(new SKColor(205, 193, 169));
            Frame(16, 14, new(110, 103, 88)); Frame(47, 3, new(153, 137, 112));
            for (int i = 0; i < 8; i++)
            {
                fill.Color = i % 2 == 0 ? new(53, 72, 72) : new(158, 90, 66);
                float x = 150 + i * 160;
                using var arch = new SKPathBuilder(); arch.MoveTo(x, 740); arch.LineTo(x, 420);
                arch.CubicTo(x, 170, x + 110, 170, x + 110, 420); arch.LineTo(x + 110, 740); arch.Close();
                using var archPath = arch.Detach(); canvas.DrawPath(archPath, fill);
            }
        }
        else
        {
            // Original rosette vines in the border, reflected to produce woven symmetry.
            for (int x = 120; x < width - 80; x += 68)
            {
                Flower(canvas, x, 112, 19, gold, red); Flower(canvas, x, height - 112, 19, gold, red);
                Flower(canvas, x, 46, 7, cream, ink); Flower(canvas, x, height - 46, 7, cream, ink);
            }
            for (int y = 180; y < height - 150; y += 62)
            {
                Flower(canvas, 112, y, 18, gold, red); Flower(canvas, width - 112, y, 18, gold, red);
                Flower(canvas, 46, y, 7, cream, ink); Flower(canvas, width - 46, y, 7, cream, ink);
            }
            for (int y = 230; y < height - 200; y += 66)
                for (int x = 230; x < width - 200; x += 74)
                {
                    float dx = (x - width / 2f) / 340, dy = (y - height / 2f) / 245;
                    if (Math.Abs(dx) + Math.Abs(dy) < 1.05f) continue;
                    if (geometric) Diamond(canvas, x, y, 17, 24, cream, ink);
                    else
                    {
                        Flower(canvas, x + (y % 132 == 32 ? 18 : 0), y, 11, gold, ink);
                        line.Color = gold.WithAlpha(160); line.StrokeWidth = 1.5f;
                        using var vine = new SKPathBuilder(); vine.MoveTo(x - 20, y + 24); vine.CubicTo(x - 5, y - 30, x + 30, y + 30, x + 35, y - 24);
                        using var vinePath = vine.Detach(); canvas.DrawPath(vinePath, line);
                    }
                }
            if (geometric)
            {
                for (int x = 390; x <= 1010; x += 310)
                {
                    Diamond(canvas, x, 490, 160, 260, gold, ink);
                    Diamond(canvas, x, 490, 128, 220, cream, red);
                    Diamond(canvas, x, 490, 92, 165, gold, ink);
                    Flower(canvas, x, 490, 43, cream, red);
                }
            }
            else
            {
                Medallion(canvas, 700, 490, 308, 217, gold);
                Medallion(canvas, 700, 490, 284, 195, cream);
                Medallion(canvas, 700, 490, 260, 178, ink);
                Medallion(canvas, 700, 490, 164, 128, red);
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * MathF.Tau / 16;
                    Flower(canvas, 700 + MathF.Cos(angle) * 210, 490 + MathF.Sin(angle) * 145, 13, gold, red);
                }
                Flower(canvas, 700, 490, 72, cream, gold);
                Flower(canvas, 700, 490, 34, red, ink);
                Flower(canvas, 700, 258, 18, cream, ink); Flower(canvas, 700, 722, 18, cream, ink);
            }
        }
        Weave(canvas, width, height, settings.TextureDetail);
        return bitmap;
    }

    private static void Flower(SKCanvas canvas, float x, float y, float radius, SKColor petals, SKColor center)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = petals };
        canvas.Save(); canvas.Translate(x, y);
        for (int i = 0; i < 8; i++)
        {
            canvas.RotateDegrees(45);
            canvas.DrawOval(0, radius * .47f, radius * .22f, radius * .52f, paint);
        }
        paint.Color = center; canvas.DrawCircle(0, 0, radius * .31f, paint);
        paint.Color = petals; canvas.DrawCircle(0, 0, radius * .12f, paint);
        canvas.Restore();
    }
    private static void Diamond(SKCanvas canvas, float x, float y, float rx, float ry, SKColor outer, SKColor inner)
    {
        using var builder = new SKPathBuilder(); builder.MoveTo(x, y - ry); builder.LineTo(x + rx, y); builder.LineTo(x, y + ry); builder.LineTo(x - rx, y); builder.Close();
        using var path = builder.Detach();
        using var p = new SKPaint { IsAntialias = true, Color = inner }; canvas.DrawPath(path, p);
        p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 5; p.Color = outer; canvas.DrawPath(path, p);
    }
    private static void Medallion(SKCanvas canvas, float x, float y, float rx, float ry, SKColor color)
    {
        using var path = new SKPathBuilder();
        for (int i = 0; i < 128; i++)
        {
            float angle = i * MathF.Tau / 128, radius = 1 + .055f * MathF.Cos(16 * angle);
            var point = new SKPoint(x + MathF.Cos(angle) * rx * radius, y + MathF.Sin(angle) * ry * radius);
            if (i == 0) path.MoveTo(point); else path.LineTo(point);
        }
        path.Close(); using var renderedPath = path.Detach(); using var paint = new SKPaint { Color = color, IsAntialias = true }; canvas.DrawPath(renderedPath, paint);
    }
    private static void Weave(SKCanvas canvas, int width, int height, float detail)
    {
        using var p = new SKPaint { StrokeWidth = 1 };
        for (int y = 0; y < height; y += 3)
        {
            p.Color = new(255, 239, 205, (byte)(23 * detail)); canvas.DrawLine(0, y, width, y, p);
            p.Color = new(12, 9, 8, (byte)(40 * detail)); canvas.DrawLine(0, y + 1, width, y + 1, p);
        }
        var random = new Random(729);
        for (int x = 0; x < width; x += 3)
        {
            p.Color = new(255, 244, 221, (byte)((random.Next(12) + 10) * detail)); canvas.DrawLine(x, 0, x, height, p);
        }
        for (int i = 0; i < 48000; i++)
        {
            p.Color = new(16, 11, 9, (byte)(random.Next(8, 40) * detail));
            canvas.DrawPoint(random.Next(width), random.Next(height), p);
        }
    }
}
