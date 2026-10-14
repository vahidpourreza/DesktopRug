namespace DesktopRug.Core;

/// <summary>Accumulates composition time so a 60 Hz target also works on 120/144 Hz displays.</summary>
public sealed class FrameCadence
{
    private double? _last;
    private double _pending;
    public double IntervalSeconds { get; set; } = 1d / 60;
    public void Reset() { _last = null; _pending = 0; }
    public bool ShouldRender(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        if (_last is null) { _last = seconds; return true; }
        double elapsed = seconds - _last.Value;
        if (elapsed <= 0) return false;
        _last = seconds;
        double interval = double.IsFinite(IntervalSeconds) && IntervalSeconds > 0 ? IntervalSeconds : 1d / 60;
        _pending += Math.Min(elapsed, interval * 4);
        if (_pending + 1e-7 < interval) return false;
        _pending = Math.Max(0, _pending % interval);
        if (_pending > interval - 1e-7) _pending = 0;
        return true;
    }
}
