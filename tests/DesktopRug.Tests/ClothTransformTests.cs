using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class ClothTransformTests
{
    [Fact]
    public void RebasePreservesScreenPositionsHistoryAndRestLengths()
    {
        var cloth = new ClothSimulation(18, 14);
        cloth.BeginGrab(0, new(300, 200), 80);
        for (int i = 0; i < 20; i++) cloth.Step();
        var before = cloth.Particles.ToArray();
        var lengths = cloth.Constraints.Select(c => c.Length).ToArray();
        Vector2 oldOrigin = new(-1920, -300), newOrigin = new(300, 200);
        cloth.Rebase(oldOrigin, 1.5f, newOrigin, 2);
        for (int i = 0; i < before.Length; i++)
        {
            AssertScreenEqual(before[i].Position, cloth.Particles[i].Position);
            AssertScreenEqual(before[i].Previous, cloth.Particles[i].Previous);
            AssertScreenEqual(before[i].RenderPrevious, cloth.Particles[i].RenderPrevious);
        }
        for (int i = 0; i < lengths.Length; i++) Assert.InRange(Math.Abs(lengths[i] * .75f - cloth.Constraints[i].Length), 0, .0001f);
        Assert.Equal(new Vector2(480, 337.5f), cloth.MaterialSize);
        Assert.True(cloth.IsGrabbed);

        void AssertScreenEqual(Vector3 beforePosition, Vector3 afterPosition)
        {
            Vector2 expected = oldOrigin + RugGeometry.Project(beforePosition) * 1.5f;
            Vector2 actual = newOrigin + RugGeometry.Project(afterPosition) * 2;
            Assert.InRange(Vector2.Distance(expected, actual), 0, .001f);
            Assert.InRange(Math.Abs(beforePosition.Z * 1.5f - afterPosition.Z * 2), 0, .001f);
        }
    }

    [Fact]
    public void RebaseRoundTripPreservesAnActiveGrabTrajectory()
    {
        var reference = new ClothSimulation(18, 14); var rebased = new ClothSimulation(18, 14);
        foreach (var cloth in new[] { reference, rebased })
        {
            cloth.BeginGrab(0, new(300, 240), 120);
            for (int i = 0; i < 20; i++) cloth.Step();
        }
        rebased.Rebase(new(-200, 0), 1, new(100, -50), 2);
        rebased.Rebase(new(100, -50), 2, new(-200, 0), 1);
        for (int i = 0; i < 20; i++) { reference.Step(); rebased.Step(); }
        for (int i = 0; i < reference.Particles.Length; i++)
            Assert.InRange(Vector3.Distance(reference.Particles[i].Position, rebased.Particles[i].Position), 0, .02f);
    }

    [Fact]
    public void CompactRollRetainsMaterialLengthAndDevelopsActualHeight()
    {
        var cloth = new ClothSimulation();
        cloth.SetRolled(true);
        Assert.True(cloth.IsRolling);
        for (int i = 0; i < 100; i++) cloth.Step();
        Assert.True(cloth.IsRolled); Assert.False(cloth.IsRolling); Assert.True(cloth.IsSleeping);
        Assert.Equal(new Vector2(640, 450), cloth.MaterialSize);
        float arc = 0;
        for (int row = 1; row < cloth.Rows; row++)
            arc += Vector3.Distance(cloth.Particles[row * cloth.Columns].Position, cloth.Particles[(row - 1) * cloth.Columns].Position);
        Assert.InRange(arc, 430, 451);
        Assert.True(cloth.Particles.Max(p => p.Position.Z) > 40);
        float depth = cloth.Particles.Max(p => p.Position.Y) - cloth.Particles.Min(p => p.Position.Y);
        Assert.InRange(depth, 30, 90);
    }

    [Fact]
    public void UnrollReturnsToOriginalDimensionsAndSettles()
    {
        var cloth = new ClothSimulation(24, 18);
        cloth.Reset(new(500, 400), new(420, 300), 30);
        var before = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.SetRolled(true); for (int i = 0; i < 100; i++) cloth.Step();
        cloth.SetRolled(false); for (int i = 0; i < 500; i++) cloth.Step();
        Assert.False(cloth.IsRolled); Assert.False(cloth.IsRolling); Assert.True(cloth.IsSleeping);
        for (int i = 0; i < before.Length; i++) Assert.InRange(Vector3.Distance(before[i], cloth.Particles[i].Position), 0, .1f);
    }

    [Fact]
    public void ReversingRollOrGrabbingDoesNotTeleportTheMesh()
    {
        var cloth = new ClothSimulation(24, 18);
        cloth.SetRolled(true); for (int i = 0; i < 40; i++) cloth.Step();
        var before = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.SetRolled(false);
        Assert.Equal(before, cloth.Particles.Select(p => p.Position));
        cloth.SetRolled(true); for (int i = 0; i < 100; i++) cloth.Step();
        before = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.BeginGrab(0, RugGeometry.Project(before[0]));
        Assert.Equal(before, cloth.Particles.Select(p => p.Position));
        Assert.False(cloth.IsRolled); Assert.False(cloth.IsRolling); Assert.True(cloth.IsGrabbed);
    }

    [Fact]
    public void RebasePreservesTheRestingRolledShapeAfterWake()
    {
        var cloth = new ClothSimulation();
        cloth.SetRolled(true); for (int i = 0; i < 100; i++) cloth.Step();
        var before = cloth.Particles.Select(p => p.Position).ToArray();
        cloth.Rebase(new(-100, 0), 1, new(-100, 0), 2);
        cloth.Step();
        for (int i = 0; i < before.Length; i++) Assert.InRange(Vector3.Distance(before[i], cloth.Particles[i].Position * 2), 0, .01f);
        Assert.True(cloth.IsRolled); Assert.True(cloth.IsSleeping);
    }
}
