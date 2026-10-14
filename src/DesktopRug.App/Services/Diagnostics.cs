using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using DesktopRug.App.Interop;
using DesktopRug.App.Rendering;
using DesktopRug.App.Windows;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Services;

internal static class Diagnostics
{
    /// <summary>Off-screen app smoke test plus real read-only shell discovery. Never moves the pointer or manipulates Explorer.</summary>
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var results = new Dictionary<string, object>();
        nint foreground = Native.GetForegroundWindow();
        using var focusMonitor = new FocusMonitor();
        try
        {
            TrayService.ExportIcon(Path.Combine(output, "rug.ico"));
            var displays = new DisplayTopologyService().Discover();
            var host = new DesktopWindowService().Discover();
            IconSnapshot icons;
            try { icons = await new DesktopIconService().ReadAsync(host.IconList).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { icons = new([], "Explorer accessibility timed out.", false); }
            results["os"] = Environment.OSVersion.VersionString;
            results["displayCount"] = displays.Count;
            results["displayScales"] = displays.Select(d => d.DpiScale).ToArray();
            results["shellHostDiscovered"] = host.Host != 0;
            results["iconListDiscovered"] = host.IconList != 0;
            results["iconTrackingAvailable"] = icons.Available;
            results["iconCount"] = icons.Icons.Count;
            // Do not publish labels, paths, captures of the real desktop, or handle values.
            var display = new DisplayInfo("Offscreen test", new(-30000, -30000, 1000, 760), new(-30000, -30000, 1000, 760), 1, false);
            var cloth = new ClothSimulation { Viewport = display.SizeDip };
            cloth.Reset(new(500, 380), new(700, 490), -5);
            using var renderer = new RugRenderer(cloth, new());
            var visual = new RugOverlayWindow(display, cloth, renderer);
            using var input = new RugInputWindow(display, cloth);
            try
            {
                visual.Show(); visual.Position(display); input.Show();
                await Task.Delay(150);
                results["visualCreated"] = Native.IsWindow(visual.Handle);
                results["inputCreated"] = Native.IsWindow(input.Handle);
                results["inputShown"] = Native.IsWindowVisible(input.Handle);
                results["inputLayerAlpha"] = Native.GetLayeredWindowAttributes(input.Handle, out _, out byte alpha, out _) && alpha == 1;
                results["inputNonActivating"] = (Native.GetWindowLongPtr(input.Handle, Native.GwlExStyle).ToInt64() & Native.NoActivate) != 0;
                results["visualClickThrough"] = (Native.GetWindowLongPtr(visual.Handle, Native.GwlExStyle).ToInt64() & Native.Transparent) != 0;
                results["visualNonActivating"] = (Native.GetWindowLongPtr(visual.Handle, Native.GwlExStyle).ToInt64() & Native.NoActivate) != 0;
                var visualCapture = new RenderTargetBitmap(1000, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                visualCapture.Render((System.Windows.Media.Visual)visual.Content);
                byte[] pixel = new byte[4];
                visualCapture.CopyPixels(new Int32Rect(500, 380, 1, 1), pixel, 4, 0);
                results["wpfRugHasPixels"] = pixel[3] > 200;
                visualCapture.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
                results["wpfOutsideTransparent"] = pixel[3] == 0;
                nint region = Native.CreateRectRgn(0, 0, 0, 0);
                results["regionApplied"] = Native.GetWindowRgn(input.Handle, region) != 0;
                results["regionInside"] = Native.PtInRegion(region, 500, 380);
                results["regionOutsideExcluded"] = !Native.PtInRegion(region, 5, 5); Native.DeleteObject(region);
                if (host.Host != 0)
                {
                    var windowService = new DesktopWindowService();
                    bool placed = true;
                    for (int pass = 0; pass < 5; pass++) placed &= windowService.PlaceAboveDesktop(visual.Handle, input.Handle, host);
                    results["invalidDesktopRejected"] = !windowService.PlaceAboveDesktop(visual.Handle, input.Handle, default);
                    results["desktopPlacementCall"] = placed;
                    static nint VisibleAbove(nint window)
                    {
                        do { window = Native.GetWindow(window, 3); } while (window != 0 && !Native.IsWindowVisible(window));
                        return window;
                    }
                    results["desktopZOrder"] = placed
                        && VisibleAbove(host.Host) == visual.Handle && VisibleAbove(visual.Handle) == input.Handle;
                    List<string> stack = [];
                    nint current = host.Host;
                    for (int index = 0; index < 8 && current != 0; index++)
                    {
                        stack.Add((current == visual.Handle ? "rug-visual" : current == input.Handle ? "rug-input" : Native.ClassName(current))
                            + ":topmost=" + ((Native.GetWindowLongPtr(current, Native.GwlExStyle).ToInt64() & 8) != 0));
                        current = Native.GetWindow(current, 3);
                    }
                    results["shellClassOrder"] = stack;
                }
                input.Hide(); visual.Hide();
                results["peekHidesBoth"] = !Native.IsWindowVisible(visual.Handle) && !Native.IsWindowVisible(input.Handle);
            }
            finally { visual.Close(); }
            foreach (var pattern in new[] { RugPattern.Kashan, RugPattern.Tabriz, RugPattern.Modern })
            {
                renderer.SetAppearance(new() { Pattern = pattern });
                Render(renderer, Path.Combine(output, pattern + ".png"));
            }
            renderer.SetAppearance(new());
            var corner = RugGeometry.Project(cloth.Particles[0].Position);
            cloth.BeginGrab(0, corner, 140);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 120; i++)
            {
                cloth.SetGrabTarget(corner + new Vector2(i * 2, i * 1.5f), 140);
                cloth.Step();
            }
            watch.Stop(); results["physics120StepsMs"] = watch.Elapsed.TotalMilliseconds;
            Render(renderer, Path.Combine(output, "Fold.png"));
            cloth.EndGrab();
            for (int i = 0; i < 1800; i++) cloth.Step();
            results["clothSettles"] = cloth.IsSleeping;
            results["finiteParticles"] = cloth.Particles.All(p => float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z));
            using (var bitmap = new SKBitmap(1000, 760))
            using (var canvas = new SKCanvas(bitmap))
            {
                for (int i = 0; i < 10; i++) renderer.Draw(canvas);
                long allocationStart = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
                for (int i = 0; i < 60; i++) { canvas.Clear(SKColors.Transparent); renderer.Draw(canvas); }
                watch.Stop();
                results["renderMeanMs1000x760"] = watch.Elapsed.TotalMilliseconds / 60;
                results["renderManagedBytesPerFrame"] = (GC.GetAllocatedBytesForCurrentThread() - allocationStart) / 60;
                results["renderLastStageMs"] = renderer.LastStageTicks.Select(t => t * 1000d / Stopwatch.Frequency).ToArray();
            }
            // Construct the complete controller, tray menu and settings UI with presentation suppressed.
            var testStore = new SettingsService(Path.Combine(output, "test-settings"));
            using (var controller = new RugController(new(), testStore, new AppLog(output), suppressPresentation: true))
            {
                var settingsWindow = new SettingsWindow(controller);
                try
                {
                    settingsWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    settingsWindow.Left = -30000; settingsWindow.Top = -30000;
                    settingsWindow.ShowActivated = false; settingsWindow.ShowInTaskbar = false;
                    nint settingsHandle = new System.Windows.Interop.WindowInteropHelper(settingsWindow).EnsureHandle();
                    long settingsStyles = Native.GetWindowLongPtr(settingsHandle, Native.GwlExStyle).ToInt64();
                    Native.SetWindowLongPtr(settingsHandle, Native.GwlExStyle, (nint)(settingsStyles | Native.NoActivate | Native.ToolWindow));
                    settingsWindow.Show();
                    await Task.Delay(100);
                    var content = (FrameworkElement)settingsWindow.Content;
                    content.Measure(new Size(610, 760)); content.Arrange(new Rect(0, 0, 610, 760)); content.UpdateLayout();
                    var target = new RenderTargetBitmap(610, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    target.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                    using var settingsImage = File.Create(Path.Combine(output, "Settings.png")); encoder.Save(settingsImage);
                    results["settingsUiCreated"] = true;
                }
                finally { settingsWindow.Close(); }
                controller.HideRug(); results["controllerHide"] = !controller.RugRequested;
                controller.ShowRug(); results["controllerShow"] = controller.RugRequested;
                controller.Peek(); results["controllerPeek"] = controller.PeekActive;
                controller.Peek(); results["controllerEndPeek"] = !controller.PeekActive;
                results["controllerSettingsChange"] = controller.ApplySettings(controller.Settings with { Size = RugSize.Small, Pattern = RugPattern.Tabriz });
                controller.Rotate(10); controller.Flatten(); controller.HideRug();
            }
            results["foregroundUnchangedDuringCheck"] = Native.GetForegroundWindow() == foreground;
            results["noAppWindowActivated"] = !focusMonitor.AppWasActivated;
            results["manualInteractionVerified"] = false;
            bool passed = new[] { "visualCreated", "inputCreated", "inputShown", "inputLayerAlpha", "inputNonActivating", "visualClickThrough", "visualNonActivating", "wpfRugHasPixels", "wpfOutsideTransparent", "regionApplied", "regionInside", "regionOutsideExcluded", "peekHidesBoth", "finiteParticles", "noAppWindowActivated", "settingsUiCreated", "controllerHide", "controllerShow", "controllerPeek", "controllerEndPeek", "controllerSettingsChange" }.All(key => results[key] is true);
            if (results.TryGetValue("desktopZOrder", out var zOrder)) passed &= zOrder is true;
            results["offscreenSmokePassed"] = passed;
            await File.WriteAllTextAsync(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            results["error"] = exception.ToString();
            await File.WriteAllTextAsync(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    private static void Render(RugRenderer renderer, string path)
    {
        using var bitmap = new SKBitmap(1000, 760); using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(35, 46, 50)); renderer.Draw(canvas);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var stream = File.Create(path); data.SaveTo(stream);
    }

    private sealed class FocusMonitor : IDisposable
    {
        private readonly Native.WinEventProc _callback;
        private readonly nint _hook;
        internal bool AppWasActivated { get; private set; }
        internal FocusMonitor()
        {
            _callback = (_, _, hwnd, _, _, _, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint process);
                if (process == Environment.ProcessId) AppWasActivated = true;
            };
            _hook = Native.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            if (_hook == 0) throw new System.ComponentModel.Win32Exception("Could not observe foreground events for diagnostics.");
        }
        public void Dispose() { Native.UnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }
}
