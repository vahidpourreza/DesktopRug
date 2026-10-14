using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class FrameCadenceTests
{
    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    [InlineData(144, 60)]
    [InlineData(60, 30)]
    public void CompositionRateDoesNotChangeTheRequestedAverage(int displayHz, int requestedHz)
    {
        var cadence = new FrameCadence { IntervalSeconds = 1d / requestedHz };
        int rendered = 0;
        for (int i = 0; i < displayHz * 5; i++) if (cadence.ShouldRender(i / (double)displayHz)) rendered++;
        Assert.InRange(rendered, requestedHz * 5 - 1, requestedHz * 5 + 1);
    }
    [Fact]
    public void DuplicateEventsAndLongPauseDoNotProduceABurst()
    {
        var cadence = new FrameCadence(); Assert.True(cadence.ShouldRender(1));
        Assert.False(cadence.ShouldRender(1)); Assert.False(cadence.ShouldRender(double.NaN));
        Assert.True(cadence.ShouldRender(10)); Assert.False(cadence.ShouldRender(10));
        cadence.Reset(); Assert.True(cadence.ShouldRender(11));
    }
}
