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
        int index = 0;
        for (int r = 0; r < cloth.Rows - 1; r++) for (int c = 0; c < cloth.Columns - 1; c++)
        {
            ushort a = (ushort)(r * cloth.Columns + c), b = (ushort)(a + 1), d = (ushort)(a + cloth.Columns), e = (ushort)(d + 1);
            _triangles[index++] = new() { A = a, B = b, C = d }; _triangles[index++] = new() { A = b, B = e, C = d };
        }
        SetAppearance(settings);
    }

    internal void SetAppearance(RugSettings settings)
    {
        var texture = PatternFactory.Create(settings);
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
        for (int i = 0; i < particles.Length; i++)
        {
            var position = Vector3.Lerp(particles[i].RenderPrevious, particles[i].Position, interpolation);
            var projected = RugGeometry.Project(position); _positions[i] = new(projected.X, projected.Y);
            int c = i % columns, r = i / columns;
            Vector3 dx = particles[r * columns + Math.Min(c + 1, columns - 1)].Position - particles[r * columns + Math.Max(0, c - 1)].Position;
            Vector3 dy = particles[Math.Min(r + 1, rows - 1) * columns + c].Position - particles[Math.Max(0, r - 1) * columns + c].Position;
            Vector3 normal = Vector3.Cross(dx, dy);
            normal = normal.LengthSquared() > .0001f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            float light = Math.Clamp(.67f + .33f * Vector3.Dot(normal, Vector3.Normalize(new Vector3(-.3f, -.4f, 1))), .32f, 1);
            light = 1 - (1 - light) * _settings.Lighting;
            byte shade = (byte)(light * 255); _colors[i] = new(shade, shade, shade);
        }
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
        for (int t = 0; t < _triangles.Length; t++)
        {
            ref var triangle = ref _triangles[t]; triangle.Depth = (particles[triangle.A].Position.Z + particles[triangle.B].Position.Z + particles[triangle.C].Position.Z) / 3;
        }
        Array.Sort(_triangles, DepthComparer.Instance);
        for (int t = 0; t < _triangles.Length; t++)
        { _indices[t * 3] = _triangles[t].A; _indices[t * 3 + 1] = _triangles[t].B; _indices[t * 3 + 2] = _triangles[t].C; }
        using (var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, _positions, _textureCoordinates, _colors, _indices))
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, _texturePaint);
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
    }
}
