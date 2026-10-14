using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class SurfaceOcclusionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHiddenMiddleIsFoundWhenBothSegmentEndpointsAreVisible(bool reversedWinding)
    {
        Vector3 a = new(0, 0, 10), b = new(10, 0, 10), c = new(5, 10, 10);
        if (reversedWinding) (b, c) = (c, b);
        Assert.True(SurfaceOcclusion.TryClip(new(-5, 2, 0), new(15, 2, 0), a, b, c, out var covered));
        Near(new(.3f, .7f), covered);
        Span<Vector2> visible = stackalloc Vector2[2]; visible[0] = new(0, 1);
        Assert.Equal(2, SurfaceOcclusion.Subtract(visible, 1, covered));
        Near(new(0, .3f), visible[0]); Near(new(.7f, 1), visible[1]);
    }

    [Fact]
    public void MultipleOccludersKeepTheVisibleGapBetweenThem()
    {
        Vector3 start = new(0, 0, 0), end = new(10, 0, 0);
        Span<Vector2> visible = stackalloc Vector2[3]; visible[0] = new(0, 1);
        Assert.True(SurfaceOcclusion.TryClip(start, end, new(2, -2, 10), new(6, -2, 10), new(2, 2, 10), out var first));
        Assert.True(SurfaceOcclusion.TryClip(start, end, new(6, -2, 10), new(10, -2, 10), new(6, 2, 10), out var second));
        int count = SurfaceOcclusion.Subtract(visible, 1, first);
        count = SurfaceOcclusion.Subtract(visible, count, second);
        Assert.Equal(3, count);
        Near(new(0, .2f), visible[0]); Near(new(.4f, .6f), visible[1]); Near(new(.8f, 1), visible[2]);
    }

    [Fact]
    public void SlopingTriangleDepthClipsOnlyThePortionInFront()
    {
        Assert.True(SurfaceOcclusion.TryClip(new(1, 1, 4), new(8, 1, 4),
            new(0, 0, 0), new(10, 0, 10), new(0, 10, 0), out var covered));
        Near(new((4.6f - 1) / 7, 1), covered);
    }

    [Fact]
    public void SegmentDepthIsInterpolatedAsWellAsTriangleDepth()
    {
        Assert.True(SurfaceOcclusion.TryClip(new(1, 1, 0), new(2, 1, 20),
            new(0, 0, 10), new(10, 0, 10), new(0, 10, 10), out var covered));
        Near(new(0, .47f), covered);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(.3f, .6f)]
    [InlineData(.6f, .6f)]
    public void BehindOrEqualDepthDoesNotOcclude(float surfaceDepth, float tolerance)
    {
        Assert.False(SurfaceOcclusion.TryClip(new(1, 1, 0), new(2, 1, 0),
            new(0, 0, surfaceDepth), new(10, 0, surfaceDepth), new(0, 10, surfaceDepth), out _, tolerance));
    }

    [Fact]
    public void CrossingOnlyTheTrianglePlaneOrTouchingAVertexDoesNotCoverALength()
    {
        Assert.False(SurfaceOcclusion.TryClip(new(20, 2, 0), new(30, 2, 0),
            new(0, 0, 10), new(10, 0, 10), new(0, 10, 10), out _));
        Assert.False(SurfaceOcclusion.TryClip(new(-1, 1, 0), new(1, -1, 0),
            new(0, 0, 10), new(10, 0, 10), new(0, 10, 10), out _));
    }

    [Fact]
    public void ReversingSegmentReversesItsCoveredInterval()
    {
        Vector3 start = new(-2, 2, 0), end = new(14, 2, 0);
        Vector3 a = new(0, 0, 10), b = new(10, 0, 10), c = new(5, 10, 10);
        Assert.True(SurfaceOcclusion.TryClip(start, end, a, b, c, out var forward));
        Assert.True(SurfaceOcclusion.TryClip(end, start, a, b, c, out var backward));
        Near(new(1 - forward.Y, 1 - forward.X), backward);
    }

    [Theory]
    [InlineData(.00001f)]
    [InlineData(1e20f)]
    public void FiniteVerySmallAndVeryLargeGeometryClipsWithoutOverflow(float scale)
    {
        Assert.True(SurfaceOcclusion.TryClip(new Vector3(-5, 2, 0) * scale, new Vector3(15, 2, 0) * scale,
            new Vector3(0, 0, 10) * scale, new Vector3(10, 0, 10) * scale, new Vector3(5, 10, 10) * scale, out var covered, 0));
        Near(new(.3f, .7f), covered);
    }

    [Fact]
    public void DegenerateAndNonfiniteInputsAreRejected()
    {
        Assert.False(SurfaceOcclusion.TryClip(new(1, 1, 0), new(2, 1, 0),
            new(0, 0, 10), new(5, 5, 10), new(10, 10, 10), out _));
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.False(SurfaceOcclusion.TryClip(new(invalid, 1, 0), new(2, 1, 0),
                new(0, 0, 10), new(10, 0, 10), new(0, 10, 10), out _));
            Assert.False(SurfaceOcclusion.TryClip(new(1, 1, 0), new(2, 1, 0),
                new(0, 0, 10), new(10, 0, invalid), new(0, 10, 10), out _));
        }
        Assert.False(SurfaceOcclusion.TryClip(new(1, 1, 0), new(2, 1, 0),
            new(0, 0, 10), new(10, 0, 10), new(0, 10, 10), out _, -1));
    }

    [Fact]
    public void SubtractMergesOverlappingOcclusionWithoutLosingSeparateVisiblePieces()
    {
        Span<Vector2> visible = stackalloc Vector2[4]; visible[0] = new(0, 1);
        int count = SurfaceOcclusion.Subtract(visible, 1, new(.2f, .4f));
        count = SurfaceOcclusion.Subtract(visible, count, new(.3f, .7f));
        Assert.Equal(2, count); Near(new(0, .2f), visible[0]); Near(new(.7f, 1), visible[1]);
        count = SurfaceOcclusion.Subtract(visible, count, new(-1, .1f));
        Near(new(.1f, .2f), visible[0]);
        Assert.Equal(0, SurfaceOcclusion.Subtract(visible, count, new(-1, 2)));
    }

    [Fact]
    public void SubtractIgnoresOutsideEmptyTouchingAndInvalidRanges()
    {
        Span<Vector2> visible = stackalloc Vector2[2]; visible[0] = new(.2f, .8f);
        foreach (Vector2 covered in new[] { new Vector2(-1, 0), new(1, 2), new(.3f, .3f), new(.5f, .4f), new(0, .2f), new(.8f, 1), new(float.NaN, .4f) })
        {
            Assert.Equal(1, SurfaceOcclusion.Subtract(visible, 1, covered));
            Near(new(.2f, .8f), visible[0]);
        }
    }

    [Fact]
    public void SubtractRejectsInsufficientCapacityAndInvalidCounts()
    {
        var visible = new[] { new Vector2(0, 1) };
        Assert.Throws<ArgumentException>(() => SurfaceOcclusion.Subtract(visible, 1, new(.3f, .7f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceOcclusion.Subtract(visible, -1, new(.3f, .7f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceOcclusion.Subtract(visible, 2, new(.3f, .7f)));
    }

    [Fact]
    public void ClipAndSubtractDoNotAllocatePerStrand()
    {
        Span<Vector2> visible = stackalloc Vector2[2];
        for (int i = 0; i < 10; i++) Exercise(visible);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Exercise(visible);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Exercise(Span<Vector2> buffer)
        {
            buffer[0] = new(0, 1);
            if (SurfaceOcclusion.TryClip(new(-5, 2, 0), new(15, 2, 0),
                new(0, 0, 10), new(10, 0, 10), new(5, 10, 10), out var covered))
                SurfaceOcclusion.Subtract(buffer, 1, covered);
        }
    }

    private static void Near(Vector2 expected, Vector2 actual) => Assert.InRange(Vector2.Distance(expected, actual), 0, .00001f);
}
