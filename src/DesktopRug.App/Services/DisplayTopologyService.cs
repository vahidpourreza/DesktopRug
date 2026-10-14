using System.Runtime.InteropServices;
using DesktopRug.App.Interop;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

internal sealed class DisplayTopologyService
{
    internal IReadOnlyList<DisplayInfo> Discover()
    {
        List<DisplayInfo> displays = [];
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (!Native.GetMonitorInfo(monitor, ref info)) return true;
            double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
            static RectD Convert(Native.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            displays.Add(new(info.Device, Convert(info.Monitor), Convert(info.Work), scale, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return displays;
    }
}
