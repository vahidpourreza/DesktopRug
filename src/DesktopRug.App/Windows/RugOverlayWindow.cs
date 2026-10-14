using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopRug.App.Interop;
using DesktopRug.App.Rendering;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Windows;

internal sealed class RugOverlayWindow : Window
{
    private readonly SkiaCanvas _surface = new();
    private readonly RugRenderer _renderer;
    private readonly ClothSimulation _cloth;
    private DisplayInfo _display;
    internal nint Handle { get; }

    internal RugOverlayWindow(DisplayInfo display, ClothSimulation cloth, RugRenderer renderer)
    {
        _display = display; _cloth = cloth; _renderer = renderer;
        Title = "Desktop Rug visual"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        Width = display.SizeDip.X; Height = display.SizeDip.Y; Content = _surface;
        _surface.Paint += Paint;
        Handle = new WindowInteropHelper(this).EnsureHandle();
        long styles = Native.GetWindowLongPtr(Handle, Native.GwlExStyle).ToInt64();
        Native.SetWindowLongPtr(Handle, Native.GwlExStyle, (nint)(styles | Native.ToolWindow | Native.NoActivate | Native.Transparent));
        HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        Position(display);
    }
    internal void Position(DisplayInfo display)
    {
        _display = display;
        Width = display.SizeDip.X; Height = display.SizeDip.Y;
        Native.SetWindowPos(Handle, 0, (int)display.Bounds.X, (int)display.Bounds.Y,
            (int)display.Bounds.Width, (int)display.Bounds.Height, Native.NoActivation | Native.NoZOrder);
    }
    internal void Redraw() => _surface.InvalidateVisual();
    protected override void OnClosed(EventArgs e)
    {
        _surface.Paint -= Paint;
        base.OnClosed(e);
    }
    private void Paint(SKCanvas canvas, int width, int height)
    {
        canvas.Scale(width / _display.SizeDip.X, height / _display.SizeDip.Y);
        _renderer.Draw(canvas, _cloth.IsSleeping ? 1 : _cloth.Interpolation);
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmMouseActivate) { handled = true; return 3; } // MA_NOACTIVATE
        return 0;
    }
}
