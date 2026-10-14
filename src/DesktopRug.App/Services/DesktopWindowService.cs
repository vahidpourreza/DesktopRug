using DesktopRug.App.Interop;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

internal readonly record struct DesktopHost(nint Host, nint IconList);

internal sealed class DesktopWindowService
{
    internal bool IsAvailable(DesktopHost host) => host.Host != 0 && Native.IsWindow(host.Host) && Native.IsWindowVisible(host.Host);
    internal DesktopHost Discover()
    {
        DesktopHost result = default, fallback = default;
        Native.EnumWindows((top, _) =>
        {
            string cls = Native.ClassName(top);
            if (cls is not ("Progman" or "WorkerW")) return true;
            Native.EnumChildWindows(top, (child, _) =>
            {
                if (Native.ClassName(child) != "SHELLDLL_DefView") return true;
                nint list = 0;
                Native.EnumChildWindows(child, (candidate, _) =>
                {
                    if (Native.ClassName(candidate) == "SysListView32") { list = candidate; return false; }
                    return true;
                }, 0);
                if (list != 0) result = new(top, list);
                else fallback = new(top, 0); // Verified desktop view, but icon provider/hierarchy unavailable.
                return list == 0;
            }, 0);
            return result.Host == 0;
        }, 0);
        return result.Host != 0 ? result : fallback;
    }

    internal bool IsDesktopForeground(DesktopHost host)
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0 || host.Host == 0) return false;
        nint root = Native.GetAncestor(foreground, 2);
        return root == host.Host || Native.ClassName(root) is "Progman" or "WorkerW";
    }

    internal bool PlaceAboveDesktop(nint visual, nint input, DesktopHost host)
        => PlaceAboveDesktop([(visual, input)], host);

    internal bool PlaceAboveDesktop(IReadOnlyList<(nint Visual, nint Input)> surfaces, DesktopHost host)
    {
        if (!Native.IsWindow(host.Host)) return false;
        HashSet<nint> own = [];
        foreach (var surface in surfaces)
        {
            own.Add(surface.Visual); own.Add(surface.Input);
            own.Add(Native.GetWindow(surface.Visual, 4));
        }
        nint previous = Native.GetWindow(host.Host, 3); // GW_HWNDPREV
        while (previous != 0 && own.Contains(previous)) previous = Native.GetWindow(previous, 3);
        // If the desktop is the first ordinary window, its predecessor may legitimately be the
        // topmost taskbar. HWND_NOTOPMOST places us at the top of the ordinary band without promotion.
        if (previous != 0 && (Native.GetWindowLongPtr(previous, Native.GwlExStyle).ToInt64() & 8) != 0) previous = -2;
        uint flags = Native.NoSize | Native.NoMove | Native.NoActivation;
        foreach (var surface in surfaces)
            if (!Native.SetWindowPos(surface.Visual, previous, 0, 0, 0, 0, flags)
                || !Native.SetWindowPos(surface.Input, previous, 0, 0, 0, 0, flags)) return false;
        return true;
    }

    internal bool IsLayerCorrect(IReadOnlyList<(nint Visual, nint Input)> surfaces, DesktopHost host)
    {
        if (!Native.IsWindow(host.Host)) return false;
        HashSet<nint> expected = [];
        foreach (var surface in surfaces)
        {
            if (Native.IsWindowVisible(surface.Visual)) expected.Add(surface.Visual);
            if (Native.IsWindowVisible(surface.Input)) expected.Add(surface.Input);
            if ((Native.GetWindowLongPtr(surface.Visual, Native.GwlExStyle).ToInt64() & 8) != 0
                || (Native.GetWindowLongPtr(surface.Input, Native.GwlExStyle).ToInt64() & 8) != 0) return false;
        }
        nint window = Native.GetWindow(host.Host, 3);
        for (int count = 0; count < 2048 && window != 0 && expected.Count > 0; count++)
        {
            if (!expected.Remove(window) && Native.IsWindowVisible(window)) return false;
            window = Native.GetWindow(window, 3);
        }
        return expected.Count == 0;
    }

    internal bool IsFullyOccluded(nint visual, RectD area)
    {
        if (area.Width <= 0 || area.Height <= 0) return true;
        nint window = Native.GetWindow(visual, 3);
        for (int count = 0; count < 2048 && window != 0; count++, window = Native.GetWindow(window, 3))
        {
            if (!Native.IsWindowVisible(window)) continue;
            long style = Native.GetWindowLongPtr(window, Native.GwlExStyle).ToInt64();
            if ((style & (Native.Layered | Native.Transparent)) != 0) continue;
            if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, 4) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(window, out var rect)) continue;
            if (rect.Left <= area.X && rect.Top <= area.Y && rect.Right >= area.Right && rect.Bottom >= area.Bottom)
                return true;
        }
        return false;
    }
}
