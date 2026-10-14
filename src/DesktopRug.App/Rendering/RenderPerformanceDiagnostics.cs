using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

/// <summary>Repeatable CPU rendering and pixel-comparison checks; creates no windows.</summary>
internal static class RenderPerformanceDiagnostics
{
    internal static Dictionary<string, object> Run(string output)
    {
        Directory.CreateDirectory(output);
        var report = new Dictionary<string, object> { ["avx2"] = Avx2.IsSupported };
        var cloth = new ClothSimulation { Viewport = new(1000, 760) };
        cloth.Reset(new(500, 380), new(700, 490), -5);
        using var renderer = new RugRenderer(cloth, new());
        using var reference = new SKBitmap(1000, 760);
        using var actual = new SKBitmap(1000, 760);
        using var referenceCanvas = new SKCanvas(reference);
        using var actualCanvas = new SKCanvas(actual);
        bool passed = true;
        Check("flat");
        var corner = RugGeometry.Project(cloth.Particles[0].Position);
        cloth.BeginGrab(0, corner, 140);
        for (int i = 0; i < 120; i++) { cloth.SetGrabTarget(corner + new Vector2(i * 2, i * 1.5f), 140); cloth.Step(); }
        Check("fold");

        // A monitor-local canvas sees the same world through a negative offset and DPI scale.
        referenceCanvas.Clear(SKColors.Transparent); actualCanvas.Clear(SKColors.Transparent);
        referenceCanvas.Save(); actualCanvas.Save();
        referenceCanvas.Scale(1.5f); referenceCanvas.Translate(-450, -260);
        actualCanvas.Scale(1.5f); actualCanvas.Translate(-450, -260);
        referenceCanvas.ClipRect(new(20, 15, 600, 450)); actualCanvas.ClipRect(new(20, 15, 600, 450));
        Check("scaled-negative-clip");
        referenceCanvas.Restore(); actualCanvas.Restore();

        // The scalar path is used on machines without AVX2; compare with the accelerated path.
        renderer.ReferenceMaterialEnabled = false; renderer.AdaptiveMeshEnabled = true;
        renderer.VectorAccelerationEnabled = false; Draw(referenceCanvas);
        renderer.VectorAccelerationEnabled = true; Draw(actualCanvas);
        var scalarDifference = Difference(reference, actual);
        Save(reference, Path.Combine(output, "scalar.png")); Save(actual, Path.Combine(output, "vector.png"));
        report["scalarVectorDifference"] = scalarDifference;
        passed &= scalarDifference.SignificantAlphaMismatchFraction < .001 && scalarDifference.MeanChannelDifference < 1;

        using var whole = new SKBitmap(1000, 760); using var wholeCanvas = new SKCanvas(whole);
        wholeCanvas.Clear(SKColors.Transparent); renderer.Draw(wholeCanvas);
        using var stitched = new SKBitmap(1000, 760); using var stitchedCanvas = new SKCanvas(stitched);
        stitchedCanvas.Clear(SKColors.Transparent);
        using var firstMonitor = new SKBitmap(500, 760); using var firstCanvas = new SKCanvas(firstMonitor);
        using var secondMonitor = new SKBitmap(500, 760); using var secondCanvas = new SKCanvas(secondMonitor);
        secondCanvas.Translate(-500, 0);
        for (int monitor = 0; monitor < 2; monitor++)
        {
            var half = monitor == 0 ? firstMonitor : secondMonitor; var halfCanvas = monitor == 0 ? firstCanvas : secondCanvas;
            halfCanvas.Clear(SKColors.Transparent); renderer.Draw(halfCanvas);
            stitchedCanvas.DrawBitmap(half, monitor * 500, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        var seamDifference = Difference(whole, stitched);
        report["splitMonitorDifference"] = seamDifference;
        passed &= seamDifference.SignificantAlphaMismatchFraction < .001 && seamDifference.MeanChannelDifference < 1;
        double[] splitTimes = new double[90];
        for (int frame = -12; frame < splitTimes.Length; frame++)
        {
            long start = Stopwatch.GetTimestamp(); Draw(firstCanvas); Draw(secondCanvas);
            if (frame >= 0) splitTimes[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        report["splitMonitorTiming"] = Timing(splitTimes);
        report["renderChecksPassed"] = passed;
        report["scope"] = "Offscreen CPU raster timing only; excludes real WPF/DWM presentation, input and physics.";
        return report;

        void Draw(SKCanvas canvas) { canvas.Clear(SKColors.Transparent); renderer.Draw(canvas); }
        void Check(string name)
        {
            renderer.ReferenceMaterialEnabled = true; renderer.AdaptiveMeshEnabled = false; Draw(referenceCanvas);
            renderer.ReferenceMaterialEnabled = false; renderer.AdaptiveMeshEnabled = true; Draw(actualCanvas);
            var difference = Difference(reference, actual);
            report[name + "Difference"] = difference;
            passed &= difference.SignificantAlphaMismatchFraction < .005 && difference.MeanChannelDifference < 5;
            Save(reference, Path.Combine(output, name + "-reference.png"));
            Save(actual, Path.Combine(output, name + ".png"));
            double[] referenceTimes = new double[90], actualTimes = new double[90];
            for (int frame = -12; frame < actualTimes.Length; frame++)
            {
                renderer.ReferenceMaterialEnabled = true; renderer.AdaptiveMeshEnabled = false;
                long start = Stopwatch.GetTimestamp(); Draw(referenceCanvas);
                if (frame >= 0) referenceTimes[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                renderer.ReferenceMaterialEnabled = false; renderer.AdaptiveMeshEnabled = true;
                start = Stopwatch.GetTimestamp(); Draw(actualCanvas);
                if (frame >= 0) actualTimes[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            report[name + "ReferenceTiming"] = Timing(referenceTimes);
            report[name + "Timing"] = Timing(actualTimes);
            report[name + "Triangles"] = renderer.LastTriangleCount;
        }
    }

    private static object Timing(double[] values)
    {
        Array.Sort(values);
        return new { MeanMs = values.Average(), P95Ms = values[(int)(values.Length * .95)], Samples = values.Length };
    }

    private sealed record PixelDifference(double MeanChannelDifference, double AlphaMismatchFraction, double SignificantAlphaMismatchFraction, int MaximumChannelDifference);
    private static PixelDifference Difference(SKBitmap reference, SKBitmap actual)
    {
        ReadOnlySpan<uint> expected = MemoryMarshal.Cast<byte, uint>(reference.GetPixelSpan());
        ReadOnlySpan<uint> result = MemoryMarshal.Cast<byte, uint>(actual.GetPixelSpan());
        long difference = 0; int pixels = 0, alphaMismatch = 0, significantAlphaMismatch = 0, maximum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            uint a = expected[i], b = result[i];
            if (a >> 24 == 0 && b >> 24 == 0) continue;
            pixels++;
            if (a >> 24 != b >> 24) alphaMismatch++;
            if (Math.Abs((int)(a >> 24) - (int)(b >> 24)) >= 8) significantAlphaMismatch++;
            for (int shift = 0; shift < 24; shift += 8)
            {
                int delta = Math.Abs((int)(a >> shift & 255) - (int)(b >> shift & 255));
                difference += delta; maximum = Math.Max(maximum, delta);
            }
        }
        return new(difference / (double)Math.Max(1, pixels * 3), alphaMismatch / (double)Math.Max(1, pixels), significantAlphaMismatch / (double)Math.Max(1, pixels), maximum);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path); data.SaveTo(stream);
    }
}
