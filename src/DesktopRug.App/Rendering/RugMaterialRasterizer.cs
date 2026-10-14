using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

/// <summary>
/// Bounded, full-resolution rasterization of opaque woven triangles. Skia's generic
/// software vertices pipeline is expensive for a texture multiplied by vertex colors;
/// this specialized pass interpolates the same UV and scalar lighting without shaders.
/// The transparent bitmap is composited by Skia with the existing canvas clip intact.
/// </summary>
internal sealed class RugMaterialRasterizer : IDisposable
{
    internal long RasterTicks { get; private set; }
    internal long CompositeTicks { get; private set; }
    internal bool VectorAccelerationEnabled { get; set; } = true;
    private readonly SKPoint[] _devicePositions;
    private SKBitmap? _bitmap;
    private static readonly Vector256<float> PixelOffsets = Vector256.Create(0f, 1, 2, 3, 4, 5, 6, 7);
    internal RugMaterialRasterizer(int vertexCount) => _devicePositions = new SKPoint[vertexCount];

    internal unsafe void Draw(SKCanvas canvas, SKPoint[] positions, SKPoint[] coordinates, SKColor[] colors,
        ushort[] indices, int indexCount, SKBitmap texture)
    {
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        canvas.TotalMatrix.MapPoints(_devicePositions, positions);
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach (var point in _devicePositions)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        var clip = canvas.DeviceClipBounds;
        int left = Math.Max(clip.Left, (int)MathF.Floor(minX)), top = Math.Max(clip.Top, (int)MathF.Floor(minY));
        int right = Math.Min(clip.Right, (int)MathF.Ceiling(maxX)), bottom = Math.Min(clip.Bottom, (int)MathF.Ceiling(maxY));
        int width = right - left, height = bottom - top;
        if (width <= 0 || height <= 0) return;
        if (_bitmap is null || _bitmap.Width < width || _bitmap.Height < height)
        {
            int allocationWidth = Math.Max(_bitmap?.Width ?? 0, (width + 63) / 64 * 64);
            int allocationHeight = Math.Max(_bitmap?.Height ?? 0, (height + 63) / 64 * 64);
            _bitmap?.Dispose();
            _bitmap = new(allocationWidth, allocationHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        }
        Span<uint> target = MemoryMarshal.Cast<byte, uint>(_bitmap.GetPixelSpan());
        ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(texture.GetPixelSpan());
        target.Clear();
        int textureWidth = texture.Width, textureHeight = texture.Height;
        int targetStride = _bitmap.RowBytes / 4;
        var maximumX = Vector256.Create(textureWidth - 1); var maximumY = Vector256.Create(textureHeight - 1);
        var stride = Vector256.Create(textureWidth); var white = Vector256.Create(256);
        var redBlueMask = Vector256.Create(0x00ff00ffu); var greenMask = Vector256.Create(0x0000ff00u);
        var alphaMask = Vector256.Create(0xff000000u);
        fixed (uint* sourceAddress = source)
            for (int t = 0; t < indexCount; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                SKPoint pa = _devicePositions[a], pb = _devicePositions[b], pc = _devicePositions[c];
                float determinant = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
                if (MathF.Abs(determinant) < .0001f) continue;
                float inverse = 1 / determinant;
                float ax = pa.X, ay = pa.Y;
                SKPoint ta = coordinates[a], tb = coordinates[b], tc = coordinates[c];
                float ua = ta.X, va = ta.Y, sa = colors[a].Red;
                float ux = ((tb.X - ua) * (pc.Y - ay) - (tc.X - ua) * (pb.Y - ay)) * inverse;
                float uy = ((tc.X - ua) * (pb.X - ax) - (tb.X - ua) * (pc.X - ax)) * inverse;
                float vx = ((tb.Y - va) * (pc.Y - ay) - (tc.Y - va) * (pb.Y - ay)) * inverse;
                float vy = ((tc.Y - va) * (pb.X - ax) - (tb.Y - va) * (pc.X - ax)) * inverse;
                float sx = ((colors[b].Red - sa) * (pc.Y - ay) - (colors[c].Red - sa) * (pb.Y - ay)) * inverse;
                float sy = ((colors[c].Red - sa) * (pb.X - ax) - (colors[b].Red - sa) * (pc.X - ax)) * inverse;
                var uOffsets = PixelOffsets * Vector256.Create(ux);
                var vOffsets = PixelOffsets * Vector256.Create(vx);
                var shadeOffsets = PixelOffsets * Vector256.Create(sx);
                if (pa.Y > pb.Y) (pa, pb) = (pb, pa);
                if (pb.Y > pc.Y) (pb, pc) = (pc, pb);
                if (pa.Y > pb.Y) (pa, pb) = (pb, pa);
                if (pc.Y - pa.Y < .0001f) continue;
                float longSlope = (pc.X - pa.X) / (pc.Y - pa.Y);
                int firstY = Math.Max(top, (int)MathF.Ceiling(pa.Y - .5f));
                int lastY = Math.Min(bottom, (int)MathF.Ceiling(pc.Y - .5f));
                for (int y = firstY; y < lastY; y++)
                {
                    float sampleY = y + .5f;
                    SKPoint shortStart = sampleY < pb.Y ? pa : pb;
                    SKPoint shortEnd = sampleY < pb.Y ? pb : pc;
                    float dy = shortEnd.Y - shortStart.Y;
                    if (dy < .0001f) continue;
                    float x1 = pa.X + (sampleY - pa.Y) * longSlope;
                    float x2 = shortStart.X + (sampleY - shortStart.Y) * ((shortEnd.X - shortStart.X) / dy);
                    int start = Math.Max(left, (int)MathF.Ceiling(Math.Min(x1, x2) - .5f));
                    int end = Math.Min(right, (int)MathF.Ceiling(Math.Max(x1, x2) - .5f));
                    float sampleX = start + .5f - ax, sampleOffsetY = sampleY - ay;
                    float u = ua + ux * sampleX + uy * sampleOffsetY;
                    float v = va + vx * sampleX + vy * sampleOffsetY;
                    float shade = sa + sx * sampleX + sy * sampleOffsetY;
                    int output = (y - top) * targetStride + start - left;
                    int x = start;
                    if (VectorAccelerationEnabled && Avx2.IsSupported)
                        for (; x + 8 <= end; x += 8, output += 8, u += ux * 8, v += vx * 8, shade += sx * 8)
                        {
                            var textureXs = Avx.ConvertToVector256Int32WithTruncation(Vector256.Create(u) + uOffsets);
                            var textureYs = Avx.ConvertToVector256Int32WithTruncation(Vector256.Create(v) + vOffsets);
                            textureXs = Avx2.Min(maximumX, Avx2.Max(Vector256<int>.Zero, textureXs));
                            textureYs = Avx2.Min(maximumY, Avx2.Max(Vector256<int>.Zero, textureYs));
                            var addresses = Avx2.Add(Avx2.MultiplyLow(textureYs, stride), textureXs);
                            var pixels = Avx2.GatherVector256(sourceAddress, addresses, 4);
                            var lights = Avx2.Add(Avx.ConvertToVector256Int32WithTruncation(Vector256.Create(shade) + shadeOffsets), Vector256.Create(1));
                            lights = Avx2.Min(white, Avx2.Max(Vector256.Create(1), lights));
                            var redBlues = Avx2.And(Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.And(pixels, redBlueMask), lights.AsUInt32()), 8), redBlueMask);
                            var greens = Avx2.And(Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.And(pixels, greenMask), lights.AsUInt32()), 8), greenMask);
                            Avx2.Or(alphaMask, Avx2.Or(redBlues, greens)).CopyTo(target.Slice(output, 8));
                        }
                    for (; x < end; x++, output++, u += ux, v += vx, shade += sx)
                    {
                        int textureX = Math.Clamp((int)u, 0, textureWidth - 1), textureY = Math.Clamp((int)v, 0, textureHeight - 1);
                        uint pixel = source[textureY * textureWidth + textureX];
                        uint light = (uint)Math.Clamp((int)shade + 1, 1, 256);
                        uint redBlue = (((pixel & 0x00ff00ff) * light) >> 8) & 0x00ff00ff;
                        uint green = (((pixel & 0x0000ff00) * light) >> 8) & 0x0000ff00;
                        target[output] = 0xff000000 | redBlue | green;
                    }
                }
            }
        _bitmap.NotifyPixelsChanged();
        RasterTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
        startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        canvas.Save(); canvas.ResetMatrix();
        canvas.DrawBitmap(_bitmap, new SKRect(0, 0, width, height), new SKRect(left, top, right, bottom), new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Restore();
        CompositeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
    }

    public void Dispose() => _bitmap?.Dispose();
}
