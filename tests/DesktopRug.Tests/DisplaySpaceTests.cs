using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class DisplaySpaceTests
{
    private static readonly DisplayInfo[] Displays =
    [
        new("left", new(-2560, -400, 2560, 1440), default, 1.5, false),
        new("primary", new(0, 0, 1920, 1080), default, 1, true),
        new("right", new(1920, 0, 3840, 2160), default, 2, false)
    ];

    [Fact]
    public void SharedWorldIncludesNegativeCoordinatesAndEveryMonitor()
    {
        var space = new RugCoordinateSpace(RugCoordinateSpace.Union(Displays), 1.5f);
        Assert.Equal(new RectD(-2560, -400, 8320, 2560), space.ScreenBounds);
        foreach (var display in Displays)
        {
            Vector2 point = display.Bounds.Center;
            Assert.True(Vector2.Distance(point, space.WorldToScreen(space.ScreenToWorld(point))) < .001f);
        }
    }

    [Fact]
    public void SharedProjectionDoesNotJumpAtDifferentDpiMonitorEdges()
    {
        var space = new RugCoordinateSpace(RugCoordinateSpace.Union(Displays), 1);
        for (int x = 1900; x < 1940; x++)
        {
            Vector2 point = new(x, 350), world = space.ScreenToWorld(point);
            var primaryClient = Displays[1].ScreenToLocal(space.WorldToScreen(world));
            var rightClient = Displays[2].ScreenToLocal(space.WorldToScreen(world));
            Assert.True(Vector2.Distance(Displays[1].LocalToScreen(primaryClient), Displays[2].LocalToScreen(rightClient)) < .001f);
        }
    }

    [Fact]
    public void NearestMonitorHandlesTheGapAndRemovedMonitor()
    {
        Assert.Equal("right", RugCoordinateSpace.NearestDisplay(Displays, new(2500, 700)).DeviceName);
        Assert.Equal("left", RugCoordinateSpace.NearestDisplay(Displays, new(-1200, 200)).DeviceName);
        Assert.Equal("primary", RugCoordinateSpace.NearestDisplay(Displays[1..2], new(-1200, 200)).DeviceName);
    }

    [Fact]
    public void ScreenBoundsIntersectionDoesNotFabricateAreaForDisjointMonitors()
    {
        Assert.Equal(0, RugCoordinateSpace.Intersect(new(100, 100, 400, 300), Displays[0].Bounds).Width);
        Assert.Equal(new RectD(1920, 100, 100, 300), RugCoordinateSpace.Intersect(new(1820, 100, 200, 300), Displays[2].Bounds));
    }
}
