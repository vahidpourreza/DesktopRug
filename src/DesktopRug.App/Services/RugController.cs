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
    private readonly DisplayTopologyService _topology = new();
    private readonly AudioService _audio = new();
    private readonly DispatcherTimer _frames, _maintenance;
    private readonly HwndSource _messages;
    private readonly Native.WinEventProc _foregroundCallback;
    private readonly nint _foregroundHook;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _emergencyRegistered;
    private readonly bool _suppressPresentation;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private ClothSimulation _cloth = null!;
    private RugRenderer _renderer = null!;
    private RugOverlayWindow _visual = null!;
    private RugInputWindow _input = null!;
    private DesktopHost _host;
    private DisplayInfo _display = null!;
    private double _lastFrame, _lastRegion;
    private long _lastIcons;
    private bool _requested = true, _presented, _compact, _locked, _suspended, _disposed, _refreshing;
    private bool _savingPosition;
    private bool _saveRequested;
    private DateTimeOffset _peekUntil;
    private string _iconSignature = "";
    internal RugSettings Settings { get; private set; }
    internal IReadOnlyList<DisplayInfo> Displays { get; private set; }
    internal string Diagnostic { get; private set; } = "Looking for the Windows desktop…";
    internal string HotkeyDiagnostic { get; }
    internal bool PeekActive => _peekUntil > DateTimeOffset.UtcNow;
    internal bool RugRequested => _requested;
    internal event Action? Changed;

    internal RugController(RugSettings settings, SettingsService store, AppLog log, bool suppressPresentation = false)
    {
        _suppressPresentation = suppressPresentation;
        _store = store; _log = log; Settings = settings with { LaunchAtLogin = StartupService.IsEnabled() };
        Displays = _topology.Discover();
        if (Displays.Count == 0) throw new InvalidOperationException("No interactive display is available.");
        _messages = new(new HwndSourceParameters("Desktop Rug commands") { WindowStyle = 0, Width = 0, Height = 0 });
        _messages.AddHook(MessageHook);
        _emergencyRegistered = !suppressPresentation && Native.RegisterHotKey(_messages.Handle, 1, 0x4003, 0x1B); // Ctrl+Alt+Escape
        bool toggle = !suppressPresentation && Native.RegisterHotKey(_messages.Handle, 2, 0x4003, 0x52); // R
        bool peek = !suppressPresentation && Native.RegisterHotKey(_messages.Handle, 3, 0x4003, 0x50); // P
        HotkeyDiagnostic = _emergencyRegistered
            ? $"Emergency hide: Ctrl+Alt+Esc. {(toggle ? "Show / hide: Ctrl+Alt+R." : "Ctrl+Alt+R is in use; use the tray.")} {(peek ? "Peek: Ctrl+Alt+P." : "Ctrl+Alt+P is in use; use the tray.")}"
            : "Ctrl+Alt+Esc is in use by another app. The rug stays hidden until Desktop Rug can reserve its emergency shortcut. Close the conflicting app and relaunch.";
        _frames = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16.6667) };
        _frames.Tick += Frame;
        _maintenance = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _maintenance.Tick += Maintain;
        CreateRug();
        _foregroundCallback = (_, _, _, _, _, _, _) => Application.Current.Dispatcher.BeginInvoke(UpdateVisibility);
        _foregroundHook = Native.SetWinEventHook(3, 3, 0, _foregroundCallback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _tray = new(this, _log, !suppressPresentation);
        if (!suppressPresentation) { _maintenance.Start(); Maintain(this, EventArgs.Empty); }
        _log.Write("information", "started", "Desktop-only overlay; no desktop file operations.");
    }

    private void CreateRug()
    {
        _frames.Interval = TimeSpan.FromMilliseconds(Settings.Quality == SimulationQuality.Economy ? 33.3333 : 16.6667);
        _display = Displays.FirstOrDefault(d => d.DeviceName == Settings.Monitor) ?? Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays[0];
        int cols = Settings.Quality switch { SimulationQuality.Economy => 24, SimulationQuality.High => 44, _ => 36 };
        int rows = Settings.Quality switch { SimulationQuality.Economy => 18, SimulationQuality.High => 32, _ => 26 };
        _cloth = new(cols, rows) { Viewport = _display.SizeDip, SolverIterations = Settings.Quality == SimulationQuality.Economy ? 6 : 9 };
        ResetGeometry();
        try { _renderer = new(_cloth, Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Write("warning", "texture-fallback", ex.GetType().Name);
            Settings = Settings with { Pattern = RugPattern.Kashan };
            _renderer = new(_cloth, Settings);
            Diagnostic = "Custom image unavailable. The original image is unchanged; using Kashan.";
        }
        _visual = new(_display, _cloth, _renderer); _input = new(_display, _cloth);
        _input.PeekRequested += Peek; _input.CompactRequested += ToggleCompact;
        _input.RotateRequested += Rotate; _input.Released += Released;
        _input.Activity += Wake;
        _audio.Enabled = Settings.Sound; _presented = false; _iconSignature = ""; _lastIcons = 0;
    }

    private void ResetGeometry()
    {
        Vector2 view = _display.SizeDip;
        float width = Math.Min(Settings.Width, Math.Min(view.X * .82f, view.Y * 1.05f));
        Vector2 size = new(width, width * (_compact ? .16f : .7f));
        Vector2 center = new((float)Settings.CenterX * view.X, (float)Settings.CenterY * view.Y);
        // Keep enough cloth visible even after display removal or restoring an old position.
        center = Vector2.Clamp(center, new Vector2(width * .22f, size.Y * .25f), view - new Vector2(width * .22f, size.Y * .25f));
        _cloth.Reset(center, size, Settings.Rotation);
    }

    internal void ShowRug() { _requested = true; _peekUntil = default; UpdateVisibility(); Changed?.Invoke(); }
    internal void HideRug() { _requested = false; _peekUntil = default; UpdateVisibility(); Changed?.Invoke(); }
    internal void ToggleRug() { if (_requested && !PeekActive) HideRug(); else ShowRug(); }
    internal void Peek()
    {
        _input.CancelGrab();
        _peekUntil = PeekActive ? default : DateTimeOffset.UtcNow.AddSeconds(Settings.PeekSeconds);
        UpdateVisibility(); Changed?.Invoke();
    }
    internal void Flatten() { _compact = false; ResetGeometry(); Wake(); }
    internal void RestorePosition() { ResetGeometry(); Wake(); }
    private void ToggleCompact() { _compact = !_compact; ResetGeometry(); _audio.Settle(); Wake(); }
    internal void Rotate(int degrees)
    {
        CapturePosition(); Settings = Settings with { Rotation = (Settings.Rotation + degrees + 360) % 360 };
        ResetGeometry(); Wake(); Save(); Changed?.Invoke();
    }

    internal bool ApplySettings(RugSettings requested)
    {
        var next = requested.Validated();
        // Validate texture before changing the live state or touching the startup setting.
        try { using var preview = PatternFactory.Create(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Diagnostic = "That image could not be loaded. Choose a readable PNG or JPEG under 80 megapixels."; Changed?.Invoke(); return false; }
        try
        {
            if (next.LaunchAtLogin != Settings.LaunchAtLogin) StartupService.SetEnabled(next.LaunchAtLogin);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Diagnostic = "Windows did not allow the sign-in setting to change."; Changed?.Invoke(); return false; }
        _frames.Stop();
        _input.Dispose(); _visual.Close(); _renderer.Dispose();
        Settings = next; _compact = false; CreateRug();
        UpdateVisibility(); Save(); Changed?.Invoke(); return true;
    }

    internal void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new(this); _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
    internal async void Exit()
    {
        CapturePosition(); HideRug();
        try { await _store.SaveAsync(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("error", "settings-save", ex.GetType().Name); }
        Application.Current.Shutdown();
    }

    private void Wake()
    {
        _cloth.Wake(); _visual.Redraw(); _input.UpdateRegion();
        if (_presented) { _lastFrame = _clock.Elapsed.TotalSeconds; _frames.Start(); }
    }
    private void UpdateVisibility()
    {
        if (_disposed) return;
        bool allowed = !_suppressPresentation && _requested && !PeekActive && !_locked && !_suspended && _emergencyRegistered && _desktop.IsDesktopForeground(_host);
        if (allowed && !_presented)
        {
            if (!_desktop.PlaceAboveDesktop(_visual.Handle, _input.Handle, _host)) return;
            _visual.Show();
            // Show can change normal z-order; restore it before exposing the input region.
            if (!_desktop.PlaceAboveDesktop(_visual.Handle, _input.Handle, _host)) { _visual.Hide(); return; }
            _input.UpdateRegion(); _input.Show(); _desktop.PlaceAboveDesktop(_visual.Handle, _input.Handle, _host);
            _presented = true; _visual.Redraw();
            if (!_cloth.IsSleeping) { _lastFrame = _clock.Elapsed.TotalSeconds; _frames.Start(); }
        }
        else if (!allowed && _presented)
        {
            _presented = false; _input.Hide(); _visual.Hide(); _frames.Stop();
        }
        else if (allowed && _presented && !Native.IsWindowVisible(_visual.Handle))
        { _presented = false; UpdateVisibility(); }
    }

    private void Frame(object? sender, EventArgs e)
    {
        if (!_presented) { _frames.Stop(); return; }
        double now = _clock.Elapsed.TotalSeconds;
        _input.PollDrag();
        _cloth.Advance(now - _lastFrame); _lastFrame = now;
        _visual.Redraw();
        if (now - _lastRegion > 1d / 30) { _input.UpdateRegion(); _lastRegion = now; }
        if (_cloth.IsGrabbed) _audio.Rustle();
        if (_cloth.IsSleeping)
        {
            _input.UpdateRegion(); _frames.Stop(); CapturePosition(); Save();
        }
    }

    private async void Maintain(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _host = _desktop.Discover(); UpdateVisibility();
        if (_presented && !_cloth.IsSleeping && !_frames.IsEnabled) { _lastFrame = _clock.Elapsed.TotalSeconds; _frames.Start(); }
        if (PeekActive) Changed?.Invoke();
        if (_refreshing || !_requested || _locked || _suspended || Environment.TickCount64 - _lastIcons < 1400) return;
        _lastIcons = Environment.TickCount64;
        _refreshing = true;
        try
        {
            if (!Settings.IconBumps)
            {
                _cloth.Bumps.SetIcons([]); Diagnostic = "Icon-aware bumps are turned off."; Changed?.Invoke(); return;
            }
            var snapshot = await _icons.ReadAsync(_host.IconList).WaitAsync(TimeSpan.FromSeconds(2));
            if (_disposed || !Settings.IconBumps) return;
            Diagnostic = snapshot.Diagnostic;
            // Only rectangle geometry affects simulation. Labels never leave process memory or enter logs.
            string signature = string.Join(';', snapshot.Icons.Select(i => i.Bounds.ToString()));
            if (signature != _iconSignature)
            {
                _iconSignature = signature;
                _cloth.Bumps.SetIcons(snapshot.Icons.Select(i => _display.ScreenToLocal(i.Bounds)));
                Wake();
            }
            Changed?.Invoke();
        }
        catch (TimeoutException) { Diagnostic = "Explorer is taking too long to respond. The rug is available without fresh icon bumps."; Changed?.Invoke(); }
        finally { _refreshing = false; }
    }

    private void Released() { _audio.Settle(); CapturePosition(); Save(); Wake(); }
    private void CapturePosition()
    {
        Vector2 center = _cloth.Center, size = _display.SizeDip;
        Settings = (Settings with { CenterX = center.X / size.X, CenterY = center.Y / size.Y, Monitor = _display.DeviceName }).Validated();
    }
    private async void Save()
    {
        _saveRequested = true;
        if (_savingPosition) return;
        _savingPosition = true;
        try
        {
            while (_saveRequested)
            {
                _saveRequested = false;
                await _store.SaveAsync(Settings);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("error", "settings-save", ex.GetType().Name); Diagnostic = "Settings could not be saved. Check access to local app data."; }
        finally { _savingPosition = false; }
    }
    private nint MessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmHotkey)
        {
            if (wParam == 1) HideRug(); else if (wParam == 2) ToggleRug(); else if (wParam == 3) Peek();
            handled = true;
        }
        if (message is Native.WmDisplayChange or Native.WmDpiChanged) DisplayChanged(this, EventArgs.Empty);
        return 0;
    }
    private void DisplayChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        var displays = _topology.Discover(); if (displays.Count == 0) return;
        Displays = displays; ApplySettings(Settings);
    });
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    { _locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect; UpdateVisibility(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    { _suspended = e.Mode == PowerModes.Suspend; UpdateVisibility(); });
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _frames.Stop(); _maintenance.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        if (_foregroundHook != 0) Native.UnhookWinEvent(_foregroundHook);
        for (int id = 1; id <= 3; id++) Native.UnregisterHotKey(_messages.Handle, id);
        _tray?.Dispose(); _input.Dispose(); _visual.Close(); _renderer.Dispose(); _audio.Dispose(); _messages.Dispose();
        _log.Write("information", "stopped");
    }
}
