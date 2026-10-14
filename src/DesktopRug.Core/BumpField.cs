using System.Numerics;

namespace DesktopRug.Core;

public readonly record struct IconBump(Vector2 Center, float Radius, float Height);

public sealed class BumpField
{
    private IconBump[] _bumps = [];
    public void SetIcons(IEnumerable<RectD> icons) => _bumps = icons.Select(rect =>
        new IconBump(rect.Center, Math.Clamp((float)Math.Max(rect.Width, rect.Height) * .48f, 14, 46),
            Math.Clamp((float)Math.Min(rect.Width, rect.Height) * .10f, 2.5f, 6))).ToArray();

    public float HeightAt(Vector2 position)
    {
        float total = 0;
        foreach (var bump in _bumps)
        {
            float d = Vector2.DistanceSquared(position, bump.Center) / (bump.Radius * bump.Radius);
            if (d < 10) total += bump.Height * MathF.Exp(-d * .9f);
        }
        return 1.5f + 8 * (1 - MathF.Exp(-total / 8));
    }
}
