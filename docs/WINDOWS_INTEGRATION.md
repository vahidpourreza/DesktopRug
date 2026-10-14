# Windows integration

## Desktop layer

The app enumerates top-level `Progman` and `WorkerW` windows, then searches descendants for `SHELLDLL_DefView` and `SysListView32`. These class names are an Explorer implementation detail, **not a guaranteed Windows contract**. There is no hardcoded HWND, remote process allocation, injected code, `LVM_SETITEMPOSITION`, or undocumented WorkerW-spawning message.

The visual and input windows are independent top-level windows with `WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE`, never `WS_EX_TOPMOST`. `SetWindowPos` places them directly above the discovered desktop host and below its ordinary predecessor using `SWP_NOACTIVATE`. A topmost predecessor is refused. WPF `ShowActivated=false` and `WM_MOUSEACTIVATE → MA_NOACTIVATE` reinforce the focus boundary.

To avoid relying on fragile cross-process parenting and mixed-DPI `SetParent` behavior, visibility is additionally restricted to the desktop being the foreground surface. A foreground event hook and a 500 ms fallback check hide both windows when an application, settings window or shell popup takes focus. **The rug consequently disappears from exposed desktop areas while another app is active.** This is an intentional conservative first-version trade-off. Full-screen apps are covered by the same rule, not a topmost exception.

Explorer is rediscovered during maintenance. Missing/restarted Explorer suspends presentation until a valid host returns. Show Desktop can hide ordinary tool windows; the app detects this and re-shows them only when the desktop is foreground. Alternative shells and future Explorer hierarchies may not be supported.

## Input and Peek

The visual window has `WS_EX_TRANSPARENT`; it never captures clicks, including on its shadows. A separate native input window uses `WS_EX_LAYERED` with alpha 1 and a native polygon region built from **all projected mesh triangles**. Triangle winding is normalized so folded surfaces form a union. Native region exclusion works across process/thread boundaries; the design does not rely on `HTTRANSPARENT` alone. Region creation failure hides the input window and prevents it being shown unshaped.

Only the rug surface intercepts the pointer. The input region refreshes at up to 30 Hz while animation renders at its quality target. Native capture is requested during a drag, but non-activating background windows cannot rely on it outside their region. While, and only while, an existing rug drag is active, the animation loop polls `GetCursorPos` and the left-button state to maintain dragging and detect release. No global mouse hook, synthetic mouse input or focus stealing is used.

Peek cancels any grab and **hides both HWNDs**. Actual Explorer icons receive native clicks until the timeout or a second Peek command. Emergency hide is sticky and cancels Peek. The tray always provides hide and exit. If `Ctrl+Alt+Esc` cannot be registered, presentation fails closed; settings explain why. Optional show/Peek hotkey conflicts retain tray alternatives.

## Icons

UI Automation obtains Explorer ListItem names and bounding rectangles, scoped to the discovered icon ListView. Only visible children with non-empty bounds participate. Work runs away from the WPF dispatcher. A two-second caller timeout keeps the app responsive; an in-flight task is reused so a hung provider cannot create an unbounded worker backlog. If that provider never returns, icon tracking remains unavailable while the rug and controls continue to work. A process-level UIA isolation worker is a possible future improvement.

Refresh is approximately every 1.5 seconds while the rug is enabled. Explorer may expose a dragged icon's new rectangle only after drop; this version does not promise continuous under-pointer tracking. Hidden icons, missing UIA providers and Explorer restarts produce diagnostics and either an empty or last-known bump field. No fallback fabricates icons. Names remain in memory and are never logged.

## Display coordinates

Native screen positions and UIA bounds are physical pixels, including negative coordinates. Cloth and pointer geometry use local DIPs relative to the selected monitor. Conversion subtracts that monitor's physical origin and divides by its DPI scale; rendering applies the inverse scale. A PerMonitorV2 manifest is used by WPF. The WinForms DPI analyzer warning is selectively disabled because WinForms only supplies the tray and does not own the application entry point.

Display topology is refreshed after display/DPI messages and system display notifications. A missing selected monitor falls back to primary. One rug lives on one monitor. Switching it is explicit through settings; seamless cross-monitor dragging and mixed-DPI live transitions are **not claimed complete**. Their manual tests remain pending.

## References

- [Window features and layered-window hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
- [SetWindowPos and z-order](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [SetCapture limitations for background windows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture)
- [UI Automation element discovery](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-obtainingelements)
- [UI Automation threading](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues)
