using System.Numerics;
using System.Diagnostics;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

internal sealed class RugRenderer : IDisposable
{
    private readonly ClothSimulation _cloth;
    private readonly SKPoint[] _positions, _textureCoordinates;
    private readonly float[] _renderDepths;
    private readonly Vector2[] _fringeVisibleIntervals;
    private readonly int[] _fringeOccluderIndices;
    private readonly SKColor[] _colors;
    private readonly ushort[] _indices;
    private readonly Triangle[] _triangles;
    private readonly AdaptiveRugMesh _mesh;
    private readonly RugMaterialRasterizer _material;
    private readonly SKPathBuilder _outline = new(), _shadowPath = new();
    private readonly SKPaint _texturePaint = new() { IsAntialias = true };
    private readonly SKPaint _shadow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _edge = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.6f, Color = new(47, 31, 27) };
    private readonly SKPaint _highlight = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .9f, Color = new(242, 221, 180, 140) };
    private readonly SKPaint _fringe = new() { IsAntialias = true, StrokeWidth = 1.2f, Color = new(207, 188, 148, 240) };
    private SKBitmap? _texture;
    private SKShader? _shader;
    private RugSettings _settings;
    internal long[] LastStageTicks { get; } = new long[4];
    internal int LastTriangleCount { get; private set; }
    internal int LastOccludedFringeStrands { get; private set; }
    internal SKRect LastBounds { get; private set; }
    internal bool AdaptiveMeshEnabled { get; set; } = true;
    internal bool ReferenceMaterialEnabled { get; set; }
    internal bool VectorAccelerationEnabled { get => _material.VectorAccelerationEnabled; set => _material.VectorAccelerationEnabled = value; }
    internal long[] MaterialTicks => [_material.RasterTicks, _material.CompositeTicks];
    private struct Triangle { public ushort A, B, C; public float Depth; }
    private sealed class DepthComparer : IComparer<Triangle>
    {
        internal static readonly DepthComparer Instance = new();
        public int Compare(Triangle x, Triangle y) => x.Depth.CompareTo(y.Depth);
    }

    internal RugRenderer(ClothSimulation cloth, RugSettings settings)
    {
        _cloth = cloth; _settings = settings;
        _positions = new SKPoint[cloth.Particles.Length]; _textureCoordinates = new SKPoint[_positions.Length]; _colors = new SKColor[_positions.Length];
        _renderDepths = new float[_positions.Length];
        _triangles = new Triangle[(cloth.Columns - 1) * (cloth.Rows - 1) * 2]; _indices = new ushort[_triangles.Length * 3];
        _fringeVisibleIntervals = new Vector2[_triangles.Length + 1];
        _fringeOccluderIndices = new int[_triangles.Length];
        _mesh = new(cloth.Columns, cloth.Rows);
        _material = new(_positions.Length);
        SetAppearance(settings);
    }

    internal void SetAppearance(RugSettings settings)
    {
        using var source = PatternFactory.Create(settings);
        var texture = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var copy = new SKCanvas(texture)) copy.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        texture.SetImmutable();
        var shader = texture.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        _texturePaint.Shader = shader;
        _shader?.Dispose(); _texture?.Dispose(); _texture = texture; _shader = shader; _settings = settings;
        _edge.StrokeWidth = 1.8f;
        _edge.Color = settings.Pattern switch
        {
            RugPattern.Kashan => new(94, 35, 32),
            RugPattern.Tabriz => new(161, 143, 114),
            RugPattern.Modern => new(172, 164, 146),
            _ => new(47, 31, 27)
        };
        _highlight.Color = new(242, 231, 205, 75);
        for (int r = 0; r < _cloth.Rows; r++) for (int c = 0; c < _cloth.Columns; c++)
            _textureCoordinates[r * _cloth.Columns + c] = new(c * (texture.Width - 1f) / (_cloth.Columns - 1), r * (texture.Height - 1f) / (_cloth.Rows - 1));
    }

    // Shadow/light changes do not need to decode a texture or disturb a folded mesh.
    internal void UpdateLighting(RugSettings settings) => _settings = _settings with { Shadow = settings.Shadow, Lighting = settings.Lighting };

    internal void Draw(SKCanvas canvas, float interpolation = 1)
    {
        long phaseStart = Stopwatch.GetTimestamp();
        int columns = _cloth.Columns, rows = _cloth.Rows;
        var particles = _cloth.Particles;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        Vector3 lightDirection = Vector3.Normalize(new Vector3(-.3f, -.4f, 1));
        for (int i = 0; i < particles.Length; i++)
        {
            var position = Vector3.Lerp(particles[i].RenderPrevious, particles[i].Position, interpolation);
            _renderDepths[i] = position.Z;
            var projected = RugGeometry.Project(position); _positions[i] = new(projected.X, projected.Y);
            var shadowPosition = particles[i].Position;
            minX = Math.Min(minX, Math.Min(projected.X - 14, shadowPosition.X + shadowPosition.Z * .38f - 10));
            minY = Math.Min(minY, Math.Min(projected.Y - 14, shadowPosition.Y + shadowPosition.Z * .18f - 7));
            maxX = Math.Max(maxX, Math.Max(projected.X + 14, shadowPosition.X + shadowPosition.Z * .38f + 18));
            maxY = Math.Max(maxY, Math.Max(projected.Y + 14, shadowPosition.Y + shadowPosition.Z * .18f + 21));
            int c = i % columns, r = i / columns;
            Vector3 dx = particles[r * columns + Math.Min(c + 1, columns - 1)].Position - particles[r * columns + Math.Max(0, c - 1)].Position;
            Vector3 dy = particles[Math.Min(r + 1, rows - 1) * columns + c].Position - particles[Math.Max(0, r - 1) * columns + c].Position;
            Vector3 normal = Vector3.Cross(dx, dy);
            normal = normal.LengthSquared() > .0001f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            float light = Math.Clamp(.67f + .33f * Vector3.Dot(normal, lightDirection), .32f, 1);
            light = 1 - (1 - light) * _settings.Lighting;
            byte shade = (byte)(light * 255); _colors[i] = new(shade, shade, shade);
        }
        LastBounds = new(minX, minY, maxX, maxY);
        LastStageTicks[0] = Stopwatch.GetTimestamp() - phaseStart; phaseStart = Stopwatch.GetTimestamp();
        _outline.Reset(); _shadowPath.Reset();
        bool first = true;
        void EdgePoint(int index)
        {
            SKPoint p = _positions[index]; var source = particles[index].Position;
            var shadow = new SKPoint(source.X + source.Z * .38f + 4, source.Y + source.Z * .18f + 7);
            if (first) { _outline.MoveTo(p); _shadowPath.MoveTo(shadow); first = false; }
            else { _outline.LineTo(p); _shadowPath.LineTo(shadow); }
        }
        for (int c = 0; c < columns; c++) EdgePoint(c);
        for (int r = 1; r < rows; r++) EdgePoint(r * columns + columns - 1);
        for (int c = columns - 2; c >= 0; c--) EdgePoint((rows - 1) * columns + c);
        for (int r = rows - 2; r > 0; r--) EdgePoint(r * columns);
        _outline.Close(); _shadowPath.Close();
        using var outline = _outline.Snapshot(); using var shadowPath = _shadowPath.Snapshot();
        // Layered soft strokes avoid a full-window blur intermediate on the CPU renderer.
        for (int layer = 5; layer >= 1; layer--)
        {
            _shadow.StrokeWidth = layer * 5;
            _shadow.Color = new(0, 0, 0, (byte)(_settings.Shadow * (19 - layer * 2)));
            canvas.DrawPath(shadowPath, _shadow);
        }
        canvas.DrawPath(outline, _edge);
        LastStageTicks[1] = Stopwatch.GetTimestamp() - phaseStart; phaseStart = Stopwatch.GetTimestamp();
        int indexCount;
        if (AdaptiveMeshEnabled) indexCount = _mesh.Build(_positions, _colors, particles, _indices);
        else
        {
            indexCount = 0;
            for (int r = 0; r < rows - 1; r++) for (int c = 0; c < columns - 1; c++)
            {
                ushort a = (ushort)(r * columns + c), b = (ushort)(a + 1), d = (ushort)(a + columns), e = (ushort)(d + 1);
                _indices[indexCount++] = a; _indices[indexCount++] = b; _indices[indexCount++] = d;
                _indices[indexCount++] = b; _indices[indexCount++] = e; _indices[indexCount++] = d;
            }
        }
        int triangleCount = indexCount / 3;
        for (int t = 0; t < triangleCount; t++)
        {
            ushort a = _indices[t * 3], b = _indices[t * 3 + 1], c = _indices[t * 3 + 2];
            _triangles[t] = new() { A = a, B = b, C = c, Depth = (_renderDepths[a] + _renderDepths[b] + _renderDepths[c]) / 3 };
        }
        Array.Sort(_triangles, 0, triangleCount, DepthComparer.Instance);
        for (int t = 0; t < triangleCount; t++)
        { _indices[t * 3] = _triangles[t].A; _indices[t * 3 + 1] = _triangles[t].B; _indices[t * 3 + 2] = _triangles[t].C; }
        LastTriangleCount = triangleCount;
        if (ReferenceMaterialEnabled)
        {
            Array.Clear(_indices, indexCount, _indices.Length - indexCount);
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, _positions, _textureCoordinates, _colors, _indices);
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, _texturePaint);
        }
        else if (_texture is not null) _material.Draw(canvas, _positions, _textureCoordinates, _colors, _indices, indexCount, _texture);
        LastStageTicks[2] = Stopwatch.GetTimestamp() - phaseStart; phaseStart = Stopwatch.GetTimestamp();
        canvas.DrawPath(outline, _highlight);
        bool topAndBottomFringe = _settings.AspectRatio < 1;
        LastOccludedFringeStrands = 0;
        int edgeSegments = topAndBottomFringe ? columns - 1 : rows - 1;
        SKColor fringeColor = _settings.Pattern == RugPattern.Modern ? new(224, 218, 203) : new(235, 226, 205);
        for (int segment = 0; segment < edgeSegments; segment++) for (int side = 0; side < 2; side++)
        {
            int i = topAndBottomFringe ? (side == 0 ? 0 : (rows - 1) * columns) + segment : segment * columns + (side == 0 ? 0 : columns - 1);
            int next = i + (topAndBottomFringe ? 1 : columns);
            int inward = topAndBottomFringe ? (side == 0 ? columns : -columns) : (side == 0 ? 1 : -1);
            SKPoint a = _positions[i], b = _positions[next], inside = _positions[i + inward];
            var outward = Vector2.Normalize(new Vector2(a.X - inside.X, a.Y - inside.Y));
            if (!float.IsFinite(outward.X)) continue;
            int candidateCount = CollectFringeOccluders(a, b, outward, Math.Min(_renderDepths[i], _renderDepths[next]), triangleCount);
            int strands = Math.Clamp((int)MathF.Ceiling(Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)) / 1.4f), 2, 32);
            for (int k = 0; k < strands; k++)
            {
                float f = k / (float)strands; float x = a.X + (b.X - a.X) * f, y = a.Y + (b.Y - a.Y) * f;
                uint seed = (uint)(segment * 4099 + k * 977 + side * 1279 + 29);
                seed ^= seed >> 16; seed *= 0x7feb352d; seed ^= seed >> 15;
                float variation = (seed & 1023) / 1023f;
                float length = (topAndBottomFringe ? 10 : 5.5f) + variation * 3;
                float depth = _renderDepths[i] + (_renderDepths[next] - _renderDepths[i]) * f;
                var start = new SKPoint(x, y);
                var end = new SKPoint(x + outward.X * length, y + outward.Y * length);
                int visibleCount = VisibleFringeIntervals(start, end, depth, candidateCount);
                if (visibleCount != 1 || _fringeVisibleIntervals[0] != new Vector2(0, 1)) LastOccludedFringeStrands++;
                _fringe.StrokeWidth = .65f + variation * .3f;
                _fringe.Color = fringeColor.WithAlpha((byte)(210 + (seed >> 12) % 40));
                for (int part = 0; part < visibleCount; part++)
                {
                    Vector2 interval = _fringeVisibleIntervals[part];
                    canvas.DrawLine(new(start.X + (end.X - start.X) * interval.X, start.Y + (end.Y - start.Y) * interval.X),
                        new(start.X + (end.X - start.X) * interval.Y, start.Y + (end.Y - start.Y) * interval.Y), _fringe);
                }
            }
        }
        LastStageTicks[3] = Stopwatch.GetTimestamp() - phaseStart;
    }

    private int CollectFringeOccluders(SKPoint a, SKPoint b, Vector2 outward, float minimumDepth, int triangleCount)
    {
        // All strands on this mesh edge stay inside this thin band. Reject remote
        // triangles once per edge, rather than scanning the entire cloth per thread.
        float dx = outward.X * 14, dy = outward.Y * 14;
        float left = Math.Min(a.X, b.X) + Math.Min(0, dx), right = Math.Max(a.X, b.X) + Math.Max(0, dx);
        float top = Math.Min(a.Y, b.Y) + Math.Min(0, dy), bottom = Math.Max(a.Y, b.Y) + Math.Max(0, dy);
        int count = 0;
        for (int t = triangleCount - 1; t >= 0; t--)
        {
            Triangle triangle = _triangles[t];
            if (Math.Max(_renderDepths[triangle.A], Math.Max(_renderDepths[triangle.B], _renderDepths[triangle.C])) <= minimumDepth + .6f) continue;
            SKPoint p = _positions[triangle.A], q = _positions[triangle.B], r = _positions[triangle.C];
            if (right < Math.Min(p.X, Math.Min(q.X, r.X)) || left > Math.Max(p.X, Math.Max(q.X, r.X))
                || bottom < Math.Min(p.Y, Math.Min(q.Y, r.Y)) || top > Math.Max(p.Y, Math.Max(q.Y, r.Y))) continue;
            _fringeOccluderIndices[count++] = t;
        }
        return count;
    }

    private int VisibleFringeIntervals(SKPoint start, SKPoint end, float depth, int candidateCount)
    {
        // Subtract exact covered intervals, not endpoint samples: a strand may cross
        // several foreground folds while both of its endpoints remain visible.
        _fringeVisibleIntervals[0] = new(0, 1);
        int visibleCount = 1;
        float left = Math.Min(start.X, end.X), right = Math.Max(start.X, end.X);
        float top = Math.Min(start.Y, end.Y), bottom = Math.Max(start.Y, end.Y);
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            Triangle triangle = _triangles[_fringeOccluderIndices[candidate]];
            float za = _renderDepths[triangle.A], zb = _renderDepths[triangle.B], zc = _renderDepths[triangle.C];
            if (Math.Max(za, Math.Max(zb, zc)) <= depth + .6f) continue;
            SKPoint a = _positions[triangle.A], b = _positions[triangle.B], c = _positions[triangle.C];
            if (right < Math.Min(a.X, Math.Min(b.X, c.X)) || left > Math.Max(a.X, Math.Max(b.X, c.X))
                || bottom < Math.Min(a.Y, Math.Min(b.Y, c.Y)) || top > Math.Max(a.Y, Math.Max(b.Y, c.Y))) continue;
            if (SurfaceOcclusion.TryClip(new(start.X, start.Y, depth), new(end.X, end.Y, depth),
                new(a.X, a.Y, za), new(b.X, b.Y, zb), new(c.X, c.Y, zc), out var covered))
            {
                visibleCount = SurfaceOcclusion.Subtract(_fringeVisibleIntervals, visibleCount, covered);
                if (visibleCount == 0) break;
            }
        }
        return visibleCount;
    }

    public void Dispose()
    {
        _texturePaint.Dispose(); _shader?.Dispose(); _texture?.Dispose(); _shadow.Dispose();
        _edge.Dispose(); _highlight.Dispose(); _fringe.Dispose(); _outline.Dispose(); _shadowPath.Dispose();
        _material.Dispose();
    }
}
