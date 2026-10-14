using System.Diagnostics;
using System.Numerics;
using DesktopRug.App.Rendering;
using DesktopRug.App.Windows;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

/// <summary>Measures the awake WPF composition loop, simulation, shaped input and rendering off-screen.</summary>
internal static class FramePipelineDiagnostics
{
    internal static async Task<Dictionary<string, object>> RunAsync(RugPattern pattern = RugPattern.Kashan)
    {
        var display = new DisplayInfo("Offscreen pipeline", new(-28000, -30000, 1000, 760), default, 1, false);
        var cloth = new ClothSimulation { Viewport = display.SizeDip };
        var settings = new RugSettings { Pattern = pattern };
        float width = Math.Min(640, 620 * settings.AspectRatio);
        cloth.Reset(new(500, 380), new(width, width / settings.AspectRatio), -5);
        using var renderer = new RugRenderer(cloth, settings);
        var visual = new RugOverlayWindow(display, cloth, renderer);
        using var input = new RugInputWindow(display, cloth, allowOsCapture: false);
        using var clock = new RenderClock();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var intervals = new List<double>(120);
        int frames = 0;
        double previous = 0, physicsMs = 0, regionMs = 0, paintStart = 0;
        long paintCount = 0;
        Vector2 corner = RugGeometry.Project(cloth.Particles[0].Position);
        cloth.BeginGrab(0, corner, 110);
        clock.Tick += (_, _) =>
        {
            try
            {
                double now = watch.Elapsed.TotalSeconds;
                if (frames == 15) { paintCount = visual.PaintCount; paintStart = visual.TotalPaintMilliseconds; }
                if (frames > 15) intervals.Add((now - previous) * 1000);
                double elapsed = previous == 0 ? 1d / 60 : now - previous;
                previous = now;
                cloth.SetGrabTarget(corner + new Vector2(90 + MathF.Sin(frames * .035f) * 70, 60), 110);
                long stamp = Stopwatch.GetTimestamp(); cloth.Advance(elapsed);
                if (frames >= 15) physicsMs += Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                if (frames % 2 == 0)
                {
                    stamp = Stopwatch.GetTimestamp(); input.UpdateRegion();
                    if (frames >= 15) regionMs += Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                }
                visual.Redraw(); frames++;
                if (frames == 106) { clock.Stop(); finished.TrySetResult(); }
            }
            catch (Exception exception) { clock.Stop(); finished.TrySetException(exception); }
        };
        try
        {
            visual.Show(); visual.Position(display); input.Show(); clock.Start();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await Task.Delay(40);
            double duration = intervals.Sum() / 1000;
            double[] sorted = intervals.Order().ToArray();
            return new()
            {
                ["pattern"] = UiText.Pattern(AppLanguage.English, pattern),
                ["materialWidth"] = width,
                ["materialHeight"] = width / settings.AspectRatio,
                ["frames"] = intervals.Count,
                ["meanFps"] = intervals.Count / duration,
                ["p95FrameIntervalMs"] = sorted[(int)Math.Floor((sorted.Length - 1) * .95)],
                ["wpfPaints"] = visual.PaintCount - paintCount,
                ["meanWpfPaintMs"] = (visual.TotalPaintMilliseconds - paintStart) / Math.Max(1, visual.PaintCount - paintCount),
                ["meanPhysicsMsPerFrame"] = physicsMs / 91,
                ["meanInputRegionMsPerFrame"] = regionMs / 91,
                ["near60HzInThisRun"] = intervals.Count / duration >= 55,
                ["pipelineExecuted"] = frames == 106 && visual.PaintCount > paintCount,
                ["scope"] = "Offscreen WPF composition callbacks, CPU rendering, physics and native input-region updates; not a visible monitor presentation measurement."
            };
        }
        catch (TimeoutException)
        {
            return new() { ["pipelineExecuted"] = false, ["scope"] = "Offscreen composition did not deliver enough frames within twelve seconds.", ["frames"] = frames };
        }
        finally { clock.Stop(); cloth.EndGrab(); visual.Close(); }
    }
}
