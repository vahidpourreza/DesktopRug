using System.Numerics;

namespace DesktopRug.Core;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vector2 Center => new((float)(X + Width / 2), (float)(Y + Height / 2));
    public bool Contains(Vector2 point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
}

public sealed record DisplayInfo(string DeviceName, RectD Bounds, RectD WorkArea, double DpiScale, bool IsPrimary)
{
    public Vector2 SizeDip => new((float)(Bounds.Width / DpiScale), (float)(Bounds.Height / DpiScale));
    public Vector2 ScreenToLocal(Vector2 point) => (point - new Vector2((float)Bounds.X, (float)Bounds.Y)) / (float)DpiScale;
    public Vector2 LocalToScreen(Vector2 point) => point * (float)DpiScale + new Vector2((float)Bounds.X, (float)Bounds.Y);
    public RectD ScreenToLocal(RectD rect)
    {
        var origin = ScreenToLocal(new Vector2((float)rect.X, (float)rect.Y));
        return new(origin.X, origin.Y, rect.Width / DpiScale, rect.Height / DpiScale);
    }
}

public static class RugGeometry
{
    public static Vector2 Project(Vector3 position) => new(position.X + position.Z * .16f, position.Y - position.Z * .55f);
    public static Vector3 Unproject(Vector2 point, float height) => new(point.X - height * .16f, point.Y + height * .55f, height);
    public static Vector2 Rotate(Vector2 point, float degrees)
    {
        float angle = degrees * MathF.PI / 180, c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new(point.X * c - point.Y * s, point.X * s + point.Y * c);
    }
    public static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
        float area = Cross(b - a, c - a);
        if (MathF.Abs(area) < .001f) return false;
        float u = Cross(b - a, p - a) / area, v = Cross(p - a, c - a) / area;
        return u >= -.001f && v >= -.001f && u + v <= 1.001f;
    }
}
