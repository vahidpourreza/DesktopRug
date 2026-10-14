using System.Runtime.InteropServices;
using DesktopRug.Core;

namespace DesktopRug.App.Services;

/// <summary>Owns Peek IDs 3/4 only. Emergency hide and show/hide retain their fixed IDs 1/2.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly PeekHotkeyBinding _binding;
    private readonly Registrar _registrar;
    private readonly bool _suppressed;
    internal HotkeyService(nint window, bool suppressRegistration = false)
    {
        _suppressed = suppressRegistration;
        _registrar = new(window, suppressRegistration);
        _binding = new(_registrar);
    }

    internal PeekShortcut? Shortcut => _binding.Shortcut;
    internal string? ErrorKey { get; private set; }
    internal object[] ErrorArguments { get; private set; } = [];
    internal bool TrySetShortcut(PeekShortcut shortcut, out string? error)
    {
        _registrar.ClearError();
        bool success = _binding.TrySetShortcut(shortcut, out error);
        ErrorKey = success ? null : _registrar.ErrorKey ?? error;
        ErrorArguments = success || _registrar.ErrorKey is null ? [] : _registrar.ErrorArguments;
        return success;
    }
    internal string? GetError(AppLanguage language) => ErrorKey is { } key ? UiText.Get(language, key, ErrorArguments) : null;
    internal bool IsPeekMessage(nint id) => !_suppressed && _binding.Id != 0 && id == _binding.Id;
    internal bool IsShortcutDown => !_suppressed && Shortcut is { } shortcut
        && IsDown(shortcut.Key)
        && (!shortcut.Modifiers.HasFlag(PeekModifiers.Control) || IsDown(0x11))
        && (!shortcut.Modifiers.HasFlag(PeekModifiers.Alt) || IsDown(0x12))
        && (!shortcut.Modifiers.HasFlag(PeekModifiers.Shift) || IsDown(0x10));

    public void Dispose() => _binding.Dispose();
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private sealed class Registrar(nint window, bool suppressed) : IPeekHotkeyRegistrar
    {
        internal string? ErrorKey { get; private set; }
        internal object[] ErrorArguments { get; private set; } = [];
        internal void ClearError() { ErrorKey = null; ErrorArguments = []; }
        public bool TryRegister(int id, PeekShortcut shortcut, out string? error)
        {
            if (suppressed || RegisterHotKey(window, id, (uint)shortcut.Modifiers | 0x4000, (uint)shortcut.Key))
            { error = null; return true; }
            int code = Marshal.GetLastWin32Error();
            ErrorKey = code == 1409
                ? "{0} is already in use. Choose another shortcut; your previous shortcut is unchanged."
                : "Windows could not register {0} (error {1}). Your previous shortcut is unchanged.";
            ErrorArguments = code == 1409 ? [shortcut] : [shortcut, code];
            error = UiText.Get(AppLanguage.English, ErrorKey, ErrorArguments);
            return false;
        }
        public bool Unregister(int id) => suppressed || UnregisterHotKey(window, id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
