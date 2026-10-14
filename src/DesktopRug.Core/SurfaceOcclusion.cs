using System.Numerics;

namespace DesktopRug.Core;

/// <summary>Projected XY geometry with surface depth in Z; independent of any rendering backend.</summary>
public static class SurfaceOcclusion
{
    /// <summary>
    /// Finds the portion of a projected segment covered by a nearer triangle. Its barycentric
    /// coordinates and relative depth are affine in t, so clipping their four linear inequalities
    /// finds interior occluders even when both segment endpoints remain visible. Returned endpoints
    /// are the closed boundaries of a positive-length covered interval; a single touching point or
    /// an entire segment at the depth threshold does not count as coverage.
    /// </summary>
    public static bool TryClip(Vector3 start, Vector3 end, Vector3 a, Vector3 b, Vector3 c,
        out Vector2 interval, float depthTolerance = .6f)
    {
        interval = default;
        if (!Finite(start) || !Finite(end) || !Finite(a) || !Finite(b) || !Finite(c)
            || !float.IsFinite(depthTolerance) || depthTolerance < 0) return false;
        if (Math.Max(a.Z, Math.Max(b.Z, c.Z)) <= (double)Math.Min(start.Z, end.Z) + depthTolerance) return false;
        if (Math.Max(start.X, end.X) < Math.Min(a.X, Math.Min(b.X, c.X))
            || Math.Min(start.X, end.X) > Math.Max(a.X, Math.Max(b.X, c.X))
            || Math.Max(start.Y, end.Y) < Math.Min(a.Y, Math.Min(b.Y, c.Y))
            || Math.Min(start.Y, end.Y) > Math.Max(a.Y, Math.Max(b.Y, c.Y))) return false;

        // Promote before subtraction/multiplication so even large finite float coordinates do
        // not overflow during cross products. A relative degeneracy check is scale invariant.
        double bx = (double)b.X - a.X, by = (double)b.Y - a.Y;
        double cx = (double)c.X - a.X, cy = (double)c.Y - a.Y;
        double determinant = bx * cy - cx * by;
        double scale = Math.Max(Math.Max(Math.Abs(bx), Math.Abs(by)), Math.Max(Math.Abs(cx), Math.Abs(cy)));
        if (scale == 0 || Math.Abs(determinant) <= scale * scale * 1e-10) return false;
        double sx = (double)start.X - a.X, sy = (double)start.Y - a.Y;
        double ex = (double)end.X - a.X, ey = (double)end.Y - a.Y;
        double u0 = (sx * cy - sy * cx) / determinant, u1 = (ex * cy - ey * cx) / determinant;
        double v0 = (bx * sy - by * sx) / determinant, v1 = (bx * ey - by * ex) / determinant;
        double low = 0, high = 1;
        if (!ClipNonnegative(u0, u1, ref low, ref high)
            || !ClipNonnegative(v0, v1, ref low, ref high)
            || !ClipNonnegative(1 - u0 - v0, 1 - u1 - v1, ref low, ref high)) return false;

        double bz = (double)b.Z - a.Z, cz = (double)c.Z - a.Z;
        double z0 = a.Z + u0 * bz + v0 * cz - start.Z - depthTolerance;
        double z1 = a.Z + u1 * bz + v1 * cz - end.Z - depthTolerance;
        if (!double.IsFinite(z0) || !double.IsFinite(z1) || (z0 <= 0 && z1 <= 0)
            || !ClipNonnegative(z0, z1, ref low, ref high)) return false;
        float from = (float)Math.Clamp(low, 0, 1), to = (float)Math.Clamp(high, 0, 1);
        if (to <= from) return false;
        interval = new(from, to);
        return true;
    }

    /// <summary>
    /// Subtracts one covered range from ordered, nonoverlapping visible t intervals in [0,1].
    /// A split needs one spare element; reserving one more element than the number of occluders
    /// is sufficient. Mutates only the supplied span and returns its new active element count.
    /// </summary>
    public static int Subtract(Span<Vector2> visible, int count, Vector2 covered)
    {
        if ((uint)count > (uint)visible.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (!float.IsFinite(covered.X) || !float.IsFinite(covered.Y) || covered.Y <= covered.X) return count;
        float low = Math.Clamp(covered.X, 0, 1), high = Math.Clamp(covered.Y, 0, 1);
        if (high <= low) return count;
        for (int i = 0; i < count; i++)
        {
            Vector2 current = visible[i];
            if (high <= current.X || low >= current.Y) continue;
            if (low <= current.X && high >= current.Y)
            {
                visible.Slice(i + 1, count - i - 1).CopyTo(visible[i..]);
                count--; i--;
            }
            else if (low <= current.X) visible[i] = new(high, current.Y);
            else if (high >= current.Y) visible[i] = new(current.X, low);
            else
            {
                if (count == visible.Length) throw new ArgumentException("The visible-interval buffer needs one spare element to split this range.", nameof(visible));
                visible.Slice(i + 1, count - i - 1).CopyTo(visible[(i + 2)..]);
                visible[i] = new(current.X, low);
                visible[i + 1] = new(high, current.Y);
                count++;
                // A covered range wholly inside one visible interval cannot overlap a later
                // interval when the caller obeys the ordered/nonoverlapping input contract.
                break;
            }
        }
        return count;
    }

    private static bool ClipNonnegative(double start, double end, ref double low, ref double high)
    {
        if (start >= 0 && end >= 0) return true;
        if (start < 0 && end < 0) return false;
        double crossing = -start / (end - start);
        if (end > start) low = Math.Max(low, crossing);
        else high = Math.Min(high, crossing);
        return high > low;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
