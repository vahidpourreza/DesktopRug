using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopRug.App.Interop;
using DesktopRug.App.Rendering;
using DesktopRug.App.Windows;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

/// <summary>
/// Exercises owned HWNDs and simulated input entirely outside the visible desktop. These fixtures
/// are synthetic windows, not Explorer, real icons, hardware pointer input, or a physical monitor test.
/// </summary>
internal static class NativeInteractionDiagnostics
{
    internal static async Task<Dictionary<string, object>> RunAsync(string output)
    {
        var results = new Dictionary<string, object>
        {
            ["fixture"] = "Synthetic offscreen native background and occluder; no Explorer or real desktop icons.",
            ["inputEvidence"] = "Mouse messages sent to owned WndProc; touch and pen are decoded-model simulations, not hardware tests.",
            ["physicalTouchPenVerified"] = false,
            ["physicalMultiMonitorVerified"] = false
        };
        Directory.CreateDirectory(output);
        var left = new DisplayInfo("Synthetic 100%", new(-30000, -28000, 900, 700), new(-30000, -28000, 900, 700), 1, true);
        var right = new DisplayInfo("Synthetic 150%", new(-29100, -28000, 900, 700), new(-29100, -28000, 900, 700), 1.5, false);
        var space = new RugCoordinateSpace(RugCoordinateSpace.Union([left, right]), 1);
        var cloth = new ClothSimulation(20, 16) { Viewport = space.Size };
        void Reset() => cloth.Reset(new(500, 350), new(500, 350), 0);
        Reset();
        using var focus = new ActivationMonitor();
        using var background = new FixtureWindow(space.ScreenBounds);
        background.Show();
        using var renderer = new RugRenderer(cloth, new RugSettings { Shadow = 0 });
        RugOverlayWindow? leftVisual = null, rightVisual = null;
        try
        {
            await Task.Delay(30);
            bool hitRoutingAvailable = Native.WindowFromPoint(Point(space.Origin + new Vector2(20, 20))) == background.Handle;
            results["offscreenWindowFromPointAvailable"] = hitRoutingAvailable;
            if (!hitRoutingAvailable)
                results["hitRoutingLimitation"] = "Windows does not resolve WindowFromPoint to this offscreen fixture; corresponding route checks are unavailable, not passed.";

            leftVisual = new(left, cloth, renderer, space);
            rightVisual = new(right, cloth, renderer, space);
            using var leftInput = new RugInputWindow(left, cloth, space, allowOsCapture: false);
            using var rightInput = new RugInputWindow(right, cloth, space, allowOsCapture: false);
            leftVisual.Show(); leftVisual.Position(left); rightVisual.Show(); rightVisual.Position(right);
            leftInput.Show(); rightInput.Show();
            var layers = new[] { (Visual: leftVisual.Handle, Input: leftInput.Handle), (Visual: rightVisual.Handle, Input: rightInput.Handle) };
            var host = new DesktopHost(background.Handle, 0);
            var desktop = new DesktopWindowService();
            results["syntheticDesktopPlacement"] = desktop.PlaceAboveDesktop(layers, host);
            results["syntheticLayerOrder"] = desktop.IsLayerCorrect(layers, host);
            results["pointerNativeLayout"] = PointerInput.HasExpectedNativeLayout();
            var ring = new ClothSimulation(36, 16);
            for (int row = 0; row < ring.Rows; row++)
                for (int col = 0; col < ring.Columns; col++)
                {
                    float angle = col * MathF.Tau / (ring.Columns - 1), radius = 60 + row * 8;
                    ring.Particles[row * ring.Columns + col].Position = new(450 + MathF.Cos(angle) * radius, 350 + MathF.Sin(angle) * radius, 0);
                }
            var ringBuilder = new RugHitRegion(ring);
            nint ringRegion = ringBuilder.Create(left, space);
            results["scanlineUnionPreservesUncoveredHoles"] = ringRegion != 0 && !Native.PtInRegion(ringRegion, 450, 350)
                && Native.PtInRegion(ringRegion, 550, 350) && !Native.PtInRegion(ringRegion, 10, 10);
            if (ringRegion != 0) Native.DeleteObject(ringRegion);
            ring.Particles[0].Position.X = float.NaN;
            nint invalidRegion = ringBuilder.Create(left, space);
            results["invalidHitGeometryFailsClosed"] = invalidRegion == 0;
            if (invalidRegion != 0) Native.DeleteObject(invalidRegion);
            results["pointerDoesNotActivate"] = Native.SendMessage(leftInput.Handle, PointerInput.WmPointerActivate, 0, 0) == PointerInput.NoActivate;
            results["mouseDoesNotActivate"] = Native.SendMessage(leftInput.Handle, Native.WmMouseActivate, 0, 0) == 3;
            await Task.Delay(30);
            Vector2 inside = space.WorldToScreen(cloth.Center);
            results["initialShapeIncludesRug"] = RegionContains(leftInput, left, inside);
            results["initialShapeExcludesBackground"] = !RegionContains(leftInput, left, space.Origin + new Vector2(10, 10));
            RecordRoute("insideRoutesToRug", inside, leftInput.Handle);
            RecordRoute("outsideRoutesToBackground", space.Origin + new Vector2(10, 10), background.Handle);

            using (var occluder = new FixtureWindow(new(inside.X - 70, inside.Y - 70, 140, 140)))
            {
                occluder.Show();
                bool placed = desktop.PlaceAboveDesktop(layers, host);
                results["occludingWindowRemainsAboveRug"] = placed && IsAbove(occluder.Handle, leftInput.Handle)
                    && IsAbove(leftInput.Handle, leftVisual.Handle);
                RecordRoute("occludedPointRoutesToApplication", inside, occluder.Handle);
                results["occlusionDetected"] = desktop.IsFullyOccluded(leftVisual.Handle, new(inside.X - 30, inside.Y - 30, 60, 60));
            }
            leftInput.Hide(); rightInput.Hide(); leftVisual.Hide(); rightVisual.Hide();
            results["peekHidesNativeSurfaces"] = !Native.IsWindowVisible(leftInput.Handle) && !Native.IsWindowVisible(rightInput.Handle)
                && !Native.IsWindowVisible(leftVisual.Handle) && !Native.IsWindowVisible(rightVisual.Handle);
            RecordRoute("peekRoutesToBackground", inside, background.Handle);
            leftVisual.Show(); leftVisual.Position(left); rightVisual.Show(); rightVisual.Position(right);
            leftInput.Show(); rightInput.Show(); desktop.PlaceAboveDesktop(layers, host);

            // SendMessage enters the actual native WndProc; allowOsCapture:false keeps the user's input untouched.
            Vector2 before = cloth.Center;
            Send(leftInput, left, Native.WmLButtonDown, space.WorldToScreen(before));
            results["nativeMouseDownStartsGrab"] = cloth.IsGrabbed && leftInput.IsDragging;
            for (int frame = 1; frame <= 14; frame++)
            {
                Send(leftInput, left, Native.WmMouseMove, space.WorldToScreen(before + new Vector2(frame * 9, 0)));
                cloth.Step();
            }
            results["nativeMouseDragMovesCenter"] = cloth.Center.X > before.X + 30;
            Send(leftInput, left, Native.WmLButtonUp, space.WorldToScreen(before + new Vector2(126, 0)));
            results["nativeMouseUpEndsGrab"] = !cloth.IsGrabbed && !leftInput.IsDragging;

            Reset();
            int cornerIndex = cloth.Columns + 1;
            Vector2 corner = RugGeometry.Project(cloth.Particles[cornerIndex].Position);
            Send(leftInput, left, Native.WmLButtonDown, space.WorldToScreen(corner));
            for (int frame = 1; frame <= 30; frame++)
            {
                Send(leftInput, left, Native.WmMouseMove, space.WorldToScreen(corner + new Vector2(frame * 2, frame)));
                cloth.Step();
            }
            results["nativeCornerDragLiftsFabric"] = cloth.IsGrabbed && cloth.Particles.Max(p => p.Position.Z) > 25;
            Send(leftInput, left, Native.WmLButtonUp, space.WorldToScreen(corner + new Vector2(60, 30)));
            results["nativeCornerReleaseEndsGrab"] = !cloth.IsGrabbed;

            Reset();
            inside = space.WorldToScreen(cloth.Center);
            bool touchAccepted = leftInput.HandlePointer(new(11, PointerDevice.Touch, PointerPhase.Down, inside, true, false, null));
            Vector2 beforeOtherPointer = cloth.Center;
            leftInput.HandlePointer(new(22, PointerDevice.Touch, PointerPhase.Down, inside + new Vector2(100, 0), true, false, null));
            leftInput.HandlePointer(new(22, PointerDevice.Touch, PointerPhase.Move, inside + new Vector2(400, 0), true, false, null));
            leftInput.HandlePointer(new(22, PointerDevice.Touch, PointerPhase.Up, inside + new Vector2(400, 0), false, false, null));
            results["simulatedAdditionalTouchIgnored"] = touchAccepted && leftInput.IsDragging && cloth.Center == beforeOtherPointer;
            leftInput.HandlePointer(new(11, PointerDevice.Touch, PointerPhase.Move, inside + new Vector2(80, 0), true, false, null));
            for (int i = 0; i < 8; i++) cloth.Step();
            results["simulatedTouchMovesFabric"] = cloth.Center.X > beforeOtherPointer.X + 10;
            leftInput.HandlePointer(new(11, PointerDevice.Touch, PointerPhase.Canceled, default, false, false, null));
            results["simulatedTouchCancellationEndsGrab"] = !cloth.IsGrabbed && !leftInput.IsDragging;

            Reset(); corner = RugGeometry.Project(cloth.Particles[cornerIndex].Position);
            Vector2 penScreen = space.WorldToScreen(corner);
            bool penAccepted = leftInput.HandlePointer(new(33, PointerDevice.Pen, PointerPhase.Down, penScreen, true, false, .5f));
            leftInput.HandlePointer(new(33, PointerDevice.Pen, PointerPhase.Move, penScreen + new Vector2(45, 30), true, false, .2f));
            for (int i = 0; i < 24; i++) cloth.Step();
            results["simulatedPenLiftsFabric"] = penAccepted && cloth.IsGrabbed && cloth.Particles.Max(p => p.Position.Z) > 25;
            leftInput.HandlePointer(new(33, PointerDevice.Pen, PointerPhase.Up, penScreen + new Vector2(45, 30), false, false, 0));
            results["simulatedPenReleaseEndsGrab"] = !cloth.IsGrabbed;
            int barrelPeeks = 0;
            void PeekFromBarrel() { barrelPeeks++; leftInput.Hide(); }
            leftInput.PeekRequested += PeekFromBarrel;
            leftInput.HandlePointer(new(44, PointerDevice.Pen, PointerPhase.Down, penScreen, true, true, .5f));
            leftInput.PeekRequested -= PeekFromBarrel;
            leftInput.Show();
            Reset();
            bool nextTouchAccepted = leftInput.HandlePointer(new(55, PointerDevice.Touch, PointerPhase.Down,
                space.WorldToScreen(cloth.Center), true, false, null));
            results["secondaryPointerHideClearsGesture"] = barrelPeeks == 1 && nextTouchAccepted && cloth.IsGrabbed;
            leftInput.CancelGrab();

            // Original-window messages continue beyond its bounds, like a captured drag crossing monitors.
            Reset();
            Vector2 dragStart = cloth.Center;
            Send(leftInput, left, Native.WmLButtonDown, space.WorldToScreen(dragStart));
            for (int frame = 1; frame <= 80; frame++)
            {
                Send(leftInput, left, Native.WmMouseMove, space.WorldToScreen(dragStart + new Vector2(frame * 12, 0)));
                cloth.Step();
            }
            Send(leftInput, left, Native.WmLButtonUp, space.WorldToScreen(dragStart + new Vector2(960, 0)));
            leftInput.UpdateRegion(); rightInput.UpdateRegion();
            Vector2 destination = space.WorldToScreen(cloth.Center);
            results["originalWindowDragCrossesMonitorBoundary"] = destination.X > right.Bounds.X + 60 && !cloth.IsGrabbed;
            results["secondViewportRegionContainsMovedRug"] = RegionContains(rightInput, right, destination);
            RecordRoute("movedRugRoutesToSecondViewport", destination, rightInput.Handle);
            Vector2[] screenBefore = cloth.Particles.Select(p => space.WorldToScreen(RugGeometry.Project(p.Position))).ToArray();
            var nextSpace = new RugCoordinateSpace(space.ScreenBounds, 1.5f);
            cloth.Rebase(space.Origin, space.PixelScale, nextSpace.Origin, nextSpace.PixelScale);
            cloth.Viewport = nextSpace.Size;
            leftInput.SetSpace(nextSpace); rightInput.SetSpace(nextSpace);
            leftVisual.SetSpace(nextSpace); rightVisual.SetSpace(nextSpace);
            float maximumDrift = cloth.Particles.Select((p, i) => Vector2.Distance(screenBefore[i], nextSpace.WorldToScreen(RugGeometry.Project(p.Position)))).Max();
            results["mixedDpiRebaseMaximumScreenDriftPixels"] = maximumDrift;
            results["mixedDpiRebasePreservesScreenGeometry"] = maximumDrift < .02f;
            results["secondViewportRegionSurvivesRebase"] = RegionContains(rightInput, right, destination);
            RecordRoute("rebasedRugRoutesToSecondViewport", destination, rightInput.Handle);
            results["allFixtureWindowsStayNonTopmost"] = layers.SelectMany(pair => new[] { pair.Visual, pair.Input })
                .Append(background.Handle).All(handle => (Native.GetWindowLongPtr(handle, Native.GwlExStyle).ToInt64() & 8) == 0);
            results["finiteParticlesAfterInteractions"] = cloth.Particles.All(p => float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z));

            void RecordRoute(string name, Vector2 location, nint expected)
            {
                results[name] = hitRoutingAvailable ? Native.WindowFromPoint(Point(location)) == expected : "unavailable outside physical desktop";
            }
        }
        catch (Exception exception) { results["error"] = exception.ToString(); }
        finally { leftVisual?.Close(); rightVisual?.Close(); }
        await Task.Delay(30);
        results["noFixtureActivated"] = !focus.AppWasActivated;
        var required = new[]
        {
            "syntheticDesktopPlacement", "syntheticLayerOrder", "pointerNativeLayout", "scanlineUnionPreservesUncoveredHoles", "invalidHitGeometryFailsClosed", "pointerDoesNotActivate", "mouseDoesNotActivate",
            "initialShapeIncludesRug", "initialShapeExcludesBackground", "occludingWindowRemainsAboveRug", "occlusionDetected", "peekHidesNativeSurfaces",
            "nativeMouseDownStartsGrab", "nativeMouseDragMovesCenter", "nativeMouseUpEndsGrab", "nativeCornerDragLiftsFabric", "nativeCornerReleaseEndsGrab",
            "simulatedAdditionalTouchIgnored", "simulatedTouchMovesFabric", "simulatedTouchCancellationEndsGrab", "simulatedPenLiftsFabric", "simulatedPenReleaseEndsGrab",
            "secondaryPointerHideClearsGesture", "originalWindowDragCrossesMonitorBoundary", "secondViewportRegionContainsMovedRug", "mixedDpiRebasePreservesScreenGeometry",
            "secondViewportRegionSurvivesRebase", "allFixtureWindowsStayNonTopmost", "finiteParticlesAfterInteractions", "noFixtureActivated"
        };
        bool passed = !results.ContainsKey("error") && required.All(key => results.TryGetValue(key, out var value) && value is true);
        if (results.TryGetValue("offscreenWindowFromPointAvailable", out var routing) && routing is true)
            passed &= new[] { "insideRoutesToRug", "outsideRoutesToBackground", "occludedPointRoutesToApplication", "peekRoutesToBackground",
                "movedRugRoutesToSecondViewport", "rebasedRugRoutesToSecondViewport" }.All(key => results.TryGetValue(key, out var value) && value is true);
        results["nativeInteractionChecksPassed"] = passed;
        await File.WriteAllTextAsync(Path.Combine(output, "native-interactions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results;
    }

    private static Native.Point Point(Vector2 value) => new((int)MathF.Round(value.X), (int)MathF.Round(value.Y));
    private static void Send(RugInputWindow input, DisplayInfo display, int message, Vector2 screen)
    {
        int x = (int)MathF.Round(screen.X - (float)display.Bounds.X), y = (int)MathF.Round(screen.Y - (float)display.Bounds.Y);
        nint packed = (nint)((uint)(ushort)x | ((uint)(ushort)y << 16));
        Native.SendMessage(input.Handle, (uint)message, message == Native.WmLButtonUp ? 0 : 1, packed);
    }
    private static bool RegionContains(RugInputWindow input, DisplayInfo display, Vector2 screen)
    {
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            return region != 0 && Native.GetWindowRgn(input.Handle, region) != 0
                && Native.PtInRegion(region, (int)MathF.Round(screen.X - (float)display.Bounds.X), (int)MathF.Round(screen.Y - (float)display.Bounds.Y));
        }
        finally { if (region != 0) Native.DeleteObject(region); }
    }
    private static bool IsAbove(nint upper, nint lower)
    {
        for (int count = 0; count < 4096 && lower != 0; count++)
        {
            lower = Native.GetWindow(lower, 3);
            if (lower == upper) return true;
        }
        return false;
    }

    private sealed class FixtureWindow : IDisposable
    {
        private readonly Native.WindowProc _procedure;
        private readonly string _className = "DesktopRug.DiagnosticFixture." + Guid.NewGuid().ToString("N");
        private readonly nint _module = Native.GetModuleHandle(null);
        internal nint Handle { get; private set; }
        internal FixtureWindow(RectD bounds)
        {
            _procedure = (hwnd, message, wParam, lParam) => message is Native.WmMouseActivate or PointerInput.WmPointerActivate
                ? 3 : Native.DefWindowProc(hwnd, message, wParam, lParam);
            var windowClass = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Name = _className,
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = _module,
                Background = Native.GetStockObject(4)
            };
            if (Native.RegisterClassEx(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Handle = Native.CreateWindowEx((uint)(Native.ToolWindow | Native.NoActivate), _className, "Offscreen diagnostic fixture", 0x80000000,
                (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0, 0, _module, 0);
            if (Handle == 0)
            {
                int error = Marshal.GetLastWin32Error(); Native.UnregisterClass(_className, _module);
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
        internal void Show() => Native.ShowWindow(Handle, 8);
        public void Dispose()
        {
            if (Handle == 0) return;
            Native.DestroyWindow(Handle); Handle = 0; Native.UnregisterClass(_className, _module); GC.KeepAlive(_procedure);
        }
    }
    private sealed class ActivationMonitor : IDisposable
    {
        private readonly Native.WinEventProc _callback;
        private readonly nint _hook;
        internal bool AppWasActivated { get; private set; }
        internal ActivationMonitor()
        {
            _callback = (_, _, hwnd, _, _, _, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint process);
                if (process == Environment.ProcessId) AppWasActivated = true;
            };
            _hook = Native.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            if (_hook == 0) throw new System.ComponentModel.Win32Exception("Could not observe fixture activation.");
        }
        public void Dispose() { Native.UnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }
}
