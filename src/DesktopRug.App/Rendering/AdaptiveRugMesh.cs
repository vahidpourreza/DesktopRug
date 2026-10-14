using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Rendering;

/// <summary>
/// Removes redundant raster triangles, never simulation particles. Shared row/column cuts
/// keep the mesh conforming; every omitted vertex is checked against its replacement
/// triangle in projection, elevation and lighting before the coarse patch is accepted.
/// </summary>
internal sealed class AdaptiveRugMesh
{
    private readonly int _columns, _rows;
    private readonly bool[] _keepColumns, _keepRows;
    private readonly int[] _columnCuts, _rowCuts;
    private const float PositionTolerance = .3f, HeightTolerance = .6f, ShadeTolerance = 1.5f;

    internal AdaptiveRugMesh(int columns, int rows)
    {
        _columns = columns; _rows = rows;
        _keepColumns = new bool[columns]; _keepRows = new bool[rows];
        _columnCuts = new int[columns]; _rowCuts = new int[rows];
    }

    internal int Build(SKPoint[] positions, SKColor[] colors, ClothParticle[] particles, ushort[] indices)
    {
        Array.Clear(_keepColumns); Array.Clear(_keepRows);
        _keepColumns[0] = _keepColumns[^1] = true;
        _keepRows[0] = _keepRows[^1] = true;
        SplitLine(0, _columns - 1, true);
        SplitLine(0, _rows - 1, false);
        int columnCount, rowCount;
        bool changed;
        do
        {
            columnCount = Cuts(_keepColumns, _columnCuts);
            rowCount = Cuts(_keepRows, _rowCuts);
            changed = false;
            for (int r = 0; r < rowCount - 1; r++)
                for (int c = 0; c < columnCount - 1; c++)
                {
                    int left = _columnCuts[c], right = _columnCuts[c + 1];
                    int top = _rowCuts[r], bottom = _rowCuts[r + 1];
                    if (right == left + 1 && bottom == top + 1) continue;
                    int a = top * _columns + left, b = top * _columns + right;
                    int d = bottom * _columns + left, e = bottom * _columns + right;
                    for (int y = top; y <= bottom; y++)
                        for (int x = left; x <= right; x++)
                        {
                            float u = (x - left) / (float)(right - left), v = (y - top) / (float)(bottom - top);
                            int index = y * _columns + x;
                            float error = u + v <= 1
                                ? Error(index, a, b, d, 1 - u - v, u, v)
                                : Error(index, b, e, d, 1 - v, u + v - 1, 1 - u);
                            if (error <= 1) continue;
                            if (x > left && x < right && !_keepColumns[x]) { _keepColumns[x] = true; changed = true; }
                            if (y > top && y < bottom && !_keepRows[y]) { _keepRows[y] = true; changed = true; }
                        }
                }
        } while (changed);

        int count = 0;
        for (int r = 0; r < rowCount - 1; r++)
            for (int c = 0; c < columnCount - 1; c++)
            {
                ushort a = (ushort)(_rowCuts[r] * _columns + _columnCuts[c]);
                ushort b = (ushort)(_rowCuts[r] * _columns + _columnCuts[c + 1]);
                ushort d = (ushort)(_rowCuts[r + 1] * _columns + _columnCuts[c]);
                ushort e = (ushort)(_rowCuts[r + 1] * _columns + _columnCuts[c + 1]);
                indices[count++] = a; indices[count++] = b; indices[count++] = d;
                indices[count++] = b; indices[count++] = e; indices[count++] = d;
            }
        return count;

        void SplitLine(int start, int end, bool horizontal)
        {
            if (end <= start + 1) return;
            float worst = 1; int split = -1;
            int lines = horizontal ? _rows : _columns;
            for (int line = 0; line < lines; line++)
            {
                int a = horizontal ? line * _columns + start : start * _columns + line;
                int b = horizontal ? line * _columns + end : end * _columns + line;
                for (int step = start + 1; step < end; step++)
                {
                    int index = horizontal ? line * _columns + step : step * _columns + line;
                    float t = (step - start) / (float)(end - start);
                    float error = Error(index, a, b, b, 1 - t, t, 0);
                    if (error > worst) { worst = error; split = step; }
                }
            }
            if (split < 0) return;
            (horizontal ? _keepColumns : _keepRows)[split] = true;
            SplitLine(start, split, horizontal); SplitLine(split, end, horizontal);
        }

        float Error(int index, int a, int b, int c, float wa, float wb, float wc)
        {
            float dx = positions[index].X - (positions[a].X * wa + positions[b].X * wb + positions[c].X * wc);
            float dy = positions[index].Y - (positions[a].Y * wa + positions[b].Y * wb + positions[c].Y * wc);
            float dz = particles[index].Position.Z - (particles[a].Position.Z * wa + particles[b].Position.Z * wb + particles[c].Position.Z * wc);
            float shade = colors[index].Red - (colors[a].Red * wa + colors[b].Red * wb + colors[c].Red * wc);
            return Math.Max(Math.Max((dx * dx + dy * dy) / (PositionTolerance * PositionTolerance),
                dz * dz / (HeightTolerance * HeightTolerance)), shade * shade / (ShadeTolerance * ShadeTolerance));
        }
    }

    private static int Cuts(bool[] selected, int[] output)
    {
        int count = 0;
        for (int index = 0; index < selected.Length; index++) if (selected[index]) output[count++] = index;
        return count;
    }
}
