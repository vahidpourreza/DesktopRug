using System.Runtime.InteropServices;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

internal static class PatternFactory
{
    // The original reference PNGs are embedded unchanged. These four points identify
    // the fabric body, excluding the photographed floor and fringe. Sampling the slight
    // quadrilateral straightens its edges without inventing or redrawing the ornament.
    private sealed record Material(string File, int Width, int Height, SKPoint TopLeft, SKPoint TopRight, SKPoint BottomRight, SKPoint BottomLeft);
    private static Material Reference(RugPattern pattern) => pattern switch
    {
        RugPattern.Tabriz => new("light-ivory-persian", 966, 1380, new(37, 76), new(984, 76), new(987, 1443), new(35, 1444)),
        RugPattern.Modern => new("neutral-tonal", 1323, 980, new(60, 62), new(1370, 62), new(1370, 1037), new(59, 1038)),
        _ => new("rich-red-persian", 1020, 1275, new(59, 47), new(1062, 46), new(1073, 1319), new(49, 1320))
    };

    internal static SKBitmap Create(RugSettings settings)
    {
        if (settings.Pattern == RugPattern.Custom)
        {
            if (settings.CustomTexturePath is not { } path) throw new IOException("Choose a local image before selecting Custom.");
            return CreateCustom(path, settings.TextureDetail);
        }
        Material material = Reference(settings.Pattern);
        using var stream = typeof(PatternFactory).Assembly.GetManifestResourceStream($"DesktopRug.Materials.{material.File}.png")
            ?? throw new IOException("The built-in rug material is unavailable.");
        using var decoded = SKBitmap.Decode(stream) ?? throw new IOException("The built-in rug material cannot be decoded.");
        using var source = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(source)) canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        var bitmap = new SKBitmap(material.Width, material.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try
        {
            ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
            int sourceStride = source.RowBytes / 4, outputStride = bitmap.RowBytes / 4;
            for (int y = 0; y < bitmap.Height; y++)
            {
                float v = y / (float)(bitmap.Height - 1);
                float leftX = Mix(material.TopLeft.X, material.BottomLeft.X, v);
                float leftY = Mix(material.TopLeft.Y, material.BottomLeft.Y, v);
                float rightX = Mix(material.TopRight.X, material.BottomRight.X, v);
                float rightY = Mix(material.TopRight.Y, material.BottomRight.Y, v);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    float u = x / (float)(bitmap.Width - 1);
                    float sx = Math.Clamp(Mix(leftX, rightX, u), 0, source.Width - 1f);
                    float sy = Math.Clamp(Mix(leftY, rightY, u), 0, source.Height - 1f);
                    int ix = (int)sx, iy = (int)sy, nextX = Math.Min(ix + 1, source.Width - 1), nextY = Math.Min(iy + 1, source.Height - 1);
                    uint tx = (uint)((sx - ix) * 256), ty = (uint)((sy - iy) * 256);
                    uint top = MixPixel(pixels[iy * sourceStride + ix], pixels[iy * sourceStride + nextX], tx);
                    uint bottom = MixPixel(pixels[nextY * sourceStride + ix], pixels[nextY * sourceStride + nextX], tx);
                    output[y * outputStride + x] = MixPixel(top, bottom, ty);
                }
            }
            bitmap.NotifyPixelsChanged();
            // Reference photos already carry fine pile detail. Add only a subtle fibre
            // response, never the coarse grid used by the earlier procedural materials.
            using (var canvas = new SKCanvas(bitmap)) Weave(canvas, bitmap.Width, bitmap.Height, settings.TextureDetail * .12f);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
    private static uint MixPixel(uint a, uint b, uint amount)
    {
        uint inverse = 256 - amount;
        uint rb = ((((a & 0x00ff00ff) * inverse) + ((b & 0x00ff00ff) * amount)) >> 8) & 0x00ff00ff;
        uint g = ((((a & 0x0000ff00) * inverse) + ((b & 0x0000ff00) * amount)) >> 8) & 0x0000ff00;
        return 0xff000000 | rb | g;
    }

    private static SKBitmap CreateCustom(string path, float detail)
    {
        const int width = 1400, height = 980;
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try
        {
            using var codec = SKCodec.Create(path) ?? throw new IOException("The selected image cannot be decoded.");
            if ((long)codec.Info.Width * codec.Info.Height > 80_000_000) throw new IOException("Choose an image smaller than 80 megapixels.");
            using var source = SKBitmap.Decode(codec) ?? throw new IOException("The selected image cannot be decoded.");
            using var canvas = new SKCanvas(bitmap);
            float scale = Math.Min(width / (float)source.Width, height / (float)source.Height);
            float w = source.Width * scale, h = source.Height * scale;
            canvas.Clear(new SKColor(38, 32, 29));
            canvas.DrawBitmap(source, new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), new SKSamplingOptions(SKFilterMode.Linear));
            Weave(canvas, width, height, detail);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void Weave(SKCanvas canvas, int width, int height, float detail)
    {
        detail = float.IsFinite(detail) ? Math.Clamp(detail, 0, 1) : 0;
        if (detail == 0) return;
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
