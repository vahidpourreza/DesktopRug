using DesktopRug.App.Interop;

namespace DesktopRug.App.Services;

internal readonly record struct DesktopHost(nint Host, nint IconList);

internal sealed class DesktopWindowService
{
    internal DesktopHost Discover()
    {
        DesktopHost result = default;
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
                return list == 0;
            }, 0);
            return result.Host == 0;
        }, 0);
        return result;
    }

    internal bool IsDesktopForeground(DesktopHost host)
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0 || host.Host == 0) return false;
        nint root = Native.GetAncestor(foreground, 2);
        return root == host.Host || Native.ClassName(root) is "Progman" or "WorkerW";
    }

    internal bool PlaceAboveDesktop(nint visual, nint input, DesktopHost host)
    {
        if (!Native.IsWindow(host.Host)) return false;
        // HWND_TOPMOST is deliberately never used. Find the ordinary window preceding the shell host.
        nint previous = Native.GetWindow(host.Host, 3); // GW_HWNDPREV
        nint owner = Native.GetWindow(visual, 4); // WPF's hidden ShowInTaskbar=false owner.
        while (previous != 0 && (previous == visual || previous == input || previous == owner)) previous = Native.GetWindow(previous, 3);
        // Refuse a topmost predecessor: a malformed shell stack must never promote our windows.
        if (previous != 0 && (Native.GetWindowLongPtr(previous, Native.GwlExStyle).ToInt64() & 8) != 0) return false;
        uint flags = Native.NoSize | Native.NoMove | Native.NoActivation;
        return Native.SetWindowPos(visual, previous, 0, 0, 0, 0, flags)
            && Native.SetWindowPos(input, previous, 0, 0, 0, 0, flags);
    }
}
