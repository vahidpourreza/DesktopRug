using System.Numerics;
using System.Runtime.InteropServices;
using DesktopRug.Core;

namespace DesktopRug.App.Windows;

/// <summary>
/// Rasterizes the union of projected cloth triangles into a bounded coverage mask,
/// then sends sorted, non-overlapping horizontal bands to GDI. Unlike polygon-region
/// union, its cost is bounded by visible pixels rather than intersections between
/// hundreds of folded polygons. A successful Create transfers HRGN ownership to its caller.
/// </summary>
internal sealed class RugHitRegion
{
    private const int MaximumDimension = 32768, MaximumPixels = 64 * 1024 * 1024;
    private const int MaximumRectangles = 1_000_000;
    private readonly ClothSimulation _cloth;
    private readonly Vector2[] _positions;
    private ulong[] _coverage = [];
    private int[] _currentRuns = [], _previousRuns = [];
    // RGNDATAHEADER: eight DWORDs, followed by RECT structures of four LONGs each.
    private int[] _data = new int[8 + 4096 * 4];

    internal RugHitRegion(ClothSimulation cloth)
    {
        _cloth = cloth;
        _positions = new Vector2[cloth.Particles.Length];
    }

    internal unsafe nint Create(DisplayInfo display, RugCoordinateSpace space)
    {
        if (!double.IsFinite(display.Bounds.Width) || !double.IsFinite(display.Bounds.Height)
            || display.Bounds.Width <= 0 || display.Bounds.Height <= 0
            || display.Bounds.Width > MaximumDimension || display.Bounds.Height > MaximumDimension)
            return 0;
        int monitorWidth = (int)Math.Ceiling(display.Bounds.Width), monitorHeight = (int)Math.Ceiling(display.Bounds.Height);
        Vector2 origin = new((float)display.Bounds.X, (float)display.Bounds.Y);
        float minX = monitorWidth, minY = monitorHeight, maxX = 0, maxY = 0;
        for (int i = 0; i < _positions.Length; i++)
        {
            Vector2 p = space.WorldToScreen(RugGeometry.Project(_cloth.Particles[i].Position)) - origin;
            // Reject corrupted simulation coordinates before edge differences or integer
            // conversions can overflow. Real virtual-desktop coordinates are far smaller.
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || Math.Abs(p.X) > 10_000_000 || Math.Abs(p.Y) > 10_000_000) return 0;
            _positions[i] = p;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        int left = (int)MathF.Floor(Math.Clamp(minX, 0, monitorWidth));
        int top = (int)MathF.Floor(Math.Clamp(minY, 0, monitorHeight));
        int right = (int)MathF.Ceiling(Math.Clamp(maxX, 0, monitorWidth));
        int bottom = (int)MathF.Ceiling(Math.Clamp(maxY, 0, monitorHeight));
        int width = right - left, height = bottom - top;
        if (width <= 0 || height <= 0) return CreateRectRgn(0, 0, 0, 0);
        if ((long)width * height > MaximumPixels) return 0;
        int stride = (width + 63) / 64, words = checked(stride * height);
        if (_coverage.Length < words) Array.Resize(ref _coverage, Grow(_coverage.Length, words));
        Array.Clear(_coverage, 0, words);
        if (_currentRuns.Length < width + 2)
        {
            int capacity = Grow(_currentRuns.Length, width + 2);
            _currentRuns = new int[capacity]; _previousRuns = new int[capacity];
        }

        int columns = _cloth.Columns;
        for (int row = 0; row < _cloth.Rows - 1; row++)
            for (int col = 0; col < columns - 1; col++)
            {
                int a = row * columns + col, b = a + 1, c = a + columns, d = c + 1;
                Rasterize(a, b, c); Rasterize(b, d, c);
            }

        int used = 8, previousCount = 0, bandStart = 8;
        int boundLeft = right, boundTop = bottom, boundRight = left, boundBottom = top;
        for (int y = 0; y < height; y++)
        {
            int count = Runs(y * stride);
            bool sameBand = count == previousCount && _currentRuns.AsSpan(0, count).SequenceEqual(_previousRuns.AsSpan(0, count));
            if (sameBand)
            {
                for (int index = bandStart + 3; index < used; index += 4) _data[index] = top + y + 1;
            }
            else
            {
                bandStart = used;
                int required = checked(used + count * 2);
                if ((required - 8) / 4 > MaximumRectangles) return 0;
                if (_data.Length < required) Array.Resize(ref _data, Grow(_data.Length, required));
                for (int run = 0; run < count; run += 2)
                {
                    _data[used++] = left + _currentRuns[run]; _data[used++] = top + y;
                    _data[used++] = left + _currentRuns[run + 1]; _data[used++] = top + y + 1;
                }
            }
            if (count > 0)
            {
                boundLeft = Math.Min(boundLeft, left + _currentRuns[0]);
                boundTop = Math.Min(boundTop, top + y);
                boundRight = Math.Max(boundRight, left + _currentRuns[count - 1]);
                boundBottom = top + y + 1;
            }
            (_currentRuns, _previousRuns) = (_previousRuns, _currentRuns); previousCount = count;
        }
        if (used == 8) return CreateRectRgn(0, 0, 0, 0);
        _data[0] = 32; _data[1] = 1; _data[2] = (used - 8) / 4; _data[3] = (used - 8) * 4;
        _data[4] = boundLeft; _data[5] = boundTop; _data[6] = boundRight; _data[7] = boundBottom;
        fixed (int* data = _data) return ExtCreateRegion(0, checked((uint)(used * 4)), data);

        void Rasterize(int ia, int ib, int ic)
        {
            Vector2 a = _positions[ia], b = _positions[ib], c = _positions[ic];
            if (a.Y > b.Y) (a, b) = (b, a);
            if (b.Y > c.Y) (b, c) = (c, b);
            if (a.Y > b.Y) (a, b) = (b, a);
            float span = c.Y - a.Y;
            if (span < .0001f || c.Y < top || a.Y > bottom) return;
            float longSlope = (c.X - a.X) / span;
            int firstY = (int)MathF.Ceiling(Math.Clamp(a.Y - .5f, top, bottom));
            int lastY = (int)MathF.Ceiling(Math.Clamp(c.Y - .5f, top, bottom));
            for (int y = firstY; y < lastY; y++)
            {
                float sample = y + .5f;
                Vector2 from = sample < b.Y ? a : b, to = sample < b.Y ? b : c;
                float dy = to.Y - from.Y;
                if (dy < .0001f) continue;
                float x1 = a.X + (sample - a.Y) * longSlope;
                float x2 = from.X + (sample - from.Y) * ((to.X - from.X) / dy);
                int start = (int)MathF.Ceiling(Math.Clamp(Math.Min(x1, x2) - .5f, left, right)) - left;
                int end = (int)MathF.Ceiling(Math.Clamp(Math.Max(x1, x2) - .5f, left, right)) - left;
                if (end <= start) continue;
                int offset = (y - top) * stride;
                int firstWord = start >> 6, lastWord = (end - 1) >> 6;
                ulong firstMask = ulong.MaxValue << (start & 63);
                ulong lastMask = ulong.MaxValue >> (63 - ((end - 1) & 63));
                if (firstWord == lastWord) _coverage[offset + firstWord] |= firstMask & lastMask;
                else
                {
                    _coverage[offset + firstWord] |= firstMask;
                    for (int word = firstWord + 1; word < lastWord; word++) _coverage[offset + word] = ulong.MaxValue;
                    _coverage[offset + lastWord] |= lastMask;
                }
            }
        }

        int Runs(int rowOffset)
        {
            int count = 0, x = 0;
            while (x < width)
            {
                int word = x >> 6;
                ulong bits = _coverage[rowOffset + word] & (ulong.MaxValue << (x & 63));
                while (bits == 0 && ++word < stride) bits = _coverage[rowOffset + word];
                if (word >= stride) break;
                x = (word << 6) + BitOperations.TrailingZeroCount(bits);
                if (x >= width) break;
                _currentRuns[count++] = x;
                word = x >> 6;
                bits = ~_coverage[rowOffset + word] & (ulong.MaxValue << (x & 63));
                while (bits == 0 && ++word < stride) bits = ~_coverage[rowOffset + word];
                x = word >= stride ? width : Math.Min(width, (word << 6) + BitOperations.TrailingZeroCount(bits));
                _currentRuns[count++] = x;
            }
            return count;
        }
    }

    private static int Grow(int current, int minimum) => Math.Max(minimum, current == 0 ? 1024 : checked(current * 2));

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern unsafe nint ExtCreateRegion(nint transform, uint bytes, int* regionData);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
}
