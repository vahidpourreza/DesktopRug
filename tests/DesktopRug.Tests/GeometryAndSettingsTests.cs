using System.Numerics;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class GeometryAndSettingsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NegativeMonitorCoordinatesRoundTripAtEveryDpi(double scale)
    {
        var display = new DisplayInfo("left", new(-2560, -600, 2560, 1440), default, scale, false);
        Vector2 screen = new(-1400, -240);
        Vector2 local = display.ScreenToLocal(screen);
        Assert.True(local.X > 0 && local.Y > 0);
        Assert.True(Vector2.Distance(screen, display.LocalToScreen(local)) < .001);
    }

    [Fact]
    public void ScreenRectangleMapsIntoLocalDips()
    {
        var display = new DisplayInfo("left", new(-1920, 0, 1920, 1080), default, 1.5, false);
        var local = display.ScreenToLocal(new RectD(-1800, 150, 90, 90));
        Assert.Equal(new RectD(80, 100, 60, 60), local);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(200)]
    public void LiftProjectionAndPointerUnprojectionAgree(float height)
    {
        Vector3 position = new(100, -250, height);
        Assert.True(Vector3.Distance(position, RugGeometry.Unproject(RugGeometry.Project(position), height)) < .001f);
    }

    [Fact]
    public void TriangleHitTestHandlesReflectedFoldsAndDegenerateFaces()
    {
        Assert.True(RugGeometry.InTriangle(new(2, 2), new(0, 0), new(10, 0), new(0, 10)));
        Assert.True(RugGeometry.InTriangle(new(2, 2), new(0, 10), new(10, 0), new(0, 0)));
        Assert.False(RugGeometry.InTriangle(new(9, 9), new(0, 0), new(10, 0), new(0, 10)));
        Assert.False(RugGeometry.InTriangle(new(2, 0), new(0, 0), new(10, 0), new(5, 0)));
    }

    [Fact]
    public void BumpFieldHasSmoothFalloffAndBoundedOverlap()
    {
        var field = new BumpField(); field.SetIcons(Enumerable.Repeat(new RectD(80, 80, 40, 40), 30));
        float peak = field.HeightAt(new(100, 100));
        Assert.InRange(peak, 5, 9.5f);
        Assert.True(peak > field.HeightAt(new(120, 100)));
        Assert.Equal(1.5f, field.HeightAt(new(1000, 1000)));
        field.SetIcons([new(480, 480, 40, 40)]);
        Assert.Equal(1.5f, field.HeightAt(new(100, 100)));
        Assert.True(field.HeightAt(new(500, 500)) > 2);
    }

    [Fact]
    public void SettingsClampInvalidNumbersAndUnknownEnums()
    {
        var settings = (new RugSettings
        {
            CenterX = double.NaN,
            CenterY = -999,
            Rotation = float.PositiveInfinity,
            Shadow = float.NaN,
            Quality = (SimulationQuality)999,
            Size = (RugSize)999,
            Pattern = (RugPattern)999,
            PeekSeconds = 999
        }).Validated();
        Assert.Equal(.43, settings.CenterX); Assert.Equal(.05, settings.CenterY); Assert.Equal(0, settings.Rotation);
        Assert.Equal(SimulationQuality.Balanced, settings.Quality); Assert.Equal(RugPattern.Kashan, settings.Pattern);
        Assert.Equal(30, settings.PeekSeconds); Assert.True(float.IsFinite(settings.Shadow));
    }

    [Fact]
    public async Task SettingsRoundTripAndCorruptFileIsPreserved()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DesktopRug-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var service = new SettingsService(folder);
            var settings = new RugSettings { CenterX = .3, Pattern = RugPattern.Tabriz, Monitor = "left", Rotation = 45, Sound = true };
            await service.SaveAsync(settings);
            Assert.Equal(settings, await service.LoadAsync());
            await File.WriteAllTextAsync(service.FilePath, "{broken");
            Assert.Equal(new RugSettings(), await service.LoadAsync());
            Assert.NotNull(service.Diagnostic);
            string backup = Assert.Single(Directory.GetFiles(folder, "settings-recovery-*.json"));
            Assert.Equal("{broken", await File.ReadAllTextAsync(backup));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ConcurrentSettingsWritesRemainValid()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DesktopRug-Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(folder);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.SaveAsync(new() { Rotation = i })));
            var result = await service.LoadAsync(); Assert.InRange(result.Rotation, 0, 19); Assert.Null(service.Diagnostic);
        }
        finally { Directory.Delete(folder, true); }
    }
}
