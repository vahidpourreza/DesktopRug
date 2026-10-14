using System.Numerics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DesktopRug.Tests")]

namespace DesktopRug.Core;

internal readonly record struct ClothContact(Vector3 Normal, Vector3 Barycentric, float Penetration, float Time);

/// <summary>
/// Bounded, allocation-free vertex/triangle self-contact. Swept contacts preserve the vertex's
/// original side of a moving face; local thickness contacts also cover triangle edges and vertices.
/// This is not an exact edge/edge collision solver for arbitrary tangled or degenerate meshes.
/// </summary>
internal sealed class ClothContactSolver
{
    private readonly int _columns;
    private readonly int[] _buckets;
    private readonly int[] _next;
    private readonly int[] _entryParticle;
    private readonly int[] _visited;
    private readonly Vector3[] _sweptMin;
    private readonly Vector3[] _sweptMax;
    private int _stamp;
    internal int ContactsLastStep { get; private set; }
    internal int CandidateTestsLastStep { get; private set; }
    internal bool BudgetExhaustedLastStep { get; private set; }

    internal ClothContactSolver(int columns, int count)
    {
        _columns = columns;
        int bucketCount = 1;
        while (bucketCount < count * 8) bucketCount <<= 1;
        _buckets = new int[bucketCount];
        _next = new int[count * 8];
        _entryParticle = new int[count * 8];
        _visited = new int[count];
        _sweptMin = new Vector3[count]; _sweptMax = new Vector3[count];
    }

    internal void Solve(ClothParticle[] particles, float spacing, float thickness)
    {
        ContactsLastStep = CandidateTestsLastStep = 0;
        BudgetExhaustedLastStep = false;
        float motion = 0;
        for (int i = 0; i < particles.Length; i++)
            motion = Math.Max(motion, Vector3.Distance(particles[i].Position, particles[i].RenderPrevious));
        // Larger swept motion broadens the grid without unbounded cell enumeration.
        float cellSize = Math.Max(spacing * 1.5f, motion * 2 + thickness * 2.1f);
        Array.Fill(_buckets, -1);
        int entry = 0;
        for (int i = 0; i < particles.Length; i++)
        {
            _sweptMin[i] = Vector3.Min(particles[i].Position, particles[i].RenderPrevious) - new Vector3(thickness);
            _sweptMax[i] = Vector3.Max(particles[i].Position, particles[i].RenderPrevious) + new Vector3(thickness);
            Vector3 lo = _sweptMin[i] / cellSize, hi = _sweptMax[i] / cellSize;
            // The cell is wider than every expanded swept vertex AABB: at most two cells per
            // axis, so exactly eight preallocated hash entries per vertex is a hard upper bound.
            for (int x = (int)MathF.Floor(lo.X); x <= (int)MathF.Floor(hi.X); x++)
                for (int y = (int)MathF.Floor(lo.Y); y <= (int)MathF.Floor(hi.Y); y++)
                    for (int z = (int)MathF.Floor(lo.Z); z <= (int)MathF.Floor(hi.Z); z++)
                    {
                        int hash = Hash(x, y, z);
                        _entryParticle[entry] = i; _next[entry] = _buckets[hash]; _buckets[hash] = entry++;
                    }
        }

        int rows = particles.Length / _columns;
        int budget = particles.Length * 192;
        for (int row = 0; row < rows - 1; row++)
            for (int col = 0; col < _columns - 1; col++)
            {
                int a = row * _columns + col, b = a + 1, c = a + _columns, d = c + 1;
                SolveTriangle(a, b, c);
                SolveTriangle(b, d, c);
                if (BudgetExhaustedLastStep) return;
            }

        void SolveTriangle(int a, int b, int c)
        {
            Vector3 min = Vector3.Min(particles[a].Position, Vector3.Min(particles[b].Position, particles[c].Position));
            Vector3 max = Vector3.Max(particles[a].Position, Vector3.Max(particles[b].Position, particles[c].Position));
            min = Vector3.Min(min, Vector3.Min(particles[a].RenderPrevious, Vector3.Min(particles[b].RenderPrevious, particles[c].RenderPrevious)));
            max = Vector3.Max(max, Vector3.Max(particles[a].RenderPrevious, Vector3.Max(particles[b].RenderPrevious, particles[c].RenderPrevious)));
            // The hash contains expanded swept vertex bounds. Triangle queries therefore need
            // no global-motion expansion, keeping work local even during a quick corner grab.
            Vector3 scaledMin = min / cellSize, scaledMax = max / cellSize;
            Vector3 lo = new(MathF.Floor(scaledMin.X), MathF.Floor(scaledMin.Y), MathF.Floor(scaledMin.Z));
            Vector3 hi = new(MathF.Floor(scaledMax.X), MathF.Floor(scaledMax.Y), MathF.Floor(scaledMax.Z));
            if ((hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1) > 512)
            {
                // Gross externally injected stretches must not lock up the UI.
                BudgetExhaustedLastStep = true;
                return;
            }
            if (++_stamp == int.MaxValue) { Array.Clear(_visited); _stamp = 1; }
            for (int x = (int)lo.X; x <= (int)hi.X; x++)
                for (int y = (int)lo.Y; y <= (int)hi.Y; y++)
                    for (int z = (int)lo.Z; z <= (int)hi.Z; z++)
                        for (int node = _buckets[Hash(x, y, z)]; node >= 0; node = _next[node])
                        {
                            int p = _entryParticle[node];
                            if (_visited[p] == _stamp) continue;
                            _visited[p] = _stamp;
                            if (Adjacent(p, a) || Adjacent(p, b) || Adjacent(p, c)) continue;
                            Vector3 pMin = _sweptMin[p], pMax = _sweptMax[p];
                            if (pMax.X < min.X || pMin.X > max.X || pMax.Y < min.Y || pMin.Y > max.Y || pMax.Z < min.Z || pMin.Z > max.Z) continue;
                            if (++CandidateTestsLastStep > budget) { BudgetExhaustedLastStep = true; return; }
                            if (!TryContact(particles[p], particles[a], particles[b], particles[c], thickness, out var contact)) continue;
                            Vector3 bary = contact.Barycentric;
                            float inverseMass = 1 + bary.LengthSquared();
                            // CCD correction is bounded by the swept separation. Velocity clipping in
                            // the integrator and the next structural sweep contain large input impulses.
                            Vector3 correction = contact.Normal * (Math.Min(contact.Penetration, Math.Max(thickness * 3, motion * 1.5f)) / inverseMass);
                            particles[p].Position += correction;
                            particles[a].Position -= correction * bary.X;
                            particles[b].Position -= correction * bary.Y;
                            particles[c].Position -= correction * bary.Z;
                            // Remove the collision-normal velocity added by positional repair.
                            particles[p].Previous += correction;
                            particles[a].Previous -= correction * bary.X;
                            particles[b].Previous -= correction * bary.Y;
                            particles[c].Previous -= correction * bary.Z;
                            // Positional projection alone preserves a hidden velocity into the
                            // supporting layer. Remove that relative normal velocity and apply
                            // bounded cloth-on-cloth friction, so resting folds can actually sleep.
                            Vector3 relativeVelocity = particles[p].Position - particles[p].Previous
                                - (particles[a].Position - particles[a].Previous) * bary.X
                                - (particles[b].Position - particles[b].Previous) * bary.Y
                                - (particles[c].Position - particles[c].Previous) * bary.Z;
                            float inward = Vector3.Dot(relativeVelocity, contact.Normal);
                            if (inward < 0)
                            {
                                Vector3 impulse = contact.Normal * -inward;
                                Vector3 tangent = relativeVelocity - contact.Normal * inward;
                                float tangentLength = tangent.Length();
                                if (tangentLength > .00001f)
                                    impulse -= tangent * (Math.Min(tangentLength, -inward * .32f) / tangentLength);
                                impulse /= inverseMass;
                                particles[p].Previous -= impulse;
                                particles[a].Previous += impulse * bary.X;
                                particles[b].Previous += impulse * bary.Y;
                                particles[c].Previous += impulse * bary.Z;
                            }
                            ContactsLastStep++;
                        }
        }
    }

    private bool Adjacent(int a, int b) => Math.Abs(a % _columns - b % _columns) <= 1 && Math.Abs(a / _columns - b / _columns) <= 1;
    private int Hash(int x, int y, int z) => unchecked((x * 73856093 ^ y * 19349663 ^ z * 83492791) & (_buckets.Length - 1));

    internal static bool TryContact(ClothParticle point, ClothParticle a, ClothParticle b, ClothParticle c, float thickness, out ClothContact contact)
    {
        contact = default;
        Vector3 normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        float normalLength = normal.Length();
        if (normalLength < .0001f) return false;
        normal /= normalLength;

        // Signed volume of a vertex relative to a linearly moving triangle is a cubic.
        Vector3 u = point.RenderPrevious - a.RenderPrevious;
        Vector3 v = b.RenderPrevious - a.RenderPrevious;
        Vector3 w = c.RenderPrevious - a.RenderPrevious;
        Vector3 du = point.Position - a.Position - u;
        Vector3 dv = b.Position - a.Position - v;
        Vector3 dw = c.Position - a.Position - w;
        Vector3 cross0 = Vector3.Cross(v, w), cross1 = Vector3.Cross(dv, w) + Vector3.Cross(v, dw), cross2 = Vector3.Cross(dv, dw);
        float f0 = Vector3.Dot(u, cross0), f1 = Vector3.Dot(du, cross0) + Vector3.Dot(u, cross1);
        float f2 = Vector3.Dot(du, cross1) + Vector3.Dot(u, cross2), f3 = Vector3.Dot(du, cross2);
        Span<float> roots = stackalloc float[3];
        int count = CrossingRoots(f0, f1, f2, f3, roots);
        for (int i = 0; i < count; i++)
        {
            float t = roots[i];
            Vector3 at = Vector3.Lerp(a.RenderPrevious, a.Position, t), bt = Vector3.Lerp(b.RenderPrevious, b.Position, t), ct = Vector3.Lerp(c.RenderPrevious, c.Position, t);
            Vector3 pt = Vector3.Lerp(point.RenderPrevious, point.Position, t);
            Vector3 hit = ClosestPoint(pt, at, bt, ct, out Vector3 bary);
            if (Vector3.DistanceSquared(pt, hit) > .0004f) continue;
            float beforeHit = Math.Max(0, t - .001f);
            float previousSide = ((f3 * beforeHit + f2) * beforeHit + f1) * beforeHit + f0;
            float sign = previousSide >= 0 ? 1 : -1;
            Vector3 oriented = normal * sign;
            Vector3 surface = a.Position * bary.X + b.Position * bary.Y + c.Position * bary.Z;
            float separation = Vector3.Dot(point.Position - surface, oriented);
            if (separation >= thickness) continue;
            contact = new(oriented, bary, thickness - separation, t);
            return true;
        }

        Vector3 closest = ClosestPoint(point.Position, a.Position, b.Position, c.Position, out Vector3 weights);
        Vector3 delta = point.Position - closest;
        float distance = delta.Length();
        if (distance >= thickness) return false;
        Vector3 outward = distance > .0001f ? delta / distance : normal * (f0 >= 0 ? 1 : -1);
        contact = new(outward, weights, thickness - distance, 1);
        return true;
    }

    private static int CrossingRoots(float a, float b, float c, float d, Span<float> result)
    {
        Span<float> intervals = stackalloc float[4];
        intervals[0] = 0; int intervalsCount = 1;
        float scale = Math.Max(1, Math.Max(Math.Abs(b), Math.Max(Math.Abs(c), Math.Abs(d))));
        float epsilon = scale * 1e-7f;
        if (Math.Abs(d) < epsilon)
        {
            if (Math.Abs(c) > epsilon)
            {
                float derivativeRoot = -b / (2 * c);
                if (derivativeRoot > 0 && derivativeRoot < 1) intervals[intervalsCount++] = derivativeRoot;
            }
        }
        else
        {
            float discriminant = 4 * c * c - 12 * d * b;
            if (discriminant > 0)
            {
                float squareRoot = MathF.Sqrt(discriminant);
                float r1 = (-2 * c - squareRoot) / (6 * d), r2 = (-2 * c + squareRoot) / (6 * d);
                if (r1 > r2) (r1, r2) = (r2, r1);
                if (r1 > 0 && r1 < 1) intervals[intervalsCount++] = r1;
                if (r2 > 0 && r2 < 1) intervals[intervalsCount++] = r2;
            }
        }
        intervals[intervalsCount++] = 1;
        int count = 0;
        for (int i = 0; i < intervalsCount - 1; i++)
        {
            float lo = intervals[i], hi = intervals[i + 1];
            float left = ((d * lo + c) * lo + b) * lo + a, right = ((d * hi + c) * hi + b) * hi + a;
            // Initial coplanarity is handled as a thickness contact, not as a crossing.
            if (Math.Abs(left) < epsilon || left * right > 0) continue;
            for (int iteration = 0; iteration < 14; iteration++)
            {
                float middle = (lo + hi) * .5f;
                float value = ((d * middle + c) * middle + b) * middle + a;
                if ((value > 0) == (left > 0)) lo = middle; else hi = middle;
            }
            result[count++] = (lo + hi) * .5f;
        }
        return count;
    }

    internal static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { bary = new(1, 0, 0); return a; }
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { bary = new(0, 1, 0); return b; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        { float t = d1 / (d1 - d3); bary = new(1 - t, t, 0); return a + ab * t; }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { bary = new(0, 0, 1); return c; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        { float t = d2 / (d2 - d6); bary = new(1 - t, 0, t); return a + ac * t; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        { float t = (d4 - d3) / (d4 - d3 + d5 - d6); bary = new(0, 1 - t, t); return b + (c - b) * t; }
        float denominator = va + vb + vc;
        if (Math.Abs(denominator) < 1e-10f) { bary = new(1, 0, 0); return a; }
        float v = vb / denominator, w = vc / denominator;
        bary = new(1 - v - w, v, w);
        return a + ab * v + ac * w;
    }
}
