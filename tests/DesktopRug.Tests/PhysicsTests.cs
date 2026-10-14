using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class PhysicsTests
{
    private static ClothSimulation Cloth() => new(18, 14) { Viewport = new(1600, 1000) };

    [Fact]
    public void GravityAcceleratesAnElevatedParticle()
    {
        var cloth = Cloth();
        for (int i = 0; i < cloth.Particles.Length; i++)
        {
            cloth.Particles[i].Position.Z = 100; cloth.Particles[i].Previous.Z = 100;
        }
        float before = cloth.Particles[100].Position.Z;
        cloth.Step(); float first = cloth.Particles[100].Position.Z;
        cloth.Step(); float second = cloth.Particles[100].Position.Z;
        Assert.True(first < before); Assert.True(first - second > before - first);
    }

    [Fact]
    public void ConstraintSolverReducesAnInjectedStretch()
    {
        var cloth = Cloth();
        cloth.Particles[90].Position += new Vector3(60, 30, 20);
        cloth.Particles[90].Previous = cloth.Particles[90].Position;
        float Error() => cloth.Constraints.Sum(c => MathF.Abs(Vector3.Distance(cloth.Particles[c.A].Position, cloth.Particles[c.B].Position) - c.Length));
        float before = Error();
        for (int i = 0; i < 60; i++) cloth.Step();
        Assert.True(Error() < before * .2f);
    }

    [Fact]
    public void SameInputsProduceIdenticalResults()
    {
        var a = Cloth(); var b = Cloth();
        foreach (var cloth in new[] { a, b })
        {
            cloth.BeginGrab(0, RugGeometry.Project(cloth.Particles[0].Position), 100);
            for (int i = 0; i < 160; i++) { cloth.SetGrabTarget(new(260 + i, 200 + i * .5f), 120); cloth.Step(); }
            cloth.EndGrab(); for (int i = 0; i < 160; i++) cloth.Step();
        }
        Assert.Equal(a.Particles.Select(p => p.Position), b.Particles.Select(p => p.Position));
    }

    [Fact]
    public void FixedTimeStepDoesNotDependOnRenderFrequency()
    {
        var a = Cloth(); var b = Cloth();
        a.BeginGrab(0, new(400, 250), 100); b.BeginGrab(0, new(400, 250), 100);
        for (int i = 0; i < 120; i++) a.Advance(ClothSimulation.TimeStep);
        for (int i = 0; i < 60; i++) b.Advance(ClothSimulation.TimeStep * 2);
        Assert.Equal(a.Particles.Select(p => p.Position), b.Particles.Select(p => p.Position));
    }

    [Fact]
    public void HugePointerImpulseIsBoundedAndFinite()
    {
        var cloth = Cloth(); var original = cloth.Particles[0].Position;
        cloth.BeginGrab(0, new(float.MaxValue, -float.MaxValue), 1e9f);
        cloth.Step();
        Assert.True(Vector3.Distance(original, cloth.Particles[0].Position) < 30);
        for (int i = 0; i < 600; i++)
        {
            cloth.SetGrabTarget(new(i % 2 == 0 ? 1e8f : -1e8f, 1e7f), 200); cloth.Step();
        }
        Assert.All(cloth.Particles, p =>
        {
            Assert.True(float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z));
            Assert.InRange(p.Position.Z, 1.4f, 401);
        });
        var worstStructuralStretch = cloth.Constraints.Where(c => c.Stiffness == 1)
            .Max(c => Vector3.Distance(cloth.Particles[c.A].Position, cloth.Particles[c.B].Position) / c.Length);
        Assert.True(worstStructuralStretch < 1.7f, $"Stretch ratio was {worstStructuralStretch}.");
    }

    [Fact]
    public void FabricSleepsAfterReleaseAndWakesOnInteraction()
    {
        var cloth = Cloth();
        cloth.BeginGrab(0, new(370, 270), 100);
        for (int i = 0; i < 80; i++) cloth.Step();
        cloth.EndGrab();
        for (int i = 0; i < 2400; i++) cloth.Step();
        Assert.True(cloth.IsSleeping, $"Remaining motion: {cloth.MeanMotion}");
        Assert.False(cloth.Advance(.1));
        cloth.BeginGrab(0, new(320, 200)); Assert.False(cloth.IsSleeping);
    }

    [Fact]
    public void FlatClothSleepsWithoutContinuouslyRunning()
    {
        var cloth = Cloth(); for (int i = 0; i < 300; i++) cloth.Step();
        Assert.True(cloth.IsSleeping);
    }

    [Fact]
    public void InvalidPointerAndElapsedValuesCannotPoisonSimulation()
    {
        var cloth = Cloth(); cloth.BeginGrab(0, new(250, 200));
        cloth.SetGrabTarget(new(float.NaN, float.PositiveInfinity), float.NaN);
        Assert.False(cloth.Advance(double.NaN)); Assert.False(cloth.Advance(-1));
        cloth.Step(); Assert.All(cloth.Particles, p => Assert.True(float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z)));
    }

    [Fact]
    public void BumpsLiftFabricButNeverBecomeLargeHills()
    {
        var cloth = Cloth(); cloth.Bumps.SetIcons([new(470, 370, 60, 60)]); cloth.Reset(new(500, 400), new(320, 230), 0);
        for (int i = 0; i < 100; i++) cloth.Step();
        Assert.InRange(cloth.Particles.Max(p => p.Position.Z), 3, 10);
    }

    [Fact]
    public void ResetAppliesSizeAndRotationWithoutResidualVelocity()
    {
        var cloth = Cloth(); cloth.Reset(new(500, 400), new(300, 200), 90);
        var first = cloth.Particles[0].Position;
        var last = cloth.Particles[cloth.Columns - 1].Position;
        Assert.InRange(last.X - first.X, -.001f, .001f); Assert.InRange(last.Y - first.Y, 299.99f, 300.01f);
        Assert.All(cloth.Particles, p => Assert.Equal(p.Position, p.Previous));
    }

    [Fact]
    public void PickingRejectsOutsideAndFindsInteriorFabric()
    {
        var cloth = Cloth(); Assert.Equal(-1, cloth.Pick(new(1, 1)));
        Assert.InRange(cloth.Pick(new(500, 400)), 0, cloth.Particles.Length - 1);
    }
}
