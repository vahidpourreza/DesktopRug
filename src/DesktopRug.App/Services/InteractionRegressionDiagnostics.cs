using System.Numerics;
using System.Text.Json;
using DesktopRug.App.Interop;
using DesktopRug.App.Windows;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Services;

/// <summary>Owned-window messages and isolated preferences; never injects global input or changes startup registration.</summary>
internal static class InteractionRegressionDiagnostics
{
    internal static async Task<Dictionary<string, object>> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var results = new Dictionary<string, object>();
        using var activation = new ActivationObserver();
        try
        {
            CheckInput(results);
            await CheckLivePreferences(output, results);
            CheckCatalogGeometry(output, results);
            await CheckCustomFallbackAndRollback(output, results);
        }
        catch (Exception exception) { results["error"] = exception.ToString(); }
        results["noDiagnosticWindowActivated"] = !activation.AppWasActivated;
        results["interactionRegressionChecksPassed"] = !results.ContainsKey("error") && results.Values.All(value => value is true);
        results["scope"] = "Synthetic mouse messages to owned offscreen HWNDs, modeled touch gestures, read-only primary-button mapping, and isolated settings files. No physical mouse/touch/multi-monitor verification, global input injection, focus requests, actual user-image changes or startup writes.";
        await File.WriteAllTextAsync(Path.Combine(output, "interaction-regressions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results;
    }

    private static void CheckInput(Dictionary<string, object> results)
    {
        var left = new DisplayInfo("Synthetic left", new(-30000, -28000, 900, 700), default, 1, true);
        var right = new DisplayInfo("Synthetic right", new(-29100, -28000, 900, 700), default, 1.5, false);
        var space = new RugCoordinateSpace(RugCoordinateSpace.Union([left, right]), 1);
        var cloth = new ClothSimulation(20, 16) { Viewport = space.Size };
        cloth.Reset(new(500, 350), new(500, 350), 0);
        using var input = new RugInputWindow(left, cloth, space, allowOsCapture: false);
        using var second = new RugInputWindow(right, cloth, space, allowOsCapture: false);
        int compactRequests = 0, rotatedDegrees = 0, wheelEvents = 0;
        input.CompactRequested += () => { compactRequests++; cloth.SetRolled(!cloth.IsRolled); };
        input.RotateRequested += degrees => { rotatedDegrees += degrees; wheelEvents++; };
        input.Show(); second.Show();

        void Reset(Vector2? center = null)
        {
            input.CancelGrab(); second.CancelGrab();
            cloth.Reset(center ?? new(500, 350), new(500, 350), 0);
            input.UpdateRegion(); second.UpdateRegion();
        }
        Vector2 PointOnCloth()
        {
            for (int row = 0; row < cloth.Rows - 1; row++)
                for (int col = 0; col < cloth.Columns - 1; col++)
                {
                    int a = row * cloth.Columns + col;
                    Vector2 point = (RugGeometry.Project(cloth.Particles[a].Position) + RugGeometry.Project(cloth.Particles[a + 1].Position)
                        + RugGeometry.Project(cloth.Particles[a + cloth.Columns].Position)) / 3;
                    Vector2 screen = space.WorldToScreen(point);
                    point = space.ScreenToWorld(new Vector2(MathF.Round(screen.X), MathF.Round(screen.Y)));
                    if (cloth.Pick(point) >= 0) return point;
                }
            throw new InvalidOperationException("Diagnostic cloth has no pickable projected face.");
        }
        void Mouse(int message, Vector2 world) => Send(input.Handle, message, space.WorldToScreen(world), left, 0);
        void Click(Vector2 world) { Mouse(Native.WmLButtonDown, world); Mouse(Native.WmLButtonUp, world); }
        void Wheel(int delta) => Send(input.Handle, Native.WmMouseWheel, space.WorldToScreen(cloth.Center), left,
            (nint)((uint)(ushort)(short)delta << 16));

        cloth.SetRolled(true);
        for (int frame = 0; frame < 110; frame++) cloth.Step();
        input.UpdateRegion();
        Vector2 rolledPoint = PointOnCloth();
        Vector3[] beforeClick = cloth.Particles.Select(p => p.Position).ToArray();
        Mouse(Native.WmLButtonDown, rolledPoint);
        results["rolledMouseDownIsPending"] = input.IsDragging && !cloth.IsGrabbed && cloth.IsRolled;
        Mouse(Native.WmLButtonUp, rolledPoint);
        results["plainClickPreservesRollAndGeometry"] = cloth.IsRolled && !input.IsDragging
            && beforeClick.SequenceEqual(cloth.Particles.Select(p => p.Position));
        Click(rolledPoint);
        results["doubleClickUnrollsRolledRug"] = compactRequests == 1 && !cloth.IsRolled;

        Reset(); int priorCompact = compactRequests; Vector2 center = cloth.Center;
        Click(center); Click(center);
        results["doubleClickRollsFlatRug"] = compactRequests == priorCompact + 1 && cloth.IsRolled;
        Reset(); priorCompact = compactRequests; center = cloth.Center;
        Click(center); input.Hide(); input.Show(); Click(center);
        results["hideShowStartsFreshClickSequence"] = compactRequests == priorCompact && !cloth.IsRolled;
        input.CancelGrab();
        Click(center); Mouse(Native.WmRButtonUp, center); Click(center);
        results["rightClickResetsDoubleClickSequence"] = compactRequests == priorCompact && !cloth.IsRolled;
        input.CancelGrab();
        Click(center); Click(center + new Vector2(MouseInputSettings.DoubleClickHalfWidth + 3, 0));
        results["doubleClickUsesSystemScreenRectangle"] = compactRequests == priorCompact;

        Reset(); center = cloth.Center;
        Mouse(Native.WmLButtonDown, center);
        Mouse(Native.WmMouseMove, center + new Vector2(MouseInputSettings.DragWidth + 12, 0));
        results["movementStartsDeferredGrab"] = input.IsDragging && cloth.IsGrabbed;
        Mouse(Native.WmLButtonUp, center + new Vector2(MouseInputSettings.DragWidth + 12, 0));
        results["deferredGrabReleases"] = !input.IsDragging && !cloth.IsGrabbed;

        Reset(new(900, 350));
        Vector2 leftPoint = space.WorldToScreen(new Vector2(800, 350)), rightPoint = space.WorldToScreen(new Vector2(1000, 350));
        input.HandlePointer(new(101, PointerDevice.Touch, PointerPhase.Down, leftPoint, true, false, null));
        second.HandlePointer(new(102, PointerDevice.Touch, PointerPhase.Down, rightPoint, true, false, null));
        results["pendingGestureOwnsClothAcrossSurfaces"] = input.IsDragging && !second.IsDragging && !cloth.IsGrabbed;
        input.HandlePointer(new(101, PointerDevice.Touch, PointerPhase.Canceled, leftPoint, false, false, null));
        second.HandlePointer(new(103, PointerDevice.Touch, PointerPhase.Down, rightPoint, true, false, null));
        results["cancelTransfersPendingOwnership"] = !input.IsDragging && second.IsDragging && !cloth.IsGrabbed;
        second.CancelGrab();
        Mouse(Native.WmLButtonDown, new(800, 350));
        Native.SendMessage(input.Handle, Native.WmCaptureChanged, 0, 0);
        results["captureLossCancelsPendingGesture"] = !input.IsDragging && !cloth.IsGrabbed;

        Reset();
        void ResetWheel() { input.Hide(); input.Show(); rotatedDegrees = wheelEvents = 0; }
        ResetWheel(); Wheel(0);
        results["zeroWheelDoesNothing"] = wheelEvents == 0;
        for (int i = 0; i < 5; i++) Wheel(20);
        bool noPrematureRotation = wheelEvents == 0;
        Wheel(20);
        results["fractionalWheelAccumulatesOneNotch"] = noPrematureRotation && wheelEvents == 1 && rotatedDegrees == 5;
        ResetWheel(); Wheel(240); Wheel(-240);
        results["wheelHonorsMultipleNotches"] = wheelEvents == 2 && rotatedDegrees == 0;
        ResetWheel(); Wheel(60); Wheel(-60);
        results["opposingPartialWheelCancels"] = wheelEvents == 0;
        Wheel(60); input.Hide(); input.Show(); Wheel(60);
        results["hideClearsPartialWheel"] = wheelEvents == 0;
        results["primaryButtonMappingHandlesSwappedButtons"] = MouseInputSettings.PrimaryVirtualKey(false) == 1 && MouseInputSettings.PrimaryVirtualKey(true) == 2;

        Reset(new(900, 350));
        int faults = 0;
        void FailActivity() => throw new InvalidOperationException("Injected owned-window diagnostic failure.");
        input.Activity += FailActivity; input.Faulted += _ => faults++;
        Mouse(Native.WmLButtonDown, new(800, 350));
        input.Activity -= FailActivity;
        second.HandlePointer(new(104, PointerDevice.Touch, PointerPhase.Down, rightPoint, true, false, null));
        results["nativeCallbackFailureReleasesGestureOwnership"] = faults == 1 && !input.IsDragging && second.IsDragging && !cloth.IsGrabbed;
        second.CancelGrab();
    }

    private static async Task CheckLivePreferences(string output, Dictionary<string, object> results)
    {
        string imagePath = Path.Combine(output, "disappearing-custom-fixture.png");
        CreateCustomFixture(imagePath);
        var store = new SettingsService(Path.Combine(output, "isolated-live-preferences"));
        using var controller = new RugController(new()
        {
            Pattern = RugPattern.Custom,
            CustomTexturePath = imagePath,
            Sound = false,
            PeekMode = PeekActivationMode.Toggle
        }, store, new AppLog(output), suppressPresentation: true);
        await store.SaveAsync(controller.Settings);
        controller.ToggleRoll(); controller.Peek();
        var before = controller.CaptureSceneDiagnostics();
        bool startup = controller.Settings.LaunchAtLogin;
        bool soundSaved = await controller.ApplySettingsAsync(controller.Settings with { Sound = true });
        results["soundTogglePreservesSceneAndPeek"] = soundSaved && controller.Settings.Sound && controller.PeekActive
            && SameScene(before, controller.CaptureSceneDiagnostics());
        await controller.ApplySettingsAsync(controller.Settings with { Sound = false });
        // This is the generated fixture only; no user-selected image is touched.
        File.Delete(imagePath);
        bool missingImageSaved = await controller.ApplySettingsAsync(controller.Settings with
        {
            IconBumps = false,
            PeekMode = PeekActivationMode.Hold,
            PeekSeconds = 11,
            PeekOnRightClick = false,
            PeekShortcut = new() { Modifiers = PeekModifiers.Control | PeekModifiers.Shift, Key = 'M' }
        });
        var persisted = await store.LoadAsync();
        results["livePreferencesDoNotReopenMissingCustomImage"] = missingImageSaved && !File.Exists(imagePath)
            && controller.Settings.Pattern == RugPattern.Custom && controller.Settings.CustomTexturePath == imagePath
            && SameScene(before, controller.CaptureSceneDiagnostics());
        results["livePreferencesPersist"] = !persisted.IconBumps && persisted.PeekMode == PeekActivationMode.Hold
            && persisted.PeekSeconds == 11 && !persisted.PeekOnRightClick && persisted.PeekShortcut.Key == 'M';
        results["livePreferencesLeaveStartupUnchanged"] = controller.Settings.LaunchAtLogin == startup;
        bool lightingSaved = await controller.ApplySettingsAsync(controller.Settings with { Shadow = .17f, Lighting = .29f });
        results["lightingShadowPreserveSceneWithMissingCustomSource"] = lightingSaved
            && SameScene(before, controller.CaptureSceneDiagnostics()) && !File.Exists(imagePath);
        persisted = await store.LoadAsync();
        results["lightingShadowPersist"] = persisted.Shadow == .17f && persisted.Lighting == .29f;

        controller.Flatten();
        var geometryState = controller.CaptureSceneDiagnostics();
        var selectedDisplay = controller.Displays.FirstOrDefault(d => d.IsPrimary) ?? controller.Displays[0];
        var frame = new RugCoordinateSpace(RugCoordinateSpace.Union(controller.Displays), (float)selectedDisplay.DpiScale);
        Vector2 midpoint = new(geometryState.Positions.Average(p => p.X), geometryState.Positions.Average(p => p.Y));
        var savedPosition = controller.Settings;
        Send(geometryState.Handles[0].Input, Native.WmLButtonDown, frame.WorldToScreen(midpoint), controller.Displays[0], 0);
        Send(geometryState.Handles[0].Input, Native.WmMouseMove,
            frame.WorldToScreen(midpoint + new Vector2(MouseInputSettings.DragWidth + 14, 0)), controller.Displays[0], 0);
        bool dragged = controller.HasActiveGesture;
        controller.RestorePosition();
        var restored = controller.CaptureSceneDiagnostics();
        results["restoreCancelsDragWithoutReplacingSavedPosition"] = dragged && !controller.HasActiveGesture
            && controller.Settings.CenterX == savedPosition.CenterX && controller.Settings.CenterY == savedPosition.CenterY
            && geometryState.Positions.Zip(restored.Positions).All(pair => Vector3.Distance(pair.First, pair.Second) < .001f);
        Send(geometryState.Handles[0].Input, Native.WmLButtonDown, frame.WorldToScreen(midpoint), controller.Displays[0], 0);
        bool prepared = controller.HasActiveGesture;
        controller.ToggleRoll();
        results["geometryCommandCancelsPendingInputOwner"] = prepared && !controller.HasActiveGesture && controller.IsRolled;

        string blocked = Path.Combine(output, "blocked-preferences-parent.file");
        await File.WriteAllTextAsync(blocked, "Isolated diagnostic parent-path blocker.");
        using var failed = new RugController(new() { Sound = false }, new SettingsService(Path.Combine(blocked, "settings")), new AppLog(output), suppressPresentation: true);
        failed.ToggleRoll();
        var failedBefore = failed.CaptureSceneDiagnostics(); var previous = failed.Settings;
        bool applied = await failed.ApplySettingsAsync(previous with
        { Sound = true, PeekShortcut = new() { Modifiers = PeekModifiers.Control | PeekModifiers.Shift, Key = 'N' } });
        results["failedLivePreferencesKeepSettingsAndScene"] = !applied && !failed.IsApplying && failed.Settings == previous
            && SameScene(failedBefore, failed.CaptureSceneDiagnostics());
        failed.Rotate(-1085);
        results["largeNegativeRotationNormalizes"] = failed.Settings.Rotation is >= 0 and < 360;
    }

    private static bool SameScene(SceneDiagnosticSnapshot before, SceneDiagnosticSnapshot after) =>
        ReferenceEquals(before.ClothIdentity, after.ClothIdentity) && before.Handles.SequenceEqual(after.Handles)
        && before.Positions.SequenceEqual(after.Positions) && before.Rolled == after.Rolled;

    private static async Task CheckCustomFallbackAndRollback(string output, Dictionary<string, object> results)
    {
        string missing = Path.Combine(output, "missing-fallback-" + Guid.NewGuid().ToString("N") + ".png");
        string corrupt = Path.Combine(output, "corrupt-fallback-fixture.png");
        await File.WriteAllTextAsync(corrupt, "This generated diagnostic fixture is intentionally not an image.");
        bool fallbackPatterns = true, fallbackShapes = true;
        foreach (string path in new[] { missing, corrupt })
        {
            using var controller = new RugController(new()
            { Pattern = RugPattern.Custom, CustomTexturePath = path, Rotation = 45, Sound = false },
                new SettingsService(Path.Combine(output, "fallback-settings")), new AppLog(output), suppressPresentation: true);
            fallbackPatterns &= controller.Settings.Pattern == RugPattern.Kashan;
            fallbackShapes &= Math.Abs(MaterialRatio(controller.CaptureSceneDiagnostics(), controller.Settings.Rotation) - controller.Settings.AspectRatio) < .001f;
        }
        results["missingAndCorruptCustomImagesSelectCatalogFallback"] = fallbackPatterns;
        results["customImageFallbackRebuildsCorrectPortraitShape"] = fallbackShapes;

        string rollbackImage = Path.Combine(output, "rollback-custom-fixture.png");
        CreateCustomFixture(rollbackImage);
        string blocker = Path.Combine(output, "blocked-material-settings-parent.file");
        await File.WriteAllTextAsync(blocker, "Isolated material-save diagnostic blocker.");
        using var previous = new RugController(new()
        { Pattern = RugPattern.Custom, CustomTexturePath = rollbackImage, Sound = false },
            new SettingsService(Path.Combine(blocker, "settings")), new AppLog(output), suppressPresentation: true);
        previous.ToggleRoll();
        var oldSettings = previous.Settings; var oldScene = previous.CaptureSceneDiagnostics();
        // Only this generated fixture is removed. The already loaded custom material must
        // survive a different material's failed settings transaction without reopening it.
        File.Delete(rollbackImage);
        bool saved = await previous.ApplySettingsAsync(oldSettings with { Pattern = RugPattern.Tabriz });
        results["failedMaterialSavePreservesMissingCustomSourceScene"] = !saved && !previous.IsApplying
            && previous.Settings == oldSettings && SameScene(oldScene, previous.CaptureSceneDiagnostics());
        results["failedMaterialSavePreservesCustomAspectRatio"] =
            Math.Abs(MaterialRatio(previous.CaptureSceneDiagnostics(), previous.Settings.Rotation) - previous.Settings.AspectRatio) < .001f;
    }

    private static float MaterialRatio(SceneDiagnosticSnapshot scene, float rotation)
    {
        Vector2[] local = scene.Positions.Select(p => RugGeometry.Rotate(new Vector2(p.X, p.Y), -rotation)).ToArray();
        return (local.Max(p => p.X) - local.Min(p => p.X)) / (local.Max(p => p.Y) - local.Min(p => p.Y));
    }

    private static void CreateCustomFixture(string path)
    {
        using var bitmap = new SKBitmap(100, 70);
        bitmap.Erase(new SKColor(150, 55, 65));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path); data.SaveTo(stream);
    }

    private static void CheckCatalogGeometry(string output, Dictionary<string, object> results)
    {
        bool ratios = true, bounds = true;
        foreach (var pattern in new[] { RugPattern.Kashan, RugPattern.Tabriz, RugPattern.Modern })
        {
            var requested = new RugSettings { Pattern = pattern, Size = RugSize.ExtraLarge, Scale = 3, Rotation = 45, Sound = false };
            using var controller = new RugController(requested, new SettingsService(Path.Combine(output, "geometry-settings")), new AppLog(output), suppressPresentation: true);
            var positions = controller.CaptureSceneDiagnostics().Positions;
            Vector2[] unrotated = positions.Select(p => RugGeometry.Rotate(new Vector2(p.X, p.Y), -requested.Rotation)).ToArray();
            float width = unrotated.Max(p => p.X) - unrotated.Min(p => p.X);
            float height = unrotated.Max(p => p.Y) - unrotated.Min(p => p.Y);
            ratios &= Math.Abs(width / height - requested.AspectRatio) < .001f;
            var display = controller.Displays.FirstOrDefault(d => d.IsPrimary) ?? controller.Displays[0];
            bounds &= positions.Max(p => p.X) - positions.Min(p => p.X) <= display.SizeDip.X * .92f + .1f
                && positions.Max(p => p.Y) - positions.Min(p => p.Y) <= display.SizeDip.Y * .92f + .1f;
        }
        results["catalogGeometryKeepsEachSourceAspectRatio"] = ratios;
        results["oversizedRotatedCatalogGeometryFitsDisplay"] = bounds;
    }

    private static void Send(nint hwnd, int message, Vector2 screen, DisplayInfo display, nint wParam)
    {
        int x = (int)MathF.Round(screen.X - (message == Native.WmMouseWheel ? 0 : (float)display.Bounds.X));
        int y = (int)MathF.Round(screen.Y - (message == Native.WmMouseWheel ? 0 : (float)display.Bounds.Y));
        nint packed = (nint)((uint)(ushort)x | ((uint)(ushort)y << 16));
        Native.SendMessage(hwnd, (uint)message, wParam, packed);
    }

    private sealed class ActivationObserver : IDisposable
    {
        private readonly Native.WinEventProc _callback;
        private readonly nint _hook;
        internal bool AppWasActivated { get; private set; }
        internal ActivationObserver()
        {
            _callback = (_, _, window, _, _, _, _) =>
            { Native.GetWindowThreadProcessId(window, out uint process); if (process == Environment.ProcessId) AppWasActivated = true; };
            _hook = Native.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            if (_hook == 0) throw new System.ComponentModel.Win32Exception("Could not observe diagnostic activation.");
        }
        public void Dispose() { Native.UnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }
}
