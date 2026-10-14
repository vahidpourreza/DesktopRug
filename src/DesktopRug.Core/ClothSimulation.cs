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
    private readonly ClothContactSolver _contacts;
    private readonly float[] _floorHeights;
    private readonly Vector3[] _rollOffsets;
    private double _accumulator;
    private int _quietSteps;
    private int _grabIndex = -1;
    private Vector3 _target;
    private Vector3 _smoothedTarget;
    private Vector2 _size;
    private Vector2 _rollCenter;
    private Vector2 _rollBoundaryOffset;
    private float _rotation;
    private float _rollAmount, _rollFrom, _rollTo, _rollElapsed;
    private bool _rollGuided;
    private float _unitScale = 1;
    public ClothParticle[] Particles { get; }
    public IReadOnlyList<ClothConstraint> Constraints => _constraints;
    public int Columns { get; }
    public int Rows { get; }
    public bool IsSleeping { get; private set; }
    public bool IsGrabbed => _grabIndex >= 0;
    public bool IsRolled => _rollGuided && _rollTo == 1;
    public bool IsRolling => _rollGuided && _rollAmount != _rollTo;
    public float RollAmount => _rollAmount;
    public Vector2 MaterialSize => _size;
    public int ContactsLastStep => _contacts.ContactsLastStep;
    public bool ContactBudgetExhausted => _contacts.BudgetExhaustedLastStep;
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
        _contacts = new(columns, Particles.Length);
        _floorHeights = new float[Particles.Length];
        _rollOffsets = new Vector3[Particles.Length];
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
        if (!float.IsFinite(center.X + center.Y + size.X + size.Y + rotation) || size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        _size = size; _rotation = rotation; _unitScale = 1;
        _rollGuided = false; _rollAmount = _rollTo = 0; _rollCenter = center; _rollBoundaryOffset = default;
        MeanMotion = 0;
        _constraints.Clear(); _grabIndex = -1; _accumulator = 0;
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Columns; col++)
            {
                int index = row * Columns + col;
                var local = new Vector2((col / (float)(Columns - 1) - .5f) * size.X, (row / (float)(Rows - 1) - .5f) * size.Y);
                Vector2 xy = center + RugGeometry.Rotate(local, rotation);
                Vector3 position = new(xy, FloorAt(xy));
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
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        // A grabbed roll becomes ordinary cloth in its current curled shape, without a teleport.
        _rollGuided = false; _rollAmount = _rollTo = 0;
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
        pointer = Vector2.Clamp(pointer, new(-80 * _unitScale), Viewport + new Vector2(80 * _unitScale));
        _target = RugGeometry.Unproject(pointer, Math.Clamp(height, 3 * _unitScale, 240 * _unitScale));
        Wake();
    }

    public void EndGrab() { _grabIndex = -1; Wake(); }

    public void Translate(Vector2 delta)
    {
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
        Vector3 d = new(delta, 0);
        for (int i = 0; i < Particles.Length; i++)
        {
            Particles[i].Position += d; Particles[i].Previous += d; Particles[i].RenderPrevious += d;
        }
        _target += d; _smoothedTarget += d; _rollCenter += delta;
        Wake();
    }

    /// <summary>
    /// Changes the local coordinate frame while preserving all physical-screen positions and
    /// velocities. DPI arguments are physical pixels per DIP (1, 1.5, etc.). The caller updates
    /// Viewport and the icon bump field for the new frame before the next simulation step.
    /// </summary>
    public void Rebase(Vector2 oldOriginScreen, float oldDpi, Vector2 newOriginScreen, float newDpi)
    {
        if (!float.IsFinite(oldDpi) || !float.IsFinite(newDpi) || oldDpi <= 0 || newDpi <= 0 ||
            !float.IsFinite(oldOriginScreen.X + oldOriginScreen.Y + newOriginScreen.X + newOriginScreen.Y))
            throw new ArgumentOutOfRangeException(nameof(newDpi));
        float scale = oldDpi / newDpi;
        Vector3 offset = new((oldOriginScreen - newOriginScreen) / newDpi, 0);
        for (int i = 0; i < Particles.Length; i++)
        {
            Particles[i].Position = Particles[i].Position * scale + offset;
            Particles[i].Previous = Particles[i].Previous * scale + offset;
            Particles[i].RenderPrevious = Particles[i].RenderPrevious * scale + offset;
            _grabOffsets[i] *= scale; _rollOffsets[i] *= scale; _floorHeights[i] *= scale;
        }
        for (int i = 0; i < _constraints.Count; i++) _constraints[i] = _constraints[i] with { Length = _constraints[i].Length * scale };
        _target = _target * scale + offset; _smoothedTarget = _smoothedTarget * scale + offset;
        _rollCenter = _rollCenter * scale + new Vector2(offset.X, offset.Y);
        _rollBoundaryOffset *= scale;
        _size *= scale; _unitScale *= scale; MeanMotion *= scale;
        Wake();
    }

    /// <summary>Animates a guided spiral roll with the material's original arc length, or unrolls it.</summary>
    public void SetRolled(bool rolled)
    {
        float target = rolled ? 1 : 0;
        if ((_rollGuided && _rollTo == target) || (!_rollGuided && !rolled)) return;
        _grabIndex = -1;
        if (!_rollGuided)
        {
            _rollCenter = Center; _rollAmount = 0; _rollBoundaryOffset = default;
            _rollBoundaryOffset = CompactBoundaryCorrection();
        }
        _rollFrom = _rollAmount; _rollTo = target; _rollElapsed = 0;
        for (int i = 0; i < Particles.Length; i++)
            _rollOffsets[i] = Particles[i].Position - RollPosition(i, _rollAmount);
        _rollGuided = true;
        Wake();
    }

    public void Step()
    {
        if (IsSleeping) return;
        if (_rollGuided) { StepRoll(); return; }
        Vector3 targetDelta = _target - _smoothedTarget;
        float targetLength = targetDelta.Length();
        if (targetLength > 10 * _unitScale) targetDelta *= 10 * _unitScale / targetLength;
        _smoothedTarget += targetDelta;
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            p.RenderPrevious = p.Position;
            Vector3 velocity = (p.Position - p.Previous) * .987f;
            float length = velocity.Length();
            if (length > 9 * _unitScale) velocity *= 9 * _unitScale / length;
            p.Previous = p.Position;
            p.Position += velocity + new Vector3(0, 0, -780 * _unitScale * TimeStep * TimeStep);
            _floorHeights[i] = FloorAt(new(p.Position.X, p.Position.Y));
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
        // Sweep against RenderPrevious even on the first moving step; MeanMotion describes the
        // previous step and cannot safely decide whether a new collision should be skipped.
        _contacts.Solve(Particles, Math.Max(_size.X / (Columns - 1), _size.Y / (Rows - 1)), 3 * _unitScale);
        CollideFloor(true);

        float motion = 0, maximumMotion = 0;
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            // Contact impulses modify Previous to cancel hidden inward velocity. Sleep must
            // measure the surface that actually moved, not that solver bookkeeping state.
            float displacement = Vector3.DistanceSquared(p.Position, p.RenderPrevious);
            motion += displacement; maximumMotion = Math.Max(maximumMotion, displacement);
        }
        MeanMotion = MathF.Sqrt(motion / Particles.Length);
        // RMS alone can hide a still-moving corner in a large mesh. Both bounds must remain
        // quiet for three quarters of a second; a moving or falling tip keeps the cloth awake.
        if (_grabIndex < 0 && MeanMotion < .018f * _unitScale && maximumMotion < .08f * .08f * _unitScale * _unitScale)
            _quietSteps++;
        else _quietSteps = 0;
        if (_quietSteps > 90)
        {
            IsSleeping = true; MeanMotion = 0;
            for (int i = 0; i < Particles.Length; i++) Particles[i].Previous = Particles[i].RenderPrevious = Particles[i].Position;
        }
    }

    private void CollideFloor(bool friction)
    {
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            // Constraints can move a particle across an icon within this step. Re-sample at the
            // final position so contact never uses the floor from an unrelated earlier location.
            float floor = friction ? FloorAt(new(p.Position.X, p.Position.Y)) : _floorHeights[i];
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
            p.Position.Z = Math.Clamp(p.Position.Z, floor, 400 * _unitScale);
        }
    }

    private void StepRoll()
    {
        _rollElapsed = Math.Min(_rollElapsed + TimeStep, .7f);
        float t = _rollElapsed / .7f;
        float eased = t * t * (3 - 2 * t);
        _rollAmount = _rollFrom + (_rollTo - _rollFrom) * eased;
        if (t >= 1) _rollAmount = _rollTo;
        float motion = 0;
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i];
            p.RenderPrevious = p.Position;
            p.Position = RollPosition(i, _rollAmount) + _rollOffsets[i] * (1 - eased);
            p.Position.Z = Math.Max(p.Position.Z, FloorAt(new(p.Position.X, p.Position.Y)));
            motion += Vector3.DistanceSquared(p.Position, p.Previous);
            p.Previous = p.Position;
        }
        MeanMotion = MathF.Sqrt(motion / Particles.Length);
        if (t < 1) return;
        if (_rollTo == 1)
        {
            IsSleeping = true; MeanMotion = 0;
            for (int i = 0; i < Particles.Length; i++) Particles[i].RenderPrevious = Particles[i].Position;
        }
        else { _rollGuided = false; Wake(); }
    }

    private Vector3 RollPosition(int index, float amount)
    {
        float x = (index % Columns / (float)(Columns - 1) - .5f) * _size.X;
        float material = index / Columns / (float)(Rows - 1) * _size.Y;
        float flat = _size.Y * (1 - amount);
        float y = material - _size.Y * .5f, z = 0;
        if (material > flat)
        {
            // An inward Archimedean spiral leaves one fabric thickness between turns. Integrating
            // its arc length retains the full rug instead of changing its rest dimensions.
            float pitch = 3.4f * _unitScale / (2 * MathF.PI);
            float innerRadius = Math.Max(19 * _unitScale, _size.Y / (Rows - 1) * 1.3f);
            float outerRadius = MathF.Sqrt(innerRadius * innerRadius + 2 * pitch * (_size.Y - flat));
            float arc = material - flat;
            float angle = arc / outerRadius;
            for (int iteration = 0; iteration < 5; iteration++)
            {
                float r = Math.Max(innerRadius * .5f, outerRadius - pitch * angle);
                float length = (SpiralIntegral(outerRadius, pitch) - SpiralIntegral(r, pitch)) / pitch;
                angle += (arc - length) / MathF.Sqrt(r * r + pitch * pitch);
            }
            float radius = outerRadius - pitch * angle;
            y = flat - _size.Y * .5f + radius * MathF.Sin(angle);
            z = outerRadius - radius * MathF.Cos(angle);
        }
        Vector2 xy = _rollCenter + RugGeometry.Rotate(new(x, y), _rotation) + _rollBoundaryOffset * amount;
        return new(xy, z + FloorAt(xy));
    }

    private Vector2 CompactBoundaryCorrection()
    {
        float left = float.PositiveInfinity, top = float.PositiveInfinity;
        float right = float.NegativeInfinity, bottom = float.NegativeInfinity;
        for (int row = 0; row < Rows; row++)
            for (int edge = 0; edge < 2; edge++)
            {
                // Each row runs straight between its two ends. The margin also covers the
                // bounded icon height changes after the compact shape moves across the floor.
                Vector2 point = RugGeometry.Project(RollPosition(row * Columns + edge * (Columns - 1), 1));
                left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
            }
        float margin = Math.Min(12 * _unitScale, Math.Min(Viewport.X, Viewport.Y) * .1f);
        static float Correction(float low, float high, float extent, float margin)
        {
            if (high - low > extent - 2 * margin) return extent * .5f - (low + high) * .5f;
            if (low < margin) return margin - low;
            return high > extent - margin ? extent - margin - high : 0;
        }
        return new(Correction(left, right, Viewport.X, margin), Correction(top, bottom, Viewport.Y, margin));
    }

    private float FloorAt(Vector2 point) => Bumps.HeightAt(point) * _unitScale;

    private static float SpiralIntegral(float radius, float pitch)
    {
        float hypotenuse = MathF.Sqrt(radius * radius + pitch * pitch);
        return .5f * (radius * hypotenuse + pitch * pitch * MathF.Log(radius + hypotenuse));
    }
}
