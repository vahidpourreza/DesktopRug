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
    private readonly Native.Point[] _points;
    private readonly int[] _counts;
    private Vector2 _start, _last;
    private bool _corner;
    private bool _regionValid;
    private long _lastClick;
    private Vector2 _lastClickPoint;
    internal event Action? PeekRequested, CompactRequested, Released, Activity;
    internal event Action<int>? RotateRequested;
    internal bool IsDragging => _cloth.IsGrabbed;
    internal nint Handle { get; private set; }

    internal RugInputWindow(DisplayInfo display, ClothSimulation cloth)
    {
        _display = display; _cloth = cloth;
        int triangles = (cloth.Columns - 1) * (cloth.Rows - 1) * 2;
        _points = new Native.Point[triangles * 3]; _counts = Enumerable.Repeat(3, triangles).ToArray();
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

    internal nint BuildRegion()
    {
        int index = 0;
        void Triangle(int a, int b, int c)
        {
            Vector2 p = RugGeometry.Project(_cloth.Particles[a].Position) * (float)_display.DpiScale;
            Vector2 q = RugGeometry.Project(_cloth.Particles[b].Position) * (float)_display.DpiScale;
            Vector2 r = RugGeometry.Project(_cloth.Particles[c].Position) * (float)_display.DpiScale;
            // Every projected triangle has the same winding, so folded triangles UNION instead of cancelling.
            if ((q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X) < 0) (q, r) = (r, q);
            _points[index++] = new((int)MathF.Round(p.X), (int)MathF.Round(p.Y));
            _points[index++] = new((int)MathF.Round(q.X), (int)MathF.Round(q.Y));
            _points[index++] = new((int)MathF.Round(r.X), (int)MathF.Round(r.Y));
        }
        for (int row = 0; row < _cloth.Rows - 1; row++) for (int col = 0; col < _cloth.Columns - 1; col++)
        {
            int a = row * _cloth.Columns + col, b = a + 1, c = a + _cloth.Columns, d = c + 1;
            Triangle(a, b, c); Triangle(b, d, c);
        }
        return Native.CreatePolyPolygonRgn(_points, _counts, _counts.Length, 2);
    }
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
        if (!_cloth.IsGrabbed) return;
        _cloth.EndGrab(); Native.ReleaseCapture(); Released?.Invoke();
    }

    /// <summary>Non-activating windows cannot rely on foreground mouse capture outside their region.</summary>
    internal void PollDrag()
    {
        if (!_cloth.IsGrabbed) return;
        if ((Native.GetAsyncKeyState(1) & 0x8000) == 0) { CancelGrab(); return; }
        if (Native.GetCursorPos(out var screen)) UpdateDrag(_display.ScreenToLocal(new Vector2(screen.X, screen.Y)));
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
        catch
        {
            // Exceptions must never escape a native callback or leave an invisible blocking window.
            _regionValid = false; Native.ShowWindow(hwnd, 0);
            return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmMouseActivate) { handled = true; return 3; }
        if (message == 0x20) { Native.SetCursor(Native.LoadCursor(0, 32649)); handled = true; return 1; } // hand
        if (message == Native.WmCaptureChanged) { if (_cloth.IsGrabbed) { _cloth.EndGrab(); Released?.Invoke(); } return 0; }
        if (message is not (Native.WmLButtonDown or Native.WmLButtonUp or Native.WmMouseMove or Native.WmRButtonUp or Native.WmMouseWheel)) return 0;
        Native.GetCursorPos(out var screen);
        Vector2 pointer = _display.ScreenToLocal(new Vector2(screen.X, screen.Y));
        if (message == Native.WmLButtonDown)
        {
            long now = Environment.TickCount64;
            if (now - _lastClick < 400 && Vector2.Distance(pointer, _lastClickPoint) < 6)
            { CancelGrab(); CompactRequested?.Invoke(); _lastClick = 0; handled = true; return 0; }
            _lastClick = now; _lastClickPoint = pointer;
            int index = _cloth.Pick(pointer);
            if (index >= 0)
            {
                int c = index % _cloth.Columns, r = index / _cloth.Columns;
                _corner = (c < 4 || c >= _cloth.Columns - 4) && (r < 4 || r >= _cloth.Rows - 4);
                _start = _last = pointer; _cloth.BeginGrab(index, pointer, _corner ? 70 : 15, _corner ? 45 : 80);
                Native.SetCapture(Handle);
                Activity?.Invoke();
            }
        }
        else if (message == Native.WmMouseMove && _cloth.IsGrabbed)
            UpdateDrag(pointer);
        else if (message == Native.WmLButtonUp) CancelGrab();
        else if (message == Native.WmRButtonUp) { CancelGrab(); PeekRequested?.Invoke(); }
        else if (message == Native.WmMouseWheel) { CancelGrab(); RotateRequested?.Invoke((short)((long)wParam >> 16) > 0 ? 5 : -5); }
        handled = true; return 0;
    }
    public void Dispose()
    {
        if (Handle == 0) return;
        CancelGrab(); Native.DestroyWindow(Handle); Handle = 0;
        Native.UnregisterClass(_className, _module); GC.KeepAlive(_procedure);
    }
}
