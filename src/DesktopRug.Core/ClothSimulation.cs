using System.Numerics;

namespace DesktopRug.Core;

public struct ClothParticle
{
    public Vector3 Position;
    public Vector3 Previous;
    public Vector3 RenderPrevious;
}

public readonly record struct ClothConstraint(int A, int B, float Length, float Stiffness);

/// <summary>Deterministic position-based cloth in monitor-local DIPs. No platform or rendering dependencies.</summary>
public sealed class ClothSimulation
{
    public const float TimeStep = 1f / 120;
    private readonly List<ClothConstraint> _constraints = [];
    private readonly float[] _grabWeights;
    private readonly Vector3[] _grabOffsets;
    private readonly int[] _buckets = new int[2048];
    private readonly int[] _next;
    private readonly float[] _floorHeights;
    private double _accumulator;
    private int _quietSteps;
    private int _grabIndex = -1;
    private Vector3 _target;
    private Vector3 _smoothedTarget;
    private Vector2 _size;
    public ClothParticle[] Particles { get; }
    public IReadOnlyList<ClothConstraint> Constraints => _constraints;
    public int Columns { get; }
    public int Rows { get; }
    public bool IsSleeping { get; private set; }
    public bool IsGrabbed => _grabIndex >= 0;
    public float Interpolation => (float)(_accumulator / TimeStep);
    public BumpField Bumps { get; } = new();
    public int SolverIterations { get; set; } = 9;
    public float InteractionStiffness { get; set; } = .85f;
    public Vector2 Viewport { get; set; } = new(1920, 1080);
    public float MeanMotion { get; private set; }

    public ClothSimulation(int columns = 36, int rows = 26)
    {
        if (columns < 4 || rows < 4 || columns > 100 || rows > 100) throw new ArgumentOutOfRangeException(nameof(columns));
        Columns = columns; Rows = rows;
        Particles = new ClothParticle[columns * rows];
        _grabWeights = new float[Particles.Length];
        _grabOffsets = new Vector3[Particles.Length];
        _next = new int[Particles.Length];
        _floorHeights = new float[Particles.Length];
        Reset(new(500, 400), new(640, 450), 0);
    }

    public Vector2 Center
    {
        get
        {
            Vector2 sum = default;
            foreach (ref readonly var particle in Particles.AsSpan()) sum += new Vector2(particle.Position.X, particle.Position.Y);
            return sum / Particles.Length;
        }
    }

    public void Reset(Vector2 center, Vector2 size, float rotation)
    {
        _size = size;
        _constraints.Clear(); _grabIndex = -1; _accumulator = 0;
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Columns; col++)
            {
                int index = row * Columns + col;
                var local = new Vector2((col / (float)(Columns - 1) - .5f) * size.X, (row / (float)(Rows - 1) - .5f) * size.Y);
                Vector2 xy = center + RugGeometry.Rotate(local, rotation);
                Vector3 position = new(xy, Bumps.HeightAt(xy));
                Particles[index] = new() { Position = position, Previous = position, RenderPrevious = position };
                if (col > 0) Add(index, index - 1, size.X / (Columns - 1), 1);
                if (row > 0) Add(index, index - Columns, size.Y / (Rows - 1), 1);
                float diagonal = new Vector2(size.X / (Columns - 1), size.Y / (Rows - 1)).Length();
                if (col > 0 && row > 0) Add(index, index - Columns - 1, diagonal, .65f);
                if (col < Columns - 1 && row > 0) Add(index, index - Columns + 1, diagonal, .65f);
                if (col > 1) Add(index, index - 2, 2 * size.X / (Columns - 1), .12f);
                if (row > 1) Add(index, index - 2 * Columns, 2 * size.Y / (Rows - 1), .12f);
            }
        Wake();
    }

    private void Add(int a, int b, float length, float stiffness) => _constraints.Add(new(a, b, length, stiffness));
    public void Wake() { IsSleeping = false; _quietSteps = 0; }

    public bool Advance(double elapsed)
    {
        if (IsSleeping) return false;
        if (!double.IsFinite(elapsed) || elapsed <= 0) return false;
        _accumulator = Math.Min(_accumulator + elapsed, TimeStep * 8);
        bool changed = false;
        while (_accumulator >= TimeStep)
        {
            Step(); _accumulator -= TimeStep; changed = true;
        }
        return changed;
    }

    public int Pick(Vector2 projectedPoint)
    {
        // Pick only a rendered triangle, including the visually uppermost folded surface.
        float highest = float.NegativeInfinity;
        int picked = -1;
        for (int r = 0; r < Rows - 1; r++)
            for (int c = 0; c < Columns - 1; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                CheckTriangle(a, b, d); CheckTriangle(b, e, d);
            }
        return picked;

        void CheckTriangle(int a, int b, int c)
        {
            var pa = Particles[a].Position; var pb = Particles[b].Position; var pc = Particles[c].Position;
            if (!RugGeometry.InTriangle(projectedPoint, RugGeometry.Project(pa), RugGeometry.Project(pb), RugGeometry.Project(pc))) return;
            float z = (pa.Z + pb.Z + pc.Z) / 3;
            if (z < highest) return;
            highest = z;
            float da = Vector2.DistanceSquared(projectedPoint, RugGeometry.Project(pa));
            float db = Vector2.DistanceSquared(projectedPoint, RugGeometry.Project(pb));
            float dc = Vector2.DistanceSquared(projectedPoint, RugGeometry.Project(pc));
            picked = da < db ? (da < dc ? a : c) : (db < dc ? b : c);
        }
    }

    public void BeginGrab(int index, Vector2 pointer, float height = 16, float radius = 48)
    {
        if ((uint)index >= Particles.Length) throw new ArgumentOutOfRangeException(nameof(index));
        _grabIndex = index;
        Vector3 origin = Particles[index].Position;
        _target = _smoothedTarget = origin;
        for (int i = 0; i < Particles.Length; i++)
        {
            float distance = Vector3.Distance(Particles[i].Position, origin);
            _grabWeights[i] = Math.Clamp(1 - distance / radius, 0, 1);
            _grabOffsets[i] = Particles[i].Position - origin;
        }
        SetGrabTarget(pointer, height); Wake();
    }

    public void SetGrabTarget(Vector2 pointer, float height)
    {
        if (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y) || !float.IsFinite(height)) return;
        pointer = Vector2.Clamp(pointer, new(-80), Viewport + new Vector2(80));
        _target = RugGeometry.Unproject(pointer, Math.Clamp(height, 3, 240));
        Wake();
    }

    public void EndGrab() { _grabIndex = -1; Wake(); }

    public void Translate(Vector2 delta)
    {
        for (int i = 0; i < Particles.Length; i++)
        {
            Vector3 d = new(delta, 0);
            Particles[i].Position += d; Particles[i].Previous += d; Particles[i].RenderPrevious += d;
        }
        Wake();
    }

    public void Step()
    {
        if (IsSleeping) return;
        Vector3 targetDelta = _target - _smoothedTarget;
        float targetLength = targetDelta.Length();
        if (targetLength > 10) targetDelta *= 10 / targetLength;
        _smoothedTarget += targetDelta;
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            p.RenderPrevious = p.Position;
            Vector3 velocity = (p.Position - p.Previous) * .987f;
            float length = velocity.Length();
            if (length > 9) velocity *= 9 / length;
            p.Previous = p.Position;
            p.Position += velocity + new Vector3(0, 0, -780 * TimeStep * TimeStep);
            _floorHeights[i] = Bumps.HeightAt(new(p.Position.X, p.Position.Y));
        }

        for (int iteration = 0; iteration < SolverIterations; iteration++)
        {
            // Alternate sweep order to reduce directional solver bias.
            for (int n = 0; n < _constraints.Count; n++)
            {
                var constraint = _constraints[iteration % 2 == 0 ? n : _constraints.Count - 1 - n];
                ref var a = ref Particles[constraint.A]; ref var b = ref Particles[constraint.B];
                Vector3 difference = b.Position - a.Position;
                float distance = difference.Length();
                if (distance < .0001f) continue;
                Vector3 correction = difference * ((distance - constraint.Length) / distance * constraint.Stiffness * .5f);
                a.Position += correction; b.Position -= correction;
            }
            if (_grabIndex >= 0)
                for (int i = 0; i < Particles.Length; i++)
                    if (_grabWeights[i] > 0)
                        Particles[i].Position = Vector3.Lerp(Particles[i].Position,
                            _smoothedTarget + _grabOffsets[i], _grabWeights[i] * InteractionStiffness * .32f);
            CollideFloor(false);
        }
        if (_grabIndex >= 0 || MeanMotion > .08f) SolveSelfContact();
        CollideFloor(true);

        float motion = 0;
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            motion += Vector3.DistanceSquared(p.Position, p.Previous);
        }
        MeanMotion = MathF.Sqrt(motion / Particles.Length);
        if (_grabIndex < 0 && MeanMotion < .018f) _quietSteps++; else _quietSteps = 0;
        if (_quietSteps > 90)
        {
            IsSleeping = true;
            for (int i = 0; i < Particles.Length; i++) Particles[i].Previous = Particles[i].RenderPrevious = Particles[i].Position;
        }
    }

    private void CollideFloor(bool friction)
    {
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            float floor = _floorHeights[i];
            if (p.Position.Z <= floor + .05f)
            {
                p.Position.Z = floor;
                if (friction)
                {
                    p.Previous.X = p.Position.X + (p.Previous.X - p.Position.X) * .72f;
                    p.Previous.Y = p.Position.Y + (p.Previous.Y - p.Position.Y) * .72f;
                    p.Previous.Z = floor;
                }
            }
            p.Position.X = Math.Clamp(p.Position.X, -_size.X * .15f, Viewport.X + _size.X * .15f);
            p.Position.Y = Math.Clamp(p.Position.Y, -_size.Y * .15f, Viewport.Y + _size.Y * .15f);
            p.Position.Z = Math.Clamp(p.Position.Z, floor, 400);
        }
    }

    private static int Hash(int x, int y, int z) => unchecked((x * 73856093 ^ y * 19349663 ^ z * 83492791) & 2047);
    private void SolveSelfContact()
    {
        const float cell = 6, separation = 3;
        Array.Fill(_buckets, -1);
        for (int i = 0; i < Particles.Length; i++)
        {
            Vector3 p = Particles[i].Position;
            int x = (int)MathF.Floor(p.X / cell), y = (int)MathF.Floor(p.Y / cell), z = (int)MathF.Floor(p.Z / cell);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                for (int j = _buckets[Hash(x + dx, y + dy, z + dz)]; j >= 0; j = _next[j])
                {
                    if (Math.Abs(i % Columns - j % Columns) <= 2 && Math.Abs(i / Columns - j / Columns) <= 2) continue;
                    Vector3 difference = Particles[i].Position - Particles[j].Position;
                    float length = difference.Length();
                    if (length >= separation || length < .0001f) continue;
                    Vector3 correction = difference * ((separation - length) / length * .5f);
                    Particles[i].Position += correction; Particles[j].Position -= correction;
                }
            int hash = Hash(x, y, z); _next[i] = _buckets[hash]; _buckets[hash] = i;
        }
    }
}
