namespace DesktopRug.Core;

public interface IPeekHotkeyRegistrar
{
    bool TryRegister(int id, PeekShortcut shortcut, out string? error);
    bool Unregister(int id);
}

/// <summary>Reserves a replacement before releasing the working shortcut, so a conflict cannot disable Peek.</summary>
public sealed class PeekHotkeyBinding(IPeekHotkeyRegistrar registrar) : IDisposable
{
    private readonly HashSet<int> _reserved = [];
    private bool _disposed;
    public PeekShortcut? Shortcut { get; private set; }
    public int Id { get; private set; }

    public bool TrySetShortcut(PeekShortcut requested, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!requested.TryValidate(out error)) return false;
        if (Shortcut == requested) return true;
        int candidate = Id == 3 ? 4 : 3;
        if (_reserved.Contains(candidate))
        {
            if (!registrar.Unregister(candidate))
            { error = "Windows could not release an earlier shortcut reservation. Try again or restart Desktop Rug."; return false; }
            _reserved.Remove(candidate);
        }
        if (!registrar.TryRegister(candidate, requested, out error)) return false;
        _reserved.Add(candidate);
        if (Id != 0 && !registrar.Unregister(Id))
        {
            if (registrar.Unregister(candidate)) _reserved.Remove(candidate);
            error = "Windows could not replace the shortcut. The previous shortcut remains active.";
            return false;
        }
        _reserved.Remove(Id);
        Id = candidate; Shortcut = requested; return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in _reserved) registrar.Unregister(id);
        _reserved.Clear(); Shortcut = null; Id = 0;
    }
}
