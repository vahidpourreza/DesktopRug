using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopRug.App.Interop;
using DesktopRug.App.Rendering;
using DesktopRug.App.Windows;
using DesktopRug.Core;
using Microsoft.Win32;

namespace DesktopRug.App.Services;

internal sealed class RugController : IDisposable
{
    private readonly SettingsService _store;
    private readonly AppLog _log;
    private readonly DesktopWindowService _desktop = new();
    private readonly DesktopIconService _icons = new();
    private readonly DesktopIconWatcher _iconWatcher = new();
    private readonly DisplayTopologyService _topology = new();
    private readonly AudioService _audio = new();
    private readonly PeekState _peek = new();
    private readonly HotkeyService _peekHotkey;
    private readonly RenderClock _frames;
    private readonly DispatcherTimer _maintenance, _iconTimer, _peekTimer;
    private readonly HwndSource _messages;
    private readonly Native.WinEventProc _foregroundCallback;
    private readonly nint _foregroundHook;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _emergencyRegistered, _toggleRegistered, _suppressPresentation;
    private readonly List<MonitorSurface> _surfaces = [];
    private (nint Visual, nint Input)[] _handles = [];
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private ClothSimulation _cloth = null!;
    private RugRenderer _renderer = null!;
    private DesktopHost _host;
    private DisplayInfo _display = null!;
    private RugCoordinateSpace _space;
    private double _lastFrame, _lastRegion;
    private long _lastIcons;
    private long _nextDisplayRetry;
    private bool _displayRefreshPending;
    private bool _requested = true, _presented, _locked, _suspended, _disposed, _refreshing, _applying, _languageOnlyApplying, _rebuilding;
    private bool _savingPosition, _saveRequested, _lastPeek, _landingSoundPending;
    private TaskCompletionSource? _saveCompletion;
    private IconSnapshot? _lastSnapshot;
    private string _iconSignature = "";
    private string? _peekErrorKey;
    private object[] _peekErrorArguments = [];
    private string _diagnosticKey = "Looking for the Windows desktop…";
    private object[] _diagnosticArguments = [];
    internal RugSettings Settings { get; private set; }
    internal IReadOnlyList<DisplayInfo> Displays { get; private set; }
    internal string Diagnostic => GetDiagnostic(AppLanguage.English);
    internal string HotkeyDiagnostic => GetHotkeyDiagnostic(AppLanguage.English);
    internal string GetDiagnostic(AppLanguage language) => UiText.Get(language, _diagnosticKey, _diagnosticArguments);
    internal string GetHotkeyDiagnostic(AppLanguage language)
    {
        if (!_emergencyRegistered && !_suppressPresentation)
            return UiText.Get(language, "Ctrl+Alt+Esc is in use. The rug stays hidden until its emergency shortcut is available. Close the conflicting app and relaunch.");
        return UiText.Get(language, "Emergency hide: Ctrl+Alt+Esc.") + " "
            + UiText.Get(language, _toggleRegistered || _suppressPresentation ? "Show / hide: Ctrl+Alt+R." : "Ctrl+Alt+R is in use; use the tray.") + " "
            + (_peekErrorKey is { } key ? UiText.Get(language, key, _peekErrorArguments)
                : UiText.Get(language, "Peek: {0} ({1}).", Settings.PeekShortcut, UiText.PeekMode(language, Settings.PeekMode)));
    }
    private void SetDiagnostic(string key, params object[] arguments) { _diagnosticKey = key; _diagnosticArguments = arguments; }
    private void CapturePeekError() { _peekErrorKey = _peekHotkey.ErrorKey; _peekErrorArguments = _peekHotkey.ErrorArguments; }
    internal bool PeekActive => _peek.IsActive(DateTimeOffset.UtcNow);
    internal bool RugRequested => _requested;
    internal bool IsApplying => _applying;
    internal bool IsRolled => _cloth.IsRolled;
    internal event Action? Changed;

    /// <summary>Immutable copies for isolated diagnostics; never changes cloth or window state.</summary>
    internal SceneDiagnosticSnapshot CaptureSceneDiagnostics() => new(_cloth, _handles.ToArray(),
        _cloth.Particles.Select(p => p.Position).ToArray(), _cloth.IsRolled);

    internal RugController(RugSettings settings, SettingsService store, AppLog log, bool suppressPresentation = false)
    {
        _suppressPresentation = suppressPresentation;
        _store = store; _log = log; Settings = settings.Validated() with { LaunchAtLogin = StartupService.IsEnabled() };
        Displays = _topology.Discover();
        if (Displays.Count == 0) throw new InvalidOperationException("No interactive display is available.");
        _messages = new(new HwndSourceParameters("Desktop Rug commands") { WindowStyle = 0, Width = 0, Height = 0 });
        _messages.AddHook(MessageHook);
        _emergencyRegistered = !suppressPresentation && Native.RegisterHotKey(_messages.Handle, 1, 0x4003, 0x1B);
        _toggleRegistered = !suppressPresentation && Native.RegisterHotKey(_messages.Handle, 2, 0x4003, 0x52);
        _peekHotkey = new(_messages.Handle, suppressPresentation);
        _peekHotkey.TrySetShortcut(Settings.PeekShortcut, out _); CapturePeekError();
        _frames = new(); _frames.Tick += Frame;
        _maintenance = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) }; _maintenance.Tick += Maintain;
        _iconTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) }; _iconTimer.Tick += RefreshIcons;
        _peekTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) }; _peekTimer.Tick += PeekTick;
        CreateScene();
        _foregroundCallback = (_, _, _, _, _, _, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) UpdateVisibility();
        });
        _foregroundHook = Native.SetWinEventHook(3, 3, 0, _foregroundCallback, 0, 0, 0);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _tray = new(this, _log, !suppressPresentation);
        if (!suppressPresentation) { _maintenance.Start(); _iconTimer.Start(); Maintain(this, EventArgs.Empty); }
        _log.Write("information", "started", "Persistent desktop layer with shared cross-monitor cloth.");
    }

    private void CreateScene()
    {
        _frames.Interval = TimeSpan.FromMilliseconds(Settings.Quality == SimulationQuality.Economy ? 33.3333 : 16.6667);
        _display = Displays.FirstOrDefault(d => d.DeviceName == Settings.Monitor) ?? Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays[0];
        _space = new(RugCoordinateSpace.Union(Displays), (float)_display.DpiScale);
        int cols = Settings.Quality switch { SimulationQuality.Economy => 24, SimulationQuality.High => 44, _ => 36 };
        int rows = Settings.Quality switch { SimulationQuality.Economy => 18, SimulationQuality.High => 32, _ => 26 };
        _cloth = new(cols, rows) { Viewport = _space.Size, SolverIterations = Settings.Quality == SimulationQuality.Economy ? 6 : 9 };
        ResetGeometry();
        try { _renderer = new(_cloth, Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Write("warning", "texture-fallback", ex.GetType().Name);
            Settings = Settings with { Pattern = RugPattern.Kashan }; _renderer = new(_cloth, Settings);
            SetDiagnostic("Custom image unavailable. The original image is unchanged; using Kashan.");
        }
        CreateSurfaces();
        _audio.Enabled = Settings.Sound; _presented = false; _iconSignature = ""; _lastIcons = 0;
        ApplyBumps();
    }
    private void CreateSurfaces()
    {
        foreach (var display in Displays)
        {
            var surface = new MonitorSurface(display, _space, _cloth, _renderer, !_suppressPresentation);
            surface.Input.PeekRequested += () => { if (Settings.PeekOnRightClick) Peek(); };
            surface.Input.CompactRequested += ToggleRoll;
            surface.Input.RotateRequested += Rotate; surface.Input.Released += Released; surface.Input.Activity += Wake;
            surface.Input.DisplayMetricsChanged += () => DisplayChanged(this, EventArgs.Empty);
            surface.Visual.DisplayMetricsChanged += () => DisplayChanged(this, EventArgs.Empty);
            surface.Input.Faulted += reason =>
            {
                _log.Write("error", "input-surface-retry", reason);
                SetDiagnostic("The input surface is recovering. Emergency hide and the tray remain available.");
                _displayRefreshPending = true; _nextDisplayRetry = Environment.TickCount64 + 1000;
                UpdateVisibility();
            };
            _surfaces.Add(surface);
        }
        _handles = _surfaces.Select(s => (s.Visual.Handle, s.Input.Handle)).ToArray();
    }
    private void DisposeSurfaces()
    {
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear(); _handles = [];
    }
    private void ResetGeometry()
    {
        float widthDip = Math.Min(Settings.Width * Settings.Scale, Math.Min(_display.SizeDip.X * .92f, _display.SizeDip.Y * 1.3f));
        Vector2 centerScreen = new((float)(_display.Bounds.X + Settings.CenterX * _display.Bounds.Width), (float)(_display.Bounds.Y + Settings.CenterY * _display.Bounds.Height));
        float width = widthDip * (float)_display.DpiScale / _space.PixelScale;
        Vector2 center = _space.ScreenToWorld(centerScreen);
        center = Vector2.Clamp(center, new(width * .1f), _space.Size - new Vector2(width * .1f));
        _cloth.Reset(center, new(width, width * .7f), Settings.Rotation);
    }

    internal void ShowRug() { _requested = true; _peek.Reset(); UpdateVisibility(); Changed?.Invoke(); }
    internal void HideRug() { _requested = false; _peek.Reset(); HideAll(); Changed?.Invoke(); }
    internal void ToggleRug() { if (_requested && !PeekActive) HideRug(); else ShowRug(); }
    internal void Peek() => ActivatePeek(false);
    private void ActivatePeek(bool keyboard)
    {
        foreach (var surface in _surfaces) surface.Input.CancelGrab();
        _peek.Activate(Settings.PeekMode, TimeSpan.FromSeconds(Settings.PeekSeconds), DateTimeOffset.UtcNow, keyboard);
        _lastPeek = PeekActive; _peekTimer.Start(); UpdateVisibility(); Changed?.Invoke();
    }
    private void PeekTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_peek.KeyboardHeld && !_peekHotkey.IsShortcutDown) _peek.ReleaseKeyboard();
        bool active = PeekActive;
        if (active != _lastPeek) { _lastPeek = active; UpdateVisibility(); Changed?.Invoke(); }
        if (!active) _peekTimer.Stop();
    }
    internal void Flatten() { if (_applying) return; CapturePosition(); ResetGeometry(); Wake(); }
    internal void RestorePosition() { if (_applying) return; ResetGeometry(); Wake(); }
    internal void ToggleRoll() { if (_applying) return; _cloth.SetRolled(!_cloth.IsRolled); _audio.Settle(); Wake(); Changed?.Invoke(); }
    internal void Rotate(int degrees)
    {
        if (_applying) return;
        CapturePosition(); Settings = Settings with { Rotation = (Settings.Rotation + degrees + 360) % 360 };
        ResetGeometry(); Wake(); Save(); Changed?.Invoke();
    }

    internal async Task<bool> ApplySettingsAsync(RugSettings requested)
    {
        if (_applying || _disposed) return false;
        if (requested.PeekShortcut is null || !requested.PeekShortcut.TryValidate(out _))
        { SetDiagnostic("Choose a valid Peek shortcut with Ctrl or Alt."); Changed?.Invoke(); return false; }
        var next = requested.Validated();
        var languageComparison = next with { Language = Settings.Language };
        // The settings monitor selector shows the effective primary display on a first launch,
        // even while the persisted/default monitor identity is still empty.
        if (Settings.Monitor.Length == 0 && languageComparison.Monitor == _display.DeviceName)
            languageComparison = languageComparison with { Monitor = Settings.Monitor };
        // Saving after a language-preview round trip is also a lightweight transaction: an
        // unchanged language must not flatten the rug or recreate its native surfaces.
        if (languageComparison == Settings)
            return await ApplyLanguageAsync(next.Language);
        if (next.Pattern == RugPattern.Custom && next.CustomTexturePath is null)
        { SetDiagnostic("Choose a local image before selecting Custom."); Changed?.Invoke(); return false; }
        _applying = true; HideAll(); Changed?.Invoke();
        var previous = Settings;
        bool loginChanged = false, bindingChanged = false;
        try
        {
            if (_saveCompletion is { } pendingSave) await pendingSave.Task;
            using var preview = await Task.Run(() => PatternFactory.Create(next));
            var currentDisplays = _topology.Discover();
            if (currentDisplays.Count == 0) throw new InvalidOperationException("No interactive display is currently available.");
            if (!_peekHotkey.TrySetShortcut(next.PeekShortcut, out string? error))
            { SetDiagnostic(_peekHotkey.ErrorKey ?? error!, _peekHotkey.ErrorArguments); return false; }
            bindingChanged = true;
            if (next.LaunchAtLogin != previous.LaunchAtLogin)
            { StartupService.SetEnabled(next.LaunchAtLogin); loginChanged = true; }
            await _store.SaveAsync(next);
            _rebuilding = true; HideAll(); DisposeSurfaces(); _renderer.Dispose();
            Displays = currentDisplays; _displayRefreshPending = false;
            Settings = next; _peek.Reset(); _peekErrorKey = null; _peekErrorArguments = []; CreateScene();
            SetDiagnostic("Settings saved."); _tray?.RefreshLanguage();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Write("error", "apply-settings", ex.GetType().Name);
            SetDiagnostic("Changes could not be saved or applied. Previous settings were retained. Check the image and local app-data access.");
            if (bindingChanged) { _peekHotkey.TrySetShortcut(previous.PeekShortcut, out _); CapturePeekError(); }
            if (loginChanged)
            {
                try { StartupService.SetEnabled(previous.LaunchAtLogin); }
                catch (Exception rollback) { _log.Write("error", "startup-rollback", rollback.GetType().Name); }
            }
            if (_rebuilding)
            {
                DisposeSurfaces(); _renderer.Dispose(); Settings = previous; CreateScene();
                try { await _store.SaveAsync(previous); }
                catch (Exception rollback) { _log.Write("error", "settings-rollback", rollback.GetType().Name); }
            }
            return false;
        }
        finally { _rebuilding = false; _applying = false; UpdateVisibility(); Changed?.Invoke(); }
    }

    private async Task<bool> ApplyLanguageAsync(AppLanguage language)
    {
        _applying = true; _languageOnlyApplying = true; Changed?.Invoke();
        RugSettings? persisted = null;
        bool saved = false;
        try
        {
            if (_saveCompletion is { } pendingSave) await pendingSave.Task;
            persisted = Settings with { Language = language };
            await _store.SaveAsync(persisted);
            // Movement may complete while disk I/O is pending. Preserve its latest coordinates,
            // cloth state, monitor frame, Peek state, and native resources.
            Settings = Settings with { Language = language };
            saved = true;
            SetDiagnostic("Settings saved.");
            _tray?.RefreshLanguage();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Write("error", "language-save", ex.GetType().Name);
            SetDiagnostic("Changes could not be saved or applied. Previous settings were retained. Check the image and local app-data access.");
            return false;
        }
        finally
        {
            _languageOnlyApplying = false; _applying = false;
            // A position save suppressed during the language transaction must not be lost.
            if (saved && persisted is not null && Settings != persisted) Save();
            UpdateVisibility(); Changed?.Invoke();
        }
    }

    internal void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new(this); _settingsWindow.Closed += (_, _) => _settingsWindow = null; _settingsWindow.Show();
    }
    internal async void Exit()
    {
        if (_applying) { SetDiagnostic("Wait for the current settings save before exiting."); Changed?.Invoke(); return; }
        CapturePosition(); HideRug();
        try
        {
            if (_saveCompletion is { } pendingSave) await pendingSave.Task;
            await _store.SaveAsync(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("error", "settings-save", ex.GetType().Name); }
        Application.Current.Shutdown();
    }
    private void Wake()
    {
        if (_disposed || _rebuilding) return;
        _cloth.Wake(); UpdateVisibility();
        foreach (var surface in _surfaces) { surface.Visual.Redraw(); surface.Input.UpdateRegion(); }
        if (_presented) { _lastFrame = _clock.Elapsed.TotalSeconds; _frames.Start(); }
    }
    private RectD ClothScreenBounds()
    {
        float left = float.PositiveInfinity, top = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.NegativeInfinity;
        foreach (ref readonly var p in _cloth.Particles.AsSpan())
        {
            Vector2 point = _space.WorldToScreen(RugGeometry.Project(p.Position));
            left = Math.Min(left, point.X); top = Math.Min(top, point.Y); right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y);
        }
        float margin = 100 * _space.PixelScale;
        return new(left - margin, top - margin, right - left + margin * 2, bottom - top + margin * 2);
    }
    private void HideAll()
    {
        _presented = false; _frames.Stop();
        foreach (var surface in _surfaces) { surface.Presented = false; surface.Input.Hide(); surface.Visual.Hide(); }
    }
    private void UpdateVisibility()
    {
        if (_disposed || _rebuilding) return;
        bool allowed = !_suppressPresentation && (!_applying || _languageOnlyApplying) && !_displayRefreshPending && _requested && !PeekActive && !_locked && !_suspended
            && _emergencyRegistered && _desktop.IsAvailable(_host);
        if (!allowed) { if (_presented) HideAll(); return; }
        RectD rug = ClothScreenBounds();
        bool showed = false, any = false;
        foreach (var surface in _surfaces)
        {
            RectD intersection = RugCoordinateSpace.Intersect(rug, surface.Display.Bounds);
            bool intersects = intersection.Width > 0 && intersection.Height > 0;
            if (intersects)
            {
                any = true;
                if (!surface.Presented || !Native.IsWindowVisible(surface.Visual.Handle))
                {
                    surface.Visual.Opacity = 0; surface.Visual.Show(); Native.ShowWindow(surface.Visual.Handle, 8);
                    surface.Input.UpdateRegion(); surface.Presented = true; showed = true;
                }
                bool wasOccluded = surface.Occluded;
                surface.Occluded = _desktop.IsFullyOccluded(surface.Visual.Handle, intersection);
                if (wasOccluded && !surface.Occluded) surface.Visual.Redraw();
            }
            else if (surface.Presented || Native.IsWindowVisible(surface.Input.Handle))
            {
                surface.Presented = false; surface.Visual.Hide();
                if (!surface.Input.IsDragging) surface.Input.Hide();
            }
        }
        if ((showed || !_desktop.IsLayerCorrect(_handles, _host)) && !_desktop.PlaceAboveDesktop(_handles, _host))
        { HideAll(); return; }
        foreach (var surface in _surfaces)
            if (surface.Presented)
            {
                if (!Native.IsWindowVisible(surface.Input.Handle)) { surface.Input.Show(); showed = true; }
                surface.Visual.Opacity = 1;
            }
        if (showed && !_desktop.PlaceAboveDesktop(_handles, _host)) { HideAll(); return; }
        _presented = any || _cloth.IsGrabbed;
        bool animate = _presented && !_cloth.IsSleeping && (_cloth.IsGrabbed || _surfaces.Any(s => s.Presented && !s.Occluded));
        if (animate && !_frames.IsEnabled) { _lastFrame = _clock.Elapsed.TotalSeconds; _frames.Start(); }
        else if (!animate) _frames.Stop();
    }
    private void Frame(object? sender, EventArgs e)
    {
        if (!_presented || (_applying && !_languageOnlyApplying)) { _frames.Stop(); return; }
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var surface in _surfaces) surface.Input.PollDrag();
        _cloth.Advance(now - _lastFrame); _lastFrame = now;
        if (now - _lastRegion > 1d / 30)
        {
            foreach (var surface in _surfaces) surface.Input.UpdateRegion();
            _lastRegion = now;
            UpdateVisibility();
        }
        foreach (var surface in _surfaces) if (surface.Presented && !surface.Occluded) surface.Visual.Redraw();
        if (_cloth.IsGrabbed && _cloth.MeanMotion > .05f) _audio.Rustle();
        if (_landingSoundPending && !_cloth.IsGrabbed && _cloth.MeanMotion < .08f)
        { _landingSoundPending = false; _audio.Settle(); }
        if (_cloth.IsSleeping) { _frames.Stop(); CapturePosition(); Save(); }
    }
    private void Maintain(object? sender, EventArgs e)
    {
        if (_disposed || _applying) return;
        if (_displayRefreshPending && Environment.TickCount64 >= _nextDisplayRetry) RefreshDisplays();
        _host = _desktop.Discover(); _iconWatcher.Watch(_host); UpdateVisibility();
    }
    private async void RefreshIcons(object? sender, EventArgs e)
    {
        if (_disposed || _refreshing || _applying || !_requested || _locked || _suspended || !Settings.IconBumps) return;
        bool desktopDrag = !_cloth.IsGrabbed && _desktop.IsDesktopForeground(_host) && (Native.GetAsyncKeyState(1) & 0x8000) != 0;
        long interval = _iconWatcher.Dragging || desktopDrag ? 50 : _iconWatcher.HasChanges ? 75 : 3000;
        if (Environment.TickCount64 - _lastIcons < interval) return;
        _lastIcons = Environment.TickCount64; _iconWatcher.Acknowledge(); _refreshing = true;
        try
        {
            var snapshot = await _icons.ReadAsync(_host.IconList).WaitAsync(TimeSpan.FromSeconds(1));
            if (_disposed || _applying || !Settings.IconBumps) return;
            _lastSnapshot = snapshot; SetDiagnostic(snapshot.DiagnosticKey, snapshot.DiagnosticArguments ?? []);
            string signature = string.Join(';', snapshot.Icons.Select(i => i.Bounds.ToString()));
            if (signature != _iconSignature) { _iconSignature = signature; ApplyBumps(); Wake(); }
            Changed?.Invoke();
        }
        catch (TimeoutException) { SetDiagnostic("Explorer is slow to respond. The rug remains usable; icon tracking will recover when Explorer responds."); Changed?.Invoke(); }
        finally { _refreshing = false; }
    }
    private void ApplyBumps() => _cloth.Bumps.SetIcons(Settings.IconBumps && _lastSnapshot is { } snapshot
        ? snapshot.Icons.Select(i => _space.ScreenToWorld(i.Bounds)) : []);
    private void Released()
    {
        if (_disposed || _rebuilding) return;
        _landingSoundPending = true; CapturePosition(); Save(); Wake();
    }
    private void CapturePosition()
    {
        if (_rebuilding) return;
        Vector2 center = _space.WorldToScreen(_cloth.Center);
        var display = RugCoordinateSpace.NearestDisplay(Displays, center);
        float scale = _cloth.MaterialSize.X * _space.PixelScale / (Settings.Width * (float)display.DpiScale);
        Settings = (Settings with
        {
            CenterX = (center.X - display.Bounds.X) / display.Bounds.Width,
            CenterY = (center.Y - display.Bounds.Y) / display.Bounds.Height,
            Monitor = display.DeviceName,
            Scale = scale
        }).Validated();
        _display = display;
        if (!_cloth.IsGrabbed && Math.Abs(_space.PixelScale - display.DpiScale) > .001)
        {
            var next = new RugCoordinateSpace(_space.ScreenBounds, (float)display.DpiScale);
            _cloth.Rebase(_space.Origin, _space.PixelScale, next.Origin, next.PixelScale); _space = next; _cloth.Viewport = next.Size;
            foreach (var surface in _surfaces) { surface.Visual.SetSpace(next); surface.Input.SetSpace(next); }
            ApplyBumps();
        }
    }
    private async void Save()
    {
        if (_disposed || _applying) return;
        _saveRequested = true;
        if (_savingPosition) return;
        _savingPosition = true;
        _saveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { while (_saveRequested) { _saveRequested = false; await _store.SaveAsync(Settings); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.Write("error", "settings-save", ex.GetType().Name); SetDiagnostic("Settings could not be saved. Check local app-data access."); }
        finally { _savingPosition = false; _saveCompletion.TrySetResult(); }
    }
    private nint MessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmHotkey)
        {
            if (wParam == 1) HideRug(); else if (wParam == 2) ToggleRug(); else if (_peekHotkey.IsPeekMessage(wParam)) ActivatePeek(true);
            handled = true;
        }
        if (message is Native.WmDisplayChange or Native.WmDpiChanged) DisplayChanged(this, EventArgs.Empty);
        return 0;
    }
    private void DisplayChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _displayRefreshPending = true; _nextDisplayRetry = 0;
        UpdateVisibility();
    });
    private void RefreshDisplays()
    {
        if (_disposed || _applying || _rebuilding) return;
        bool recovered = false;
        try
        {
            var displays = _topology.Discover();
            if (displays.Count == 0) { HideAll(); _nextDisplayRetry = Environment.TickCount64 + 2000; return; }
            CapturePosition(); _rebuilding = true; HideAll(); DisposeSurfaces();
            Displays = displays;
            var next = new RugCoordinateSpace(RugCoordinateSpace.Union(displays), _space.PixelScale);
            _cloth.Rebase(_space.Origin, _space.PixelScale, next.Origin, next.PixelScale); _space = next; _cloth.Viewport = next.Size;
            Vector2 screenCenter = next.WorldToScreen(_cloth.Center);
            if (!displays.Any(d => d.Bounds.Contains(screenCenter)))
            {
                var nearest = RugCoordinateSpace.NearestDisplay(displays, screenCenter);
                _cloth.Translate(next.ScreenToWorld(nearest.Bounds.Center) - _cloth.Center);
            }
            CreateSurfaces(); ApplyBumps(); _displayRefreshPending = false; recovered = true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            DisposeSurfaces(); _displayRefreshPending = true; _nextDisplayRetry = Environment.TickCount64 + 2000;
            _log.Write("warning", "display-rebuild-retry", ex.GetType().Name);
            SetDiagnostic("The display configuration is changing. The rug is hidden while its surfaces recover.");
        }
        finally { _rebuilding = false; }
        if (recovered) { CapturePosition(); Save(); Wake(); Changed?.Invoke(); }
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    { _locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect; UpdateVisibility(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    { _suspended = e.Mode == PowerModes.Suspend; UpdateVisibility(); });
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _frames.Stop(); _maintenance.Stop(); _iconTimer.Stop(); _peekTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        if (_foregroundHook != 0) Native.UnhookWinEvent(_foregroundHook);
        Native.UnregisterHotKey(_messages.Handle, 1); Native.UnregisterHotKey(_messages.Handle, 2);
        _peekHotkey.Dispose(); _iconWatcher.Dispose(); _tray?.Dispose(); DisposeSurfaces(); _renderer.Dispose(); _audio.Dispose(); _messages.Dispose();
        _log.Write("information", "stopped");
    }

    private sealed class MonitorSurface : IDisposable
    {
        internal DisplayInfo Display { get; }
        internal RugOverlayWindow Visual { get; }
        internal RugInputWindow Input { get; }
        internal bool Presented { get; set; }
        internal bool Occluded { get; set; }
        internal MonitorSurface(DisplayInfo display, RugCoordinateSpace space, ClothSimulation cloth, RugRenderer renderer, bool capture)
        {
            Display = display; Visual = new(display, cloth, renderer, space);
            try { Input = new(display, cloth, space, capture); }
            catch { Visual.Close(); throw; }
        }
        public void Dispose() { Input.Dispose(); Visual.Close(); }
    }
}

internal sealed record SceneDiagnosticSnapshot(object ClothIdentity, (nint Visual, nint Input)[] Handles, Vector3[] Positions, bool Rolled);
