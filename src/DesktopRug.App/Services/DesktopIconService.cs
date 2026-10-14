using System.Windows.Automation;
using DesktopRug.App.Interop;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

internal sealed record DesktopIconInfo(string Label, RectD Bounds);
internal sealed record IconSnapshot(IReadOnlyList<DesktopIconInfo> Icons, string Diagnostic, bool Available);

/// <summary>Read-only UI Automation, scoped to Explorer's desktop ListView. No remote-process writes or shell mutations.</summary>
internal sealed class DesktopIconService
{
    private readonly object _sync = new();
    private Task<IconSnapshot>? _inFlight;

    internal Task<IconSnapshot> ReadAsync(nint list)
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            // A hung Explorer provider must not consume a new thread on every poll.
            return _inFlight = Task.Run(() => Read(list));
        }
    }

    private static IconSnapshot Read(nint list)
    {
        if (list == 0 || !Native.IsWindow(list)) return new([], "Explorer icon list unavailable; the rug still works without bumps.", false);
        if (!Native.IsWindowVisible(list)) return new([], "Desktop icons are hidden. Icon bumps are inactive.", true);
        try
        {
            var element = AutomationElement.FromHandle(list);
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.IsOffscreenProperty);
            using var activeCache = cache.Activate();
            var children = element.FindAll(TreeScope.Children, condition);
            List<DesktopIconInfo> icons = [];
            foreach (AutomationElement child in children)
            {
                var current = child.Cached;
                var bounds = current.BoundingRectangle;
                if (current.IsOffscreen || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                icons.Add(new(current.Name, new(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
            }
            return new(icons, $"Tracking {icons.Count} real desktop icons through Windows accessibility.", true);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException
            or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new([], "Explorer accessibility is temporarily unavailable; retrying without icon bumps.", false);
        }
    }
}
