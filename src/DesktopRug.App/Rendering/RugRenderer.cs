using System.Numerics;
using System.Diagnostics;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

internal sealed class RugRenderer : IDisposable
{
    private readonly ClothSimulation _cloth;
    private readonly SKPoint[] _positions, _textureCoordinates;
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
        _triangles = new Triangle[(cloth.Columns - 1) * (cloth.Rows - 1) * 2]; _indices = new ushort[_triangles.Length * 3];
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
        for (int r = 0; r < _cloth.Rows; r++) for (int c = 0; c < _cloth.Columns; c++)
            _textureCoordinates[r * _cloth.Columns + c] = new(c * (texture.Width - 1f) / (_cloth.Columns - 1), r * (texture.Height - 1f) / (_cloth.Rows - 1));
    }

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
            _triangles[t] = new() { A = a, B = b, C = c, Depth = (particles[a].Position.Z + particles[b].Position.Z + particles[c].Position.Z) / 3 };
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
        for (int r = 0; r < rows - 1; r++) for (int side = 0; side < 2; side++)
        {
            int i = r * columns + (side == 0 ? 0 : columns - 1), next = i + columns;
            SKPoint a = _positions[i], b = _positions[next], inside = _positions[i + (side == 0 ? 1 : -1)];
            var outward = Vector2.Normalize(new Vector2(a.X - inside.X, a.Y - inside.Y));
            if (!float.IsFinite(outward.X)) continue;
            for (int k = 0; k < 7; k++)
            {
                float f = k / 7f; float x = a.X + (b.X - a.X) * f, y = a.Y + (b.Y - a.Y) * f;
                float length = 6 + (k % 3) * 1.5f; canvas.DrawLine(x, y, x + outward.X * length, y + outward.Y * length, _fringe);
            }
        }
        LastStageTicks[3] = Stopwatch.GetTimestamp() - phaseStart;
    }

    public void Dispose()
    {
        _texturePaint.Dispose(); _shader?.Dispose(); _texture?.Dispose(); _shadow.Dispose();
        _edge.Dispose(); _highlight.Dispose(); _fringe.Dispose(); _outline.Dispose(); _shadowPath.Dispose();
        _material.Dispose();
    }
}
