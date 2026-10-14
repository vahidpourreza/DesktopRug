using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

/// <summary>A small CPU Skia-to-WPF bridge; no OpenGL context, interop child surface or extra graphics packages.</summary>
public sealed class SkiaCanvas : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    public event Action<SKCanvas, int, int>? Paint;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
        _bitmap.Lock();
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                _bitmap.BackBuffer, _bitmap.BackBufferStride);
            surface.Canvas.Clear(SKColors.Transparent);
            Paint?.Invoke(surface.Canvas, width, height);
            surface.Canvas.Flush();
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { _bitmap.Unlock(); }
        drawingContext.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
