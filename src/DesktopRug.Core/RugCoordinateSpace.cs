using System.Numerics;

namespace DesktopRug.Core;

/// <summary>One simulation coordinate frame shared by every physical monitor surface.</summary>
public readonly record struct RugCoordinateSpace(RectD ScreenBounds, float PixelScale)
{
    public Vector2 Origin => new((float)ScreenBounds.X, (float)ScreenBounds.Y);
    public Vector2 Size => new((float)ScreenBounds.Width / PixelScale, (float)ScreenBounds.Height / PixelScale);
    public Vector2 ScreenToWorld(Vector2 point) => (point - Origin) / PixelScale;
    public Vector2 WorldToScreen(Vector2 point) => point * PixelScale + Origin;
    public RectD ScreenToWorld(RectD rect)
    {
        Vector2 point = ScreenToWorld(new Vector2((float)rect.X, (float)rect.Y));
        return new(point.X, point.Y, rect.Width / PixelScale, rect.Height / PixelScale);
    }
    public RectD WorldToScreen(RectD rect)
    {
        Vector2 point = WorldToScreen(new Vector2((float)rect.X, (float)rect.Y));
        return new(point.X, point.Y, rect.Width * PixelScale, rect.Height * PixelScale);
    }
    public static RectD Union(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) throw new ArgumentException("At least one display is required.", nameof(displays));
        double left = displays.Min(d => d.Bounds.X), top = displays.Min(d => d.Bounds.Y);
        return new(left, top, displays.Max(d => d.Bounds.Right) - left, displays.Max(d => d.Bounds.Bottom) - top);
    }
    public static DisplayInfo NearestDisplay(IReadOnlyList<DisplayInfo> displays, Vector2 screenPoint)
    {
        if (displays.Count == 0) throw new ArgumentException("At least one display is required.", nameof(displays));
        static double Distance(RectD rect, Vector2 p)
        {
            double x = Math.Max(Math.Max(rect.X - p.X, 0), p.X - rect.Right);
            double y = Math.Max(Math.Max(rect.Y - p.Y, 0), p.Y - rect.Bottom);
            return x * x + y * y;
        }
        return displays.MinBy(d => Distance(d.Bounds, screenPoint))!;
    }
    public static RectD Intersect(RectD a, RectD b)
    {
        double left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        return new(left, top, Math.Max(0, Math.Min(a.Right, b.Right) - left), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - top));
    }
}
