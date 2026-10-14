using System.Windows.Media;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

/// <summary>Vsync-driven while awake; detached entirely when cloth is at rest, hidden or occluded.</summary>
internal sealed class RenderClock : IDisposable
{
    private readonly FrameCadence _cadence = new();
    internal bool IsEnabled { get; private set; }
    internal TimeSpan Interval { get => TimeSpan.FromSeconds(_cadence.IntervalSeconds); set => _cadence.IntervalSeconds = value.TotalSeconds; }
    internal event EventHandler? Tick;
    internal void Start()
    {
        if (IsEnabled) return;
        IsEnabled = true; _cadence.Reset(); CompositionTarget.Rendering += Rendering;
    }
    internal void Stop()
    {
        if (!IsEnabled) return;
        IsEnabled = false; CompositionTarget.Rendering -= Rendering;
    }
    private void Rendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering && _cadence.ShouldRender(rendering.RenderingTime.TotalSeconds)) Tick?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() => Stop();
}
