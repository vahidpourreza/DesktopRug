using System.Runtime.InteropServices;

namespace DesktopRug.App.Interop;

/// <summary>Read-only logical mouse settings; never changes pointer or system preferences.</summary>
internal static class MouseInputSettings
{
    internal static int PrimaryVirtualKey(bool buttonsSwapped) => buttonsSwapped ? 2 : 1;
    internal static bool IsPrimaryDown => (Native.GetAsyncKeyState(PrimaryVirtualKey(GetSystemMetrics(23) != 0)) & 0x8000) != 0;
    internal static float DoubleClickHalfWidth => Math.Max(1, GetSystemMetrics(36) / 2f);
    internal static float DoubleClickHalfHeight => Math.Max(1, GetSystemMetrics(37) / 2f);
    internal static float DragWidth => Math.Max(1, GetSystemMetrics(68));
    internal static float DragHeight => Math.Max(1, GetSystemMetrics(69));

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
