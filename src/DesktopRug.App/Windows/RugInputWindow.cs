using System.Numerics;
using System.Runtime.InteropServices;
using DesktopRug.App.Interop;
using DesktopRug.Core;

namespace DesktopRug.App.Windows;

internal sealed class RugInputWindow : IDisposable
{
    private readonly Native.WindowProc _procedure;
    private readonly string _className = "DesktopRug.Input." + Guid.NewGuid().ToString("N");
    private readonly nint _module = Native.GetModuleHandle(null);
    private readonly ClothSimulation _cloth;
    private DisplayInfo _display;
    private RugCoordinateSpace _space;
    private readonly bool _allowOsCapture;
    private readonly RugHitRegion _hitRegion;
    private Vector2 _start, _last;
    private bool _corner;
    private bool _regionValid;
    private bool _ownsGrab;
    private uint? _pointerId;
    private long _lastPointerEvent;
    private long _lastClick;
    private Vector2 _lastClickPoint;
    internal event Action? PeekRequested, CompactRequested, Released, Activity;
    internal event Action<int>? RotateRequested;
    internal event Action? DisplayMetricsChanged;
    internal event Action<string>? Faulted;
    internal bool IsDragging => _ownsGrab && _cloth.IsGrabbed;
    internal nint Handle { get; private set; }

    internal RugInputWindow(DisplayInfo display, ClothSimulation cloth, RugCoordinateSpace? space = null, bool allowOsCapture = true)
    {
        _display = display; _cloth = cloth;
        _space = space ?? new(display.Bounds, (float)display.DpiScale); _allowOsCapture = allowOsCapture;
        _hitRegion = new(cloth);
        _procedure = WindowProcedure;
        var windowClass = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Name = _className,
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = _module,
            Background = Native.GetStockObject(4),
            Cursor = Native.LoadCursor(0, 32649)
        };
        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Handle = Native.CreateWindowEx((uint)(Native.ToolWindow | Native.NoActivate | Native.Layered), _className,
            "Desktop Rug input", 0x80000000, (int)display.Bounds.X, (int)display.Bounds.Y,
            (int)display.Bounds.Width, (int)display.Bounds.Height, 0, 0, _module, 0);
        if (Handle == 0)
        {
            int error = Marshal.GetLastWin32Error(); Native.UnregisterClass(_className, _module);
            throw new System.ComponentModel.Win32Exception(error);
        }
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 1, 2))
        {
            int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error);
        }
        UpdateRegion();
    }

    internal void Position(DisplayInfo display)
    {
        _display = display;
        Native.SetWindowPos(Handle, 0, (int)display.Bounds.X, (int)display.Bounds.Y,
            (int)display.Bounds.Width, (int)display.Bounds.Height, Native.NoActivation | Native.NoZOrder);
        UpdateRegion();
    }
    internal void SetSpace(RugCoordinateSpace space)
    {
        var oldSpace = _space;
        _space = space;
        _start = space.ScreenToWorld(oldSpace.WorldToScreen(_start));
        _last = space.ScreenToWorld(oldSpace.WorldToScreen(_last));
        UpdateRegion();
    }

    internal nint BuildRegion() => _hitRegion.Create(_display, _space);
    internal void UpdateRegion()
    {
        nint region = BuildRegion();
        _regionValid = region != 0 && Native.SetWindowRgn(Handle, region, false) != 0;
        if (!_regionValid) { if (region != 0) Native.DeleteObject(region); Hide(); }
        // On success Windows owns and eventually deletes the HRGN.
    }
    internal void Show() { if (_regionValid) Native.ShowWindow(Handle, 8); }
    internal void Hide() { CancelGrab(); Native.ShowWindow(Handle, 0); }
    internal void CancelGrab()
    {
        bool pointerGesture = _pointerId is not null;
        _pointerId = null;
        if (pointerGesture) _lastPointerEvent = Environment.TickCount64;
        if (!_ownsGrab) return;
        _ownsGrab = false;
        _cloth.EndGrab(); if (_allowOsCapture && !pointerGesture) Native.ReleaseCapture(); Released?.Invoke();
    }

    /// <summary>Non-activating windows cannot rely on foreground mouse capture outside their region.</summary>
    internal void PollDrag()
    {
        if (!IsDragging || !_allowOsCapture || _pointerId is not null) return;
        if ((Native.GetAsyncKeyState(1) & 0x8000) == 0) { CancelGrab(); return; }
        if (Native.GetCursorPos(out var screen)) UpdateDrag(_space.ScreenToWorld(new Vector2(screen.X, screen.Y)));
    }
    private void UpdateDrag(Vector2 pointer)
    {
        Vector2 delta = pointer - _last;
        if (delta.LengthSquared() < .001f) return;
        if (!_corner)
        {
            float length = delta.Length(); if (length > 45) delta *= 45 / length;
            _cloth.Translate(delta * .72f);
        }
        float height = _corner ? Math.Clamp(65 + Vector2.Distance(_start, pointer) * .30f, 65, 200) : 15;
        _cloth.SetGrabTarget(pointer, height); _last = pointer;
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        bool handled = false;
        try
        {
            nint result = Hook(hwnd, (int)message, wParam, lParam, ref handled);
            return handled ? result : Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // Exceptions must never escape a native callback or leave an invisible blocking window.
            _regionValid = false; Native.ShowWindow(hwnd, 0);
            try { Faulted?.Invoke(exception.GetType().Name); } catch { }
            return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmMouseActivate) { handled = true; return 3; }
        if (message == Native.WmDpiChanged) DisplayMetricsChanged?.Invoke();
        if (message == PointerInput.WmPointerActivate) { handled = true; return PointerInput.NoActivate; }
        if (PointerInput.IsPointerMessage(message))
        {
            uint id = PointerInput.GetPointerId(wParam);
            if (PointerInput.TryRead(hwnd, message, wParam, lParam, out var sample))
            {
                handled = HandlePointer(sample); return 0;
            }
            if (_pointerId == id) { CancelGrab(); handled = true; }
            return 0;
        }
        if (message == 0x20) { if (_allowOsCapture) Native.SetCursor(Native.LoadCursor(0, 32649)); handled = true; return 1; } // hand
        if (message == Native.WmCaptureChanged)
        {
            _pointerId = null;
            if (_ownsGrab) { _ownsGrab = false; _cloth.EndGrab(); Released?.Invoke(); }
            return 0;
        }
        if (message is not (Native.WmLButtonDown or Native.WmLButtonUp or Native.WmMouseMove or Native.WmRButtonUp or Native.WmMouseWheel)) return 0;
        if ((_pointerId is not null || Environment.TickCount64 - _lastPointerEvent < 500) && PointerInput.IsPromotedMouseMessage())
        { handled = true; return 0; }
        var screen = new Native.Point((short)((long)lParam & 0xffff), (short)(((long)lParam >> 16) & 0xffff));
        if (message != Native.WmMouseWheel) Native.ClientToScreen(hwnd, ref screen);
        Vector2 pointer = _space.ScreenToWorld(new Vector2(screen.X, screen.Y));
        if (message == Native.WmLButtonDown)
        {
            long now = Environment.TickCount64;
            if (now - _lastClick < Native.GetDoubleClickTime() && Vector2.Distance(pointer, _lastClickPoint) < 6)
            { CancelGrab(); CompactRequested?.Invoke(); _lastClick = 0; handled = true; return 0; }
            _lastClick = now; _lastClickPoint = pointer;
            BeginDrag(pointer, false);
        }
        else if (message == Native.WmMouseMove && IsDragging)
            UpdateDrag(pointer);
        else if (message == Native.WmLButtonUp) CancelGrab();
        else if (message == Native.WmRButtonUp) { CancelGrab(); PeekRequested?.Invoke(); }
        else if (message == Native.WmMouseWheel) { CancelGrab(); RotateRequested?.Invoke((short)((long)wParam >> 16) > 0 ? 5 : -5); }
        handled = true; return 0;
    }
    private bool BeginDrag(Vector2 pointer, bool pointerGesture)
    {
        int index = _cloth.Pick(pointer);
        if (index < 0 || (_cloth.IsGrabbed && !_ownsGrab)) return false;
        int c = index % _cloth.Columns, r = index / _cloth.Columns;
        _corner = (c < 4 || c >= _cloth.Columns - 4) && (r < 4 || r >= _cloth.Rows - 4);
        _start = _last = pointer; _cloth.BeginGrab(index, pointer, _corner ? 70 : 15, _corner ? 45 : 80);
        _ownsGrab = true;
        if (_allowOsCapture && !pointerGesture) Native.SetCapture(Handle);
        Activity?.Invoke(); return true;
    }
    internal bool HandlePointer(PointerSample sample)
    {
        if (sample.Phase is PointerPhase.Enter or PointerPhase.Leave) return _pointerId == sample.Id;
        if (_pointerId is { } active && active != sample.Id) return true;
        _lastPointerEvent = Environment.TickCount64;
        if (sample.Phase == PointerPhase.Down)
        {
            if (_cloth.IsGrabbed) return true;
            _pointerId = sample.Id;
            if (sample.IsSecondary) { PeekRequested?.Invoke(); return true; }
            if (!BeginDrag(_space.ScreenToWorld(sample.ScreenPosition), true)) _pointerId = null;
            return _pointerId is not null;
        }
        if (_pointerId != sample.Id) return false;
        if (sample.Phase is PointerPhase.Up or PointerPhase.Canceled)
        {
            if (_ownsGrab) CancelGrab();
            _pointerId = null; return true;
        }
        if (IsDragging && sample.InContact)
        {
            Vector2 pointer = _space.ScreenToWorld(sample.ScreenPosition);
            UpdateDrag(pointer);
            if (_corner && sample.Pressure is { } pressure) _cloth.SetGrabTarget(pointer, 65 + (1 - pressure) * 120);
        }
        return true;
    }
    public void Dispose()
    {
        if (Handle == 0) return;
        CancelGrab(); Native.DestroyWindow(Handle); Handle = 0;
        Native.UnregisterClass(_className, _module); GC.KeepAlive(_procedure);
    }
}
