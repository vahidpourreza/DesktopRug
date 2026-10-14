using System.Numerics;
using System.Diagnostics;
using DesktopRug.Core;
using Xunit;
using Xunit.Abstractions;

namespace DesktopRug.Tests;

public sealed class ClothContactTests
{
    private readonly ITestOutputHelper _output;
    public ClothContactTests(ITestOutputHelper output) => _output = output;

    private static ClothParticle Particle(Vector3 before, Vector3 after) => new() { Position = after, Previous = before, RenderPrevious = before };
    private static ClothParticle Stationary(float x, float y, float z = 0) => Particle(new(x, y, z), new(x, y, z));

    [Theory]
    [InlineData(10, -10, 1)]
    [InlineData(-10, 10, -1)]
    public void SweptVertexCannotTunnelThroughAWholeFace(float before, float after, float direction)
    {
        bool hit = ClothContactSolver.TryContact(Particle(new(5, 5, before), new(5, 5, after)),
            Stationary(0, 0), Stationary(20, 0), Stationary(0, 20), 3, out var contact);
        Assert.True(hit);
        Assert.InRange(contact.Time, .499f, .501f);
        Assert.InRange(contact.Normal.Z * direction, .999f, 1.001f);
        Assert.InRange(contact.Penetration, 12.99f, 13.01f);
        Assert.InRange(Vector3.Distance(contact.Barycentric, new(.5f, .25f, .25f)), 0, .001f);
    }

    [Fact]
    public void AFaceSweepingAcrossAStationaryVertexAlsoCreatesContact()
    {
        Assert.True(ClothContactSolver.TryContact(Stationary(5, 5, 10),
            Particle(new(0, 0, 0), new(0, 0, 20)), Particle(new(20, 0, 0), new(20, 0, 20)),
            Particle(new(0, 20, 0), new(0, 20, 20)), 3, out var contact));
        Assert.InRange(contact.Time, .499f, .501f);
        Assert.True(contact.Normal.Z > .999f);
        Assert.InRange(contact.Penetration, 12.99f, 13.01f);
    }

    [Fact]
    public void PlaneCrossingOutsideTheTriangleDoesNotCreateAPhantomContact()
    {
        Assert.False(ClothContactSolver.TryContact(Particle(new(25, 25, 10), new(25, 25, -10)),
            Stationary(0, 0), Stationary(20, 0), Stationary(0, 20), 3, out _));
    }

    [Fact]
    public void ThicknessContactIncludesTheEdgeOfATriangle()
    {
        Assert.True(ClothContactSolver.TryContact(Stationary(10, -1, 1), Stationary(0, 0),
            Stationary(20, 0), Stationary(0, 20), 3, out var contact));
        Assert.InRange(contact.Penetration, 1.58f, 1.59f);
        Assert.InRange(Vector3.Distance(contact.Barycentric, new(.5f, .5f, 0)), 0, .001f);
        Assert.True(contact.Normal.Y < 0 && contact.Normal.Z > 0);
    }

    [Fact]
    public void DegenerateTrianglesDoNotProduceInvalidNormals()
    {
        Assert.False(ClothContactSolver.TryContact(Stationary(5, 0), Stationary(0, 0),
            Stationary(10, 0), Stationary(20, 0), 3, out _));
    }

    [Fact]
    public void BroadPhaseResolvesSeparatedMeshLayersWithoutMovingNeighborsApart()
    {
        const int columns = 8, rows = 8;
        var particles = new ClothParticle[columns * rows];
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                float y = row < 4 ? row * 10 : (7 - row) * 10 + 2;
                float z = row < 4 ? 10 : 11;
                particles[row * columns + col] = Stationary(col * 10 + (row < 4 ? 0 : 2), y, z);
            }
        var solver = new ClothContactSolver(columns, particles.Length);
        for (int i = 0; i < 4; i++) solver.Solve(particles, 10, 3);
        Assert.True(solver.ContactsLastStep > 0);
        Assert.False(solver.BudgetExhaustedLastStep);
        float upper = particles.Skip(columns * 5).Average(p => p.Position.Z);
        float lower = particles.Take(columns * 2).Average(p => p.Position.Z);
        Assert.True(upper - lower > 2, $"Layer separation was {upper - lower}.");
        Assert.All(particles, p => Assert.True(float.IsFinite(p.Position.LengthSquared())));
    }

    [Fact]
    public void FixedStepContactSolverDoesNotAllocateWhileInteracting()
    {
        var cloth = new ClothSimulation(24, 18);
        cloth.BeginGrab(0, new(400, 300), 120);
        for (int i = 0; i < 20; i++) cloth.Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) cloth.Step();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RepeatedBalancedModeFoldsKeepContactWorkBounded()
    {
        var cloth = new ClothSimulation();
        cloth.BeginGrab(0, new(180, 175), 160);
        for (int i = 0; i < 40; i++) cloth.Step();
        int contacts = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 240; i++)
        {
            cloth.SetGrabTarget(new(420 + MathF.Sin(i * .04f) * 140, 400 + MathF.Cos(i * .04f) * 140), 90);
            cloth.Step(); contacts += cloth.ContactsLastStep;
            Assert.False(cloth.ContactBudgetExhausted);
        }
        watch.Stop();
        Assert.True(contacts > 0, "The fold scenario must exercise actual self-contact.");
        _output.WriteLine($"Balanced 36x26: {watch.Elapsed.TotalMilliseconds / 240:F3} ms/fixed step; {contacts} contacts across 240 steps.");
    }

    [Fact]
    public void DiagnosticCornerPullSettlesAtBalancedResolution()
    {
        var cloth = new ClothSimulation { Viewport = new(1000, 760) };
        cloth.Reset(new(500, 380), new(700, 490), -5);
        Vector2 corner = RugGeometry.Project(cloth.Particles[0].Position);
        cloth.BeginGrab(0, corner, 140);
        for (int i = 0; i < 120; i++)
        {
            cloth.SetGrabTarget(corner + new Vector2(i * 2, i * 1.5f), 140);
            cloth.Step();
        }
        cloth.EndGrab();
        for (int i = 0; i < 1800; i++)
        {
            cloth.Step();
            if (i % 300 == 299) _output.WriteLine($"Step {i + 1}: velocity={cloth.MeanMotion:F5}; renderMotion={Math.Sqrt(cloth.Particles.Average(p => Vector3.DistanceSquared(p.Position, p.RenderPrevious))):F5}; contacts={cloth.ContactsLastStep}; maxZ={cloth.Particles.Max(p => p.Position.Z):F3}");
        }
        Assert.True(cloth.IsSleeping, $"Remaining motion: {cloth.MeanMotion}; contacts: {cloth.ContactsLastStep}.");
    }
}
