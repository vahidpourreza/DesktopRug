using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopRug.App.Rendering;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Services;

/// <summary>CPU bitmap and simulated-cloth checks only. Creates no windows and captures no desktop pixels.</summary>
internal static class MaterialDiagnostics
{
    internal static Dictionary<string, object> Run(string output)
    {
        Directory.CreateDirectory(output);
        var results = new Dictionary<string, object>
        {
            ["scope"] = "Embedded material decoding and Skia renders of simulated flat, folded, rolled, and unrolled cloth; no desktop screenshots or hardware interaction.",
            ["physicalInteractionVerified"] = false
        };
        var failures = new List<string>();
        var textureHashes = new Dictionary<string, string>();
        var flatHashes = new Dictionary<string, string>();
        var materials = new[]
        {
            (Pattern: RugPattern.Kashan, Slug: "rich-red-persian"),
            (Pattern: RugPattern.Tabriz, Slug: "light-ivory-persian"),
            (Pattern: RugPattern.Modern, Slug: "neutral-tonal")
        };
        void Check(string name, bool passed)
        {
            results[name] = passed;
            if (!passed) failures.Add(name);
        }

        try
        {
            foreach (var material in materials)
            {
                string slug = material.Slug;
                var settings = new RugSettings { Pattern = material.Pattern, TextureDetail = 1 };
                string? previousDetailHash = null;
                foreach (int detail in new[] { 0, 1 })
                {
                    string key = $"{slug}.detail{detail}";
                    var appearance = settings with { TextureDetail = detail };
                    using var texture = PatternFactory.Create(appearance);
                    using var repeat = PatternFactory.Create(appearance);
                    using var unrelatedCustom = PatternFactory.Create(appearance with
                    { CustomTexturePath = Path.Combine(output, "intentionally-missing-custom-image.png") });
                    string hash = PixelHash(texture);
                    results[key + ".width"] = texture.Width;
                    results[key + ".height"] = texture.Height;
                    results[key + ".sha256"] = hash;
                    Check(key + ".aspectMatchesMaterial", Math.Abs(texture.Width / (double)texture.Height - settings.AspectRatio) < .01);
                    Check(key + ".sourceResolutionPreserved", Math.Max(texture.Width, texture.Height) >= 900);
                    Check(key + ".repeatPixelsStable", hash == PixelHash(repeat));
                    Check(key + ".ignoresUnrelatedCustomPath", hash == PixelHash(unrelatedCustom));
                    Check(key + ".opaque", IsOpaque(texture));
                    var colors = ColorStatistics(texture);
                    results[key + ".sampledUniqueColors"] = colors.Unique;
                    results[key + ".sampledLuminanceRange"] = colors.Range;
                    Check(key + ".hasWovenColorVariation", colors.Unique >= 64 && colors.Range >= 20);
                    Save(texture, Path.Combine(output, $"{slug}-texture-detail-{detail}.png"));
                    if (detail == 0) textureHashes[slug] = hash;
                    else Check(slug + ".detailControlChangesPixels", hash != previousDetailHash);
                    previousDetailHash = hash;
                }

                const int canvasWidth = 1000, canvasHeight = 900;
                float width = Math.Min(720, 660 * settings.AspectRatio);
                Vector2 size = new(width, width / settings.AspectRatio), center = new(canvasWidth / 2f, canvasHeight / 2f);
                var cloth = new ClothSimulation(36, 26) { Viewport = new(canvasWidth, canvasHeight) };
                cloth.Reset(center, size, -4);
                using var renderer = new RugRenderer(cloth, settings);
                flatHashes[slug] = Render("flat");
                float flatHeight = ProjectedHeight(cloth);
                Check(slug + ".flatMaterialAspectPreserved", Math.Abs(cloth.MaterialSize.X / cloth.MaterialSize.Y - settings.AspectRatio) < .0001f);

                Vector2 corner = RugGeometry.Project(cloth.Particles[0].Position);
                cloth.BeginGrab(0, corner, 120, 50);
                for (int step = 0; step < 100; step++)
                {
                    float amount = step / 99f;
                    cloth.SetGrabTarget(corner + new Vector2(size.X * .50f * amount, size.Y * .20f * amount), 140);
                    cloth.Step();
                }
                string foldHash = Render("fold");
                Check(slug + ".foldRaisesFabric", cloth.Particles.Max(p => p.Position.Z) > 35);
                Check(slug + ".foldChangesRenderedShape", foldHash != flatHashes[slug]);
                cloth.EndGrab();

                // Reset between independent fixtures; the roll animation then uses the same material dimensions.
                cloth.Reset(center, size, -4);
                cloth.SetRolled(true);
                for (int step = 0; step < 120; step++) cloth.Step();
                Render("roll");
                if (settings.AspectRatio < 1) Check(slug + ".buriedFringeOccluded", renderer.LastOccludedFringeStrands > 0);
                Check(slug + ".rollCompletes", cloth.IsRolled && !cloth.IsRolling && cloth.RollAmount == 1);
                Check(slug + ".rollRetainsMaterialDimensions", cloth.MaterialSize == size);
                Check(slug + ".rollCompactsProjection", ProjectedHeight(cloth) < flatHeight * .65f);

                cloth.SetRolled(false);
                for (int step = 0; step < 120; step++) cloth.Step();
                Render("unroll");
                Check(slug + ".unrollCompletes", !cloth.IsRolled && !cloth.IsRolling && cloth.RollAmount == 0);
                Check(slug + ".unrollRestoresDimensions", cloth.MaterialSize == size);
                Check(slug + ".unrollRestoresProjection", Math.Abs(ProjectedHeight(cloth) - flatHeight) < 1);

                string Render(string state)
                {
                    string key = $"{slug}.{state}";
                    using var bitmap = new SKBitmap(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Clear(SKColors.Transparent); renderer.Draw(canvas, 1); canvas.Flush();
                    string hash = PixelHash(bitmap);
                    results[key + ".sha256"] = hash;
                    Check(key + ".finiteGeometry", cloth.Particles.All(p =>
                        float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z)));
                    Check(key + ".transparentOutside", BorderIsTransparent(bitmap));
                    Check(key + ".visibleMaterial", SampleVisiblePixels(bitmap) > 24);
                    Check(key + ".unclippedBounds", renderer.LastBounds.Left >= 0 && renderer.LastBounds.Top >= 0
                        && renderer.LastBounds.Right < canvasWidth && renderer.LastBounds.Bottom < canvasHeight);
                    Save(bitmap, Path.Combine(output, $"{slug}-{state}.png"));
                    canvas.Clear(SKColors.Transparent); renderer.Draw(canvas, 1); canvas.Flush();
                    Check(key + ".repeatRenderStable", hash == PixelHash(bitmap));
                    return hash;
                }
            }
            Check("allThreeMaterialTexturesDistinct", textureHashes.Count == 3 && textureHashes.Values.Distinct(StringComparer.Ordinal).Count() == 3);
            Check("allThreeFlatRendersDistinct", flatHashes.Count == 3 && flatHashes.Values.Distinct(StringComparer.Ordinal).Count() == 3);
        }
        catch (Exception exception)
        {
            results["error"] = exception.ToString();
            failures.Add("materialDiagnosticsException");
        }
        results["failedChecks"] = failures;
        results["materialChecksPassed"] = failures.Count == 0;
        File.WriteAllText(Path.Combine(output, "materials.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results;
    }

    private static string PixelHash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan())).ToLowerInvariant();
    private static bool IsOpaque(SKBitmap bitmap)
    {
        if (bitmap.BytesPerPixel != 4) return false;
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (pixels[y * bitmap.RowBytes + x * 4 + 3] != 255) return false;
        return true;
    }
    private static (int Unique, int Range) ColorStatistics(SKBitmap bitmap)
    {
        HashSet<uint> colors = [];
        int darkest = 255, lightest = 0;
        for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 64))
            for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 64))
            {
                SKColor color = bitmap.GetPixel(x, y);
                colors.Add((uint)color);
                int light = (color.Red * 54 + color.Green * 183 + color.Blue * 19) / 256;
                darkest = Math.Min(darkest, light); lightest = Math.Max(lightest, light);
            }
        return (colors.Count, lightest - darkest);
    }
    private static bool BorderIsTransparent(SKBitmap bitmap)
    {
        for (int x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, 0).Alpha != 0 || bitmap.GetPixel(x, bitmap.Height - 1).Alpha != 0) return false;
        for (int y = 0; y < bitmap.Height; y++)
            if (bitmap.GetPixel(0, y).Alpha != 0 || bitmap.GetPixel(bitmap.Width - 1, y).Alpha != 0) return false;
        return true;
    }
    private static int SampleVisiblePixels(SKBitmap bitmap)
    {
        int visible = 0;
        for (int y = 0; y < bitmap.Height; y += 8)
            for (int x = 0; x < bitmap.Width; x += 8)
                if (bitmap.GetPixel(x, y).Alpha > 180) visible++;
        return visible;
    }
    private static float ProjectedHeight(ClothSimulation cloth)
    {
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        foreach (var particle in cloth.Particles)
        {
            float y = RugGeometry.Project(particle.Position).Y;
            low = Math.Min(low, y); high = Math.Max(high, y);
        }
        return high - low;
    }
    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        image.SaveTo(stream);
    }
}
