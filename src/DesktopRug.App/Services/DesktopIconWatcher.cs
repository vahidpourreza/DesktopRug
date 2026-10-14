using DesktopRug.App.Interop;

namespace DesktopRug.App.Services;

/// <summary>Explorer-scoped, out-of-process notifications. Callbacks never query accessibility or move icons.</summary>
internal sealed class DesktopIconWatcher : IDisposable
{
    private readonly Native.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private DesktopHost _host;
    private int _dirty = 1;
    internal bool Dragging { get; private set; }
    internal bool HasChanges => Volatile.Read(ref _dirty) != 0;
    internal void Acknowledge() => Interlocked.Exchange(ref _dirty, 0);
    internal bool NotificationsAvailable => _hooks.Count > 0;

    internal DesktopIconWatcher() => _callback = OnEvent;
    internal void Watch(DesktopHost host)
    {
        if (host == _host && (host.IconList == 0 || _hooks.Count > 0)) return;
        foreach (nint hook in _hooks) Native.UnhookWinEvent(hook);
        _hooks.Clear(); _host = host; Dragging = false; Interlocked.Exchange(ref _dirty, 1);
        if (host.IconList == 0) return;
        Native.GetWindowThreadProcessId(host.IconList, out uint process);
        if (process == 0) return;
        Add(0x8000, 0x800C, process); // create/destroy/show/hide/reorder/location/name
        Add(0x8021, 0x8026, process); // object drag lifecycle
        Add(0x000E, 0x000F, process); // system drag lifecycle
    }
    private void Add(uint first, uint last, uint process)
    {
        nint hook = Native.SetWinEventHook(first, last, 0, _callback, process, 0, 0);
        if (hook != 0) _hooks.Add(hook);
    }
    private void OnEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (hwnd != _host.IconList && !Native.IsChild(_host.IconList, hwnd)
            && Native.GetAncestor(hwnd, 2) != _host.Host) return;
        if (evt is 0x8021 or 0x000E) Dragging = true;
        if (evt is 0x8022 or 0x8023 or 0x8026 or 0x000F) Dragging = false;
        Interlocked.Exchange(ref _dirty, 1);
    }
    public void Dispose()
    {
        foreach (nint hook in _hooks) Native.UnhookWinEvent(hook);
        _hooks.Clear(); GC.KeepAlive(_callback);
    }
}
