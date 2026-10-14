using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopRug.App.Interop;
using DesktopRug.App.Windows;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Services;

/// <summary>
/// Exercises language preview, cancellation and persistence in isolated settings,
/// using only offscreen, nonactivating windows and a generated asymmetric fixture.
/// </summary>
internal static class LanguageDiagnostics
{
    internal static async Task<Dictionary<string, object>> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var report = new Dictionary<string, object>();
        nint foreground = Native.GetForegroundWindow();
        using var focus = new FocusObserver();
        string fixture = Path.Combine(output, "language-preview-fixture.png");
        CreateFixture(fixture);
        var store = new SettingsService(Path.Combine(output, "isolated-settings"));
        using var controller = new RugController(new()
        {
            Language = AppLanguage.English,
            Pattern = RugPattern.Custom,
            CustomTexturePath = fixture
        }, store, new AppLog(output), suppressPresentation: true);
        await store.SaveAsync(controller.Settings);
        var baseline = controller.Settings;
        SettingsWindow? window = null;
        try
        {
            window = CreateWindow(controller);
            await Layout(window);
            var english = await Capture(window, Path.Combine(output, "english-top.png"));
            report["englishLtr"] = Content(window).FlowDirection == FlowDirection.LeftToRight;
            report["englishPreviewOrientation"] = PreviewOrientation(window, english);
            SetDraft(window);
            string before = Draft(window);

            window.PreviewLanguage(AppLanguage.Persian);
            await Layout(window);
            report["persianRtl"] = Content(window).FlowDirection == FlowDirection.RightToLeft;
            report["persianVisibleLabels"] = HasPersian(Control<Button>(window, "ApplyButton").Content?.ToString());
            report["previewKeepsDirtyValues"] = Draft(window) == before;
            report["previewDoesNotMutateController"] = controller.Settings == baseline;
            report["previewDoesNotPersist"] = (await store.LoadAsync()) == baseline;
            report["shortcutInputLtr"] = Control<ComboBox>(window, "PeekKey").FlowDirection == FlowDirection.LeftToRight;
            report["customImageNameLtr"] = Control<TextBlock>(window, "ImageName").FlowDirection == FlowDirection.LeftToRight;
            report["customImageNamePreserved"] = Control<TextBlock>(window, "ImageName").Text == Path.GetFileName(fixture);
            var persian = await Capture(window, Path.Combine(output, "persian-top.png"));
            report["persianPreviewOrientation"] = PreviewOrientation(window, persian);
            report["persianControlBoundsFit"] = BoundsFit(window);
            await CapturePeek(window, Path.Combine(output, "persian-peek.png"));

            window.Width = 500; window.Height = 620; window.MaxHeight = 620;
            Control<ScrollViewer>(window, "PreferencesScroll").ScrollToTop();
            await Layout(window);
            await Capture(window, Path.Combine(output, "persian-narrow-top.png"));
            report["narrowPersianControlBoundsFit"] = BoundsFit(window);
            await CapturePeek(window, Path.Combine(output, "persian-narrow-peek.png"));

            window.PreviewLanguage(AppLanguage.English);
            await Layout(window);
            report["roundTripKeepsDirtyValues"] = Draft(window) == before;
            report["englishRestoresLtr"] = Content(window).FlowDirection == FlowDirection.LeftToRight;
            await CapturePeek(window, Path.Combine(output, "english-narrow-peek.png"));
            window.PreviewLanguage(AppLanguage.Persian);
            Control<Button>(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            report["cancelClosesWindow"] = !window.IsVisible;
            report["cancelKeepsControllerSettings"] = controller.Settings == baseline;
            report["cancelKeepsPersistedSettings"] = (await store.LoadAsync()) == baseline;
            window = null;

            window = CreateWindow(controller);
            await Layout(window);
            report["cancelReopensOriginalLanguage"] = Content(window).FlowDirection == FlowDirection.LeftToRight;
            SetDraft(window);
            window.PreviewLanguage(AppLanguage.Persian);
            // The diagnostic must never change the real Windows startup registration,
            // even if a localization regression accidentally resets this checkbox.
            Control<CheckBox>(window, "Startup").IsChecked = baseline.LaunchAtLogin;
            Control<Button>(window, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saveWait = Stopwatch.StartNew();
            while ((controller.IsApplying || window.IsVisible) && saveWait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            report["saveClosesWindow"] = !window.IsVisible && !controller.IsApplying;
            report["savePersianLanguage"] = controller.Settings.Language == AppLanguage.Persian;
            report["saveDirtyValues"] = MatchesDraft(controller.Settings);
            report["saveCustomPathPreserved"] = controller.Settings.CustomTexturePath == fixture;
            report["startupPreferenceUnchanged"] = controller.Settings.LaunchAtLogin == baseline.LaunchAtLogin;
            var saved = await store.LoadAsync();
            report["persianPersists"] = saved.Language == AppLanguage.Persian && MatchesDraft(saved) && saved.CustomTexturePath == fixture;
            window.Close(); window = null;

            using var reopened = new RugController(saved, store, new AppLog(output), suppressPresentation: true);
            window = CreateWindow(reopened);
            await Layout(window);
            report["persianReopensRtl"] = Content(window).FlowDirection == FlowDirection.RightToLeft;
            report["persianReopensDirtyValues"] = Math.Abs(Control<Slider>(window, "RugScale").Value - 1.35) < .001
                && Control<ComboBox>(window, "PeekKey").Text == "K";
            await Capture(window, Path.Combine(output, "persian-reopened.png"));
            window.Close(); window = null;

            // Keep audio disabled for this fixture. In suppressed presentation the roll
            // remains pending; changing language must preserve that state and its scene.
            using var languageOnly = new RugController(saved with { Sound = false }, store, new AppLog(output), suppressPresentation: true);
            languageOnly.ToggleRoll();
            var sceneBefore = languageOnly.CaptureSceneDiagnostics();
            bool languageApplied = await languageOnly.ApplySettingsAsync(languageOnly.Settings with { Language = AppLanguage.English });
            var sceneAfter = languageOnly.CaptureSceneDiagnostics();
            report["languageOnlySaveSucceeds"] = languageApplied && languageOnly.Settings.Language == AppLanguage.English;
            report["languageOnlyKeepsClothIdentity"] = ReferenceEquals(sceneBefore.ClothIdentity, sceneAfter.ClothIdentity);
            report["languageOnlyKeepsNativeSurfaces"] = sceneBefore.Handles.SequenceEqual(sceneAfter.Handles);
            report["languageOnlyKeepsParticlePositions"] = sceneBefore.Positions.SequenceEqual(sceneAfter.Positions);
            report["languageOnlyKeepsRollState"] = sceneBefore.Rolled && sceneAfter.Rolled;

            window = CreateWindow(languageOnly);
            await Layout(window);
            var unchangedSettings = languageOnly.Settings;
            var noOpBefore = languageOnly.CaptureSceneDiagnostics();
            window.PreviewLanguage(AppLanguage.Persian);
            window.PreviewLanguage(AppLanguage.English);
            Control<CheckBox>(window, "Startup").IsChecked = unchangedSettings.LaunchAtLogin;
            Control<Button>(window, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            saveWait.Restart();
            while ((languageOnly.IsApplying || window.IsVisible) && saveWait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            var noOpAfter = languageOnly.CaptureSceneDiagnostics();
            report["noOpRoundTripSaveSucceeds"] = !window.IsVisible && !languageOnly.IsApplying && languageOnly.Settings == unchangedSettings;
            report["noOpSaveKeepsClothIdentity"] = ReferenceEquals(noOpBefore.ClothIdentity, noOpAfter.ClothIdentity);
            report["noOpSaveKeepsNativeSurfaces"] = noOpBefore.Handles.SequenceEqual(noOpAfter.Handles);
            report["noOpSaveKeepsParticlePositions"] = noOpBefore.Positions.SequenceEqual(noOpAfter.Positions);
            report["noOpSaveKeepsRollState"] = noOpBefore.Rolled && noOpAfter.Rolled;
            window.Close(); window = null;

            string blockedParent = Path.Combine(output, "blocked-language-settings-parent.file");
            await File.WriteAllTextAsync(blockedParent, "Isolated diagnostic: this file intentionally blocks a settings directory.");
            var blockedStore = new SettingsService(Path.Combine(blockedParent, "settings"));
            using var failed = new RugController(new() { Language = AppLanguage.English, Sound = false }, blockedStore, new AppLog(output), suppressPresentation: true);
            failed.ToggleRoll();
            var failedSettings = failed.Settings;
            var failedBefore = failed.CaptureSceneDiagnostics();
            bool failedResult = await failed.ApplySettingsAsync(failed.Settings with { Language = AppLanguage.Persian });
            var failedAfter = failed.CaptureSceneDiagnostics();
            report["failedLanguageSaveRejected"] = !failedResult && !failed.IsApplying;
            report["failedLanguageSaveKeepsSettings"] = failed.Settings == failedSettings;
            report["failedLanguageSaveKeepsClothIdentity"] = ReferenceEquals(failedBefore.ClothIdentity, failedAfter.ClothIdentity);
            report["failedLanguageSaveKeepsNativeSurfaces"] = failedBefore.Handles.SequenceEqual(failedAfter.Handles);
            report["failedLanguageSaveKeepsParticlePositions"] = failedBefore.Positions.SequenceEqual(failedAfter.Positions);
            report["failedLanguageSaveKeepsRollState"] = failedBefore.Rolled && failedAfter.Rolled;
        }
        catch (Exception exception)
        {
            report["error"] = exception.ToString();
        }
        finally
        {
            window?.Close();
        }
        report["noAppWindowActivated"] = !focus.AppWasActivated;
        report["foregroundUnchangedDuringCheck"] = Native.GetForegroundWindow() == foreground;
        report["languageChecksPassed"] = !report.ContainsKey("error")
            && report.Where(p => p.Key != "foregroundUnchangedDuringCheck").All(p => p.Value is true);
        report["scope"] = "Programmatic WPF controls and rendered offscreen English/Persian settings at 610px and 500px; isolated settings persistence and actual Cancel/Save handlers; scene and pending-roll preservation after language-only, unchanged, and failed saves. Captures include the HWND's complete visual root and its RTL transform. No pointer, keyboard injection, real desktop screenshots, or startup setting changes. Native font/focus behavior on another machine and manual reading remain separate checks.";
        await File.WriteAllTextAsync(Path.Combine(output, "language-diagnostics.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    private static SettingsWindow CreateWindow(RugController controller)
    {
        var window = new SettingsWindow(controller)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            Width = 610,
            Height = 800,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        long styles = Native.GetWindowLongPtr(handle, Native.GwlExStyle).ToInt64();
        Native.SetWindowLongPtr(handle, Native.GwlExStyle, (nint)(styles | Native.NoActivate | Native.ToolWindow));
        window.Show();
        return window;
    }

    private static FrameworkElement Content(SettingsWindow window) => (FrameworkElement)window.Content;
    private static FrameworkElement CaptureRoot(SettingsWindow window) => PresentationSource.FromVisual(window)?.RootVisual as FrameworkElement ?? window;
    private static T Control<T>(SettingsWindow window, string name) where T : FrameworkElement => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing language diagnostic control: {name}.");
    private static async Task Layout(SettingsWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(35);
        window.UpdateLayout();
    }

    private static async Task<RenderTargetBitmap> Capture(SettingsWindow window, string path)
    {
        await Layout(window);
        // Capture the actual HWND's complete visual root. Rendering SettingsPanel alone
        // omits the Window's RTL transform and incorrectly mirrors both text and art.
        var root = CaptureRoot(window);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.RenderSize.Width)), Math.Max(1, (int)Math.Ceiling(root.RenderSize.Height)), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
        return image;
    }

    private static async Task CapturePeek(SettingsWindow window, string path)
    {
        var scroll = Control<ScrollViewer>(window, "PreferencesScroll");
        var target = Control<ComboBox>(window, "PeekMode");
        window.UpdateLayout();
        double y = target.TransformToAncestor((Visual)scroll.Content).Transform(new Point(0, 0)).Y;
        scroll.ScrollToVerticalOffset(Math.Max(0, y - 45));
        await Capture(window, path);
    }

    private static void SetDraft(SettingsWindow window)
    {
        Control<Slider>(window, "RugScale").Value = 1.35;
        Control<Slider>(window, "Rotation").Value = 25;
        Control<Slider>(window, "Shadow").Value = .42;
        Control<Slider>(window, "Lighting").Value = .61;
        Control<Slider>(window, "Detail").Value = .77;
        Select(Control<ComboBox>(window, "SizeChoice"), RugSize.Large);
        Select(Control<ComboBox>(window, "Quality"), SimulationQuality.Economy);
        Select(Control<ComboBox>(window, "PeekMode"), PeekActivationMode.Toggle);
        Select(Control<ComboBox>(window, "PeekDuration"), 11);
        Control<CheckBox>(window, "Bumps").IsChecked = false;
        Control<CheckBox>(window, "Sound").IsChecked = true;
        Control<CheckBox>(window, "PeekControl").IsChecked = true;
        Control<CheckBox>(window, "PeekAlt").IsChecked = false;
        Control<CheckBox>(window, "PeekShift").IsChecked = true;
        Control<CheckBox>(window, "PeekRightClick").IsChecked = false;
        Control<ComboBox>(window, "PeekKey").Text = "K";
    }

    private static bool MatchesDraft(RugSettings value) => Math.Abs(value.Scale - 1.35f) < .001 && value.Rotation == 25
        && Math.Abs(value.Shadow - .42f) < .001 && Math.Abs(value.Lighting - .61f) < .001 && Math.Abs(value.TextureDetail - .77f) < .001
        && value.Size == RugSize.Large && value.Quality == SimulationQuality.Economy && !value.IconBumps && value.Sound
        && value.PeekMode == PeekActivationMode.Toggle && value.PeekSeconds == 11 && !value.PeekOnRightClick
        && value.PeekShortcut.Key == 'K' && value.PeekShortcut.Modifiers == (PeekModifiers.Control | PeekModifiers.Shift);

    private static object? Semantic(object? value)
    {
        if (value is null || value is Enum or int or string) return value;
        foreach (string name in new[] { "Value", "Mode", "Seconds", "DeviceName" })
            if (value.GetType().GetProperty(name) is { } property) return property.GetValue(value);
        return value;
    }
    private static void Select(ComboBox choice, object value)
    {
        foreach (var item in choice.Items)
            if (Equals(Semantic(item), value)) { choice.SelectedItem = item; return; }
        throw new InvalidOperationException($"Diagnostic choice {value} unavailable in {choice.Name}.");
    }
    private static string Draft(SettingsWindow window)
    {
        var values = new Dictionary<string, string?>();
        foreach (string name in new[] { "Pattern", "SizeChoice", "Quality", "Monitor", "PeekMode", "PeekDuration" })
            values[name] = Semantic(Control<ComboBox>(window, name).SelectedValue)?.ToString();
        foreach (string name in new[] { "RugScale", "Rotation", "Shadow", "Lighting", "Detail" })
            values[name] = Control<Slider>(window, name).Value.ToString("R", CultureInfo.InvariantCulture);
        foreach (string name in new[] { "Bumps", "Sound", "Startup", "PeekControl", "PeekAlt", "PeekShift", "PeekRightClick" })
            values[name] = Control<CheckBox>(window, name).IsChecked?.ToString();
        values["PeekKey"] = Control<ComboBox>(window, "PeekKey").Text;
        values["ImageName"] = Control<TextBlock>(window, "ImageName").Text;
        return JsonSerializer.Serialize(values);
    }

    private static bool BoundsFit(SettingsWindow window)
    {
        var content = Content(window);
        foreach (string name in new[] { "LanguageChoice", "Pattern", "SizeChoice", "Quality", "Monitor", "PeekKey", "PeekMode", "PeekDuration", "CancelButton", "ApplyButton" })
        {
            var element = Control<FrameworkElement>(window, name);
            Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Left < -1 || bounds.Right > content.ActualWidth + 1 || bounds.Width <= 0) return false;
        }
        return true;
    }

    private static bool PreviewOrientation(SettingsWindow window, RenderTargetBitmap image)
    {
        var preview = Control<FrameworkElement>(window, "Preview");
        Rect bounds = preview.TransformToAncestor(CaptureRoot(window)).TransformBounds(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
        int y = (int)(bounds.Top + bounds.Height / 2), left = (int)(bounds.Left + bounds.Width / 2 - bounds.Height * .22), right = (int)(bounds.Left + bounds.Width / 2 + bounds.Height * .22);
        if (y < 0 || y >= image.PixelHeight || left < 0 || right >= image.PixelWidth) return false;
        byte[] leftPixel = new byte[4], rightPixel = new byte[4];
        image.CopyPixels(new Int32Rect(left, y, 1, 1), leftPixel, 4, 0);
        image.CopyPixels(new Int32Rect(right, y, 1, 1), rightPixel, 4, 0);
        return leftPixel[2] > leftPixel[0] + 60 && rightPixel[0] > rightPixel[2] + 60;
    }

    private static void CreateFixture(string path)
    {
        using var bitmap = new SKBitmap(400, 280);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(210, 45, 45));
        using var blue = new SKPaint { Color = new SKColor(40, 80, 220) };
        canvas.DrawRect(new SKRect(200, 0, 400, 280), blue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path); data.SaveTo(stream);
    }

    private static bool HasPersian(string? value) => value?.Any(c => c is >= '\u0600' and <= '\u06ff') == true;
    private sealed class FocusObserver : IDisposable
    {
        private readonly Native.WinEventProc _callback;
        private readonly nint _hook;
        internal bool AppWasActivated { get; private set; }
        internal FocusObserver()
        {
            _callback = (_, _, hwnd, _, _, _, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint process);
                if (process == Environment.ProcessId) AppWasActivated = true;
            };
            _hook = Native.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            if (_hook == 0) throw new System.ComponentModel.Win32Exception("Could not observe foreground events for language diagnostics.");
        }
        public void Dispose() { Native.UnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }
}
