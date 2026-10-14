#nullable enable
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DesktopRug.App.Interop;

internal enum PointerDevice : uint { Unknown = 0, Touch = 2, Pen = 3 }
internal enum PointerPhase { Down, Move, Up, Enter, Leave, Canceled }

internal readonly record struct PointerSample(uint Id, PointerDevice Device, PointerPhase Phase,
    Vector2 ScreenPosition, bool InContact, bool IsSecondary, float? Pressure);

/// <summary>
/// Decodes ordinary messages delivered to our own input HWND. WM_POINTERDOWN implicitly captures
/// that pointer; no global targets, device registration, injection, or focus changes are used.
/// Read synchronously inside WndProc, before retrieving the next Windows message.
/// </summary>
internal static class PointerInput
{
    internal const int WmPointerUpdate = 0x0245, WmPointerDown = 0x0246, WmPointerUp = 0x0247,
        WmPointerEnter = 0x0249, WmPointerLeave = 0x024A, WmPointerActivate = 0x024B,
        WmPointerCaptureChanged = 0x024C;
    internal const int NoActivate = 3;

    internal static bool IsPointerMessage(int message) => message is WmPointerDown or WmPointerUpdate
        or WmPointerUp or WmPointerEnter or WmPointerLeave or WmPointerCaptureChanged;
    internal static uint GetPointerId(nint wParam) => unchecked((uint)(long)wParam) & 0xFFFF;

    /// <summary>
    /// On false, preserve normal mouse fallback unless this pointer already owns a gesture; an
    /// accepted gesture must consume its entire pointer sequence and cancel if data becomes unavailable.
    /// CAPTURECHANGED carries an ID but no fresh position, so its sample position must be ignored.
    /// </summary>
    internal static bool TryRead(nint window, int message, nint wParam, nint lParam, out PointerSample sample)
    {
        sample = default;
        if (window == 0 || !IsPointerMessage(message)) return false;
        uint id = GetPointerId(wParam);
        if (message == WmPointerCaptureChanged)
        {
            sample = new(id, PointerDevice.Unknown, PointerPhase.Canceled, default, false, false, null);
            return true;
        }
        if (!GetPointerType(id, out uint type) || type is not (2 or 3)
            || !GetPointerInfo(id, out var info) || info.Target != window || info.Id != id) return false;

        var phase = message switch
        {
            WmPointerDown => PointerPhase.Down,
            WmPointerUp => PointerPhase.Up,
            WmPointerEnter => PointerPhase.Enter,
            WmPointerLeave => PointerPhase.Leave,
            _ => PointerPhase.Move
        };
        if ((info.Flags & 0x00008000) != 0) phase = PointerPhase.Canceled;
        bool secondary = (info.Flags & 0x00000020) != 0;
        float? pressure = null;
        if (type == (uint)PointerDevice.Pen && GetPointerPenInfo(id, out var pen))
        {
            secondary |= (pen.Flags & 1) != 0;
            if ((pen.Mask & 1) != 0) pressure = Math.Clamp(pen.Pressure / 1024f, 0, 1);
        }
        sample = new(id, (PointerDevice)type, phase, new Vector2(info.PixelLocation.X, info.PixelLocation.Y),
            (info.Flags & 0x00000004) != 0, secondary, pressure);
        return true;
    }

    /// <summary>
    /// Filter promoted mouse messages only after accepting native pointer input. Legacy devices that
    /// send mouse messages alone should retain their normal mouse path.
    /// https://learn.microsoft.com/windows/win32/tablet/system-events-and-mouse-messages
    /// </summary>
    internal static bool IsPromotedMouseMessage() => IsPromotedMouseMessage(GetMessageExtraInfo());
    internal static bool IsPromotedMouseMessage(nint extraInfo) =>
        (unchecked((uint)(long)extraInfo) & 0xFFFFFF00) == 0xFF515700;

    // This check is side-effect free and can run in offscreen diagnostics without touch hardware.
    internal static bool HasExpectedNativeLayout()
    {
        int pointerSize = IntPtr.Size == 8 ? 96 : 88;
        int pixelOffset = IntPtr.Size == 8 ? 32 : 24;
        int performanceOffset = IntPtr.Size == 8 ? 80 : 72;
        return Marshal.SizeOf<PointerInfo>() == pointerSize
            && Marshal.SizeOf<PointerPenInfo>() == pointerSize + 24
            && Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PixelLocation)).ToInt32() == pixelOffset
            && Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PerformanceCount)).ToInt32() == performanceOffset
            && Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.ButtonChangeType)).ToInt32() == performanceOffset + 8
            && Marshal.OffsetOf<PointerPenInfo>(nameof(PointerPenInfo.Pressure)).ToInt32() == pointerSize + 8;
    }

    // Native field order and widths: winuser.h POINTER_INFO / POINTER_PEN_INFO.
    // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-pointer_info
    // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-pointer_pen_info
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public nint SourceDevice, Target;
        public PointerPoint PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo Pointer;
        public uint Flags, Mask, Pressure, Rotation;
        public int TiltX, TiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerInfo(uint pointerId, out PointerInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo info);
    [DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();
}
