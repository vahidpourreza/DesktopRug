using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class ClothRobustnessTests
{
    [Theory]
    [InlineData(700, 490, -5, 0)]
    [InlineData(560, 840, 0, 0)]
    [InlineData(560, 840, 45, 0)]
    [InlineData(560, 700, 45, 0)]
    [InlineData(560, 800, 45, 0)]
    [InlineData(560, 840, 90, 35)]
    [InlineData(560, 840, 180, 900)]
    [InlineData(560, 840, 270, 935)]
    public void ReleasedPortraitAndLandscapeCornersSettleWithoutContactJitter(float width, float height, float rotation, int cornerIndex)
    {
        var cloth = new ClothSimulation { Viewport = new(3000, 2000) };
        cloth.Reset(new(1200, 800), new(width, height), rotation);
        Vector2 corner = RugGeometry.Project(cloth.Particles[cornerIndex].Position);
        cloth.BeginGrab(cornerIndex, corner, 140);
        for (int step = 0; step < 120; step++)
        {
            cloth.SetGrabTarget(corner + new Vector2(step * 2, step * 1.5f), 140);
            cloth.Step();
        }
        cloth.EndGrab();
        for (int step = 0; step < 1800; step++) cloth.Step();
        Assert.True(cloth.IsSleeping, $"{width}x{height} at {rotation}: remaining motion {cloth.MeanMotion}.");
        Assert.Equal(0, cloth.MeanMotion);
        Assert.All(cloth.Particles, p => Assert.True(float.IsFinite(p.Position.LengthSquared())));
        var resting = cloth.Particles.Select(p => p.Position).ToArray();
        Assert.False(cloth.Advance(.5));
        Assert.Equal(resting, cloth.Particles.Select(p => p.Position));
        cloth.BeginGrab(cornerIndex, RugGeometry.Project(cloth.Particles[cornerIndex].Position));
        Assert.False(cloth.IsSleeping);
    }

    [Fact]
    public void FallingClothDoesNotSleepBeforeItReachesTheFloor()
    {
        var cloth = new ClothSimulation();
        for (int i = 0; i < cloth.Particles.Length; i++)
        {
            cloth.Particles[i].Position.Z = 350;
            cloth.Particles[i].Previous = cloth.Particles[i].RenderPrevious = cloth.Particles[i].Position;
        }
        for (int step = 0; step < 60; step++)
        {
            cloth.Step();
            Assert.False(cloth.IsSleeping);
        }
        Assert.True(cloth.Particles.Average(p => p.Position.Z) > 100);
        for (int step = 0; step < 600; step++) cloth.Step();
        Assert.True(cloth.IsSleeping);
    }

    [Fact]
    public void AStillMovingSingleCornerPreventsWholeMeshSleep()
    {
        var cloth = new ClothSimulation { SolverIterations = 0 };
        for (int step = 0; step < 180; step++)
        {
            // Isolate one moving particle from structural propagation: its 0.15-DIP movement
            // is below the whole mesh's RMS threshold but must reset the maximum-motion guard.
            cloth.Particles[0].Previous.X = cloth.Particles[0].Position.X - .15f;
            cloth.Step();
            Assert.False(cloth.IsSleeping);
        }
    }

    [Theory]
    [InlineData(100, 400, 270)]
    [InlineData(1500, 400, 90)]
    [InlineData(800, 100, 0)]
    [InlineData(800, 900, 180)]
    public void CompactRollRemainsReachableAtViewportEdges(float centerX, float centerY, float rotation)
    {
        var cloth = new ClothSimulation { Viewport = new(1600, 1000) };
        cloth.Reset(new(centerX, centerY), new(600, 900), rotation);
        var original = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.SetRolled(true);
        Assert.Equal(original, cloth.Particles.Select(p => p.Position));
        for (int step = 0; step < 100; step++) cloth.Step();
        Assert.True(cloth.IsRolled && cloth.IsSleeping);
        Assert.All(cloth.Particles, p =>
        {
            Vector2 point = RugGeometry.Project(p.Position);
            Assert.InRange(point.X, 0, cloth.Viewport.X);
            Assert.InRange(point.Y, 0, cloth.Viewport.Y);
        });
        Assert.Equal(new Vector2(600, 900), cloth.MaterialSize);
        cloth.SetRolled(false);
        // Inspect the exact end of the guided transition, before ordinary viewport-floor
        // constraints adjust the original partially offscreen flat rug on later steps.
        while (cloth.IsRolling) cloth.Step();
        for (int i = 0; i < original.Length; i++) Assert.InRange(Vector3.Distance(original[i], cloth.Particles[i].Position), 0, .02f);
    }

    [Fact]
    public void RebasePreservesAnEdgeCorrectedCompactRoll()
    {
        var cloth = new ClothSimulation { Viewport = new(1600, 1000) };
        cloth.Reset(new(100, 400), new(600, 900), 270);
        cloth.SetRolled(true);
        for (int step = 0; step < 100; step++) cloth.Step();
        var before = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.Rebase(Vector2.Zero, 1, Vector2.Zero, 2);
        cloth.Viewport /= 2;
        cloth.Step();
        Assert.True(cloth.IsRolled && cloth.IsSleeping);
        for (int i = 0; i < before.Length; i++) Assert.InRange(Vector3.Distance(before[i], cloth.Particles[i].Position * 2), 0, .02f);
    }

    [Fact]
    public void InvalidGrabRadiusCannotSilentlyReleaseARollOrCreateAnUnresponsiveGrab()
    {
        var cloth = new ClothSimulation();
        cloth.SetRolled(true);
        foreach (float radius in new[] { 0, -1, float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => cloth.BeginGrab(0, new(500, 400), radius: radius));
            Assert.True(cloth.IsRolled);
            Assert.False(cloth.IsGrabbed);
        }
    }

    [Fact]
    public void ResetClearsThePreviousInteractionMotionMetric()
    {
        var cloth = new ClothSimulation();
        cloth.BeginGrab(0, new(600, 400), 140);
        for (int step = 0; step < 10; step++) cloth.Step();
        Assert.True(cloth.MeanMotion > 0);
        cloth.Reset(new(500, 400), new(400, 600), 35);
        Assert.Equal(0, cloth.MeanMotion);
        Assert.False(cloth.IsGrabbed);
        Assert.All(cloth.Particles, particle => Assert.Equal(particle.Position, particle.Previous));
    }
}
