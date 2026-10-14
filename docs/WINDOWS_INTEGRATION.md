# Windows integration

## Desktop layer

The app enumerates top-level `Progman` and `WorkerW` windows, then searches descendants for `SHELLDLL_DefView` and `SysListView32`. These class names are an Explorer implementation detail, **not a guaranteed Windows contract**. There is no hardcoded HWND, remote process allocation, injected code, `LVM_SETITEMPOSITION`, or undocumented WorkerW-spawning message.

Each monitor has independent visual/input top-level windows with `WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE`, never `WS_EX_TOPMOST`. `SetWindowPos` places the whole group above the desktop host and below ordinary applications with `SWP_NOACTIVATE`. WPF's hidden owner windows are skipped. If the desktop's predecessor is already in the topmost band, `HWND_NOTOPMOST` inserts the rug into the ordinary band without promoting it. WPF `ShowActivated=false`, mouse no-activation and pointer no-activation reinforce the focus boundary.

No cross-process `SetParent` is used. A foreground hook and 250 ms maintenance check verify ordinary z-order; they do not hide the rug simply because another app gains focus. Exposed desktop areas keep their rug. A covering opaque window pauses animation; moving it away resumes. Transparent/layered or cloaked windows are not treated as definite opaque occluders. Initial showing is done at zero opacity until safe z-order is established.

Explorer is rediscovered during maintenance. A verified desktop view can host the rug even when its icon ListView is unavailable; bumps then report unavailable. A missing desktop host suspends presentation until it returns. Tool windows hidden by Show Desktop are safely restored above the host. Alternative shells and future Explorer hierarchies may not be supported. Restart behavior is implemented but a deliberate live Explorer restart remains an unexecuted acceptance check.

## Input and Peek

The visual window has `WS_EX_TRANSPARENT`; shadows never capture clicks. A separate native input window uses `WS_EX_LAYERED` with alpha 1. `RugHitRegion` rasterizes the union of all projected triangles into a reusable bit mask, preserves holes, coalesces identical scanline bands and sends non-overlapping rectangles to `ExtCreateRegion`. This avoids the expensive GDI polygon-union path. Native region exclusion does not rely on `HTTRANSPARENT` alone. Invalid geometry fails closed; an input surface is never shown without a valid region.

Only the rug surface intercepts the pointer. The input region refreshes at up to 30 Hz while animation renders at its quality target. Native capture is requested during a gesture, but non-activating background windows cannot rely on it outside their region. A press prepares a gesture; only movement beyond the Windows drag threshold starts deforming cloth. This preserves roll state for ordinary clicks and double-click unroll. While an owned gesture is pending or active, the loop polls `GetCursorPos` and the mapped physical primary button, honoring swapped-button settings. Pending clicks do not redraw a sleeping mesh. No global mouse hook, synthetic mouse input or focus stealing is used.

Double-click timing and screen-pixel rectangles use Windows settings; hiding, cancellation and unrelated gestures reset the click sequence. Wheel deltas accumulate to 120-unit notches and preserve multiple-notch magnitude. Geometry commands cancel pending/active gestures across all surfaces before resetting cloth, without overwriting the saved location during Restore.

Touch and pen use ordinary `WM_POINTER` messages delivered to the owned window, implicit pointer capture, synchronous native pointer decoding and single-pointer ownership. Pen pressure adjusts corner lift. Promoted mouse events are suppressed after native pointer handling, and cancellation clears ownership even when a secondary/barrel action hides the window. No global pointer target or UIAccess privilege is requested.

Peek cancels grabs and hides all window pairs. Modes are timed, toggle and keyboard hold; hold polls release on a separate 30 ms timer because rendering is hidden. Right-click activation is optional. The native Peek shortcut is persisted and a replacement is registered before the old binding is released. Conflicts retain the working binding and show a settings error. Emergency hide remains sticky and fixed. Failure to register `Ctrl+Alt+Esc` still prevents presentation.

## Icons

UI Automation obtains Explorer ListItem names/rectangles with a batched cache request scoped to the discovered ListView's children. Work runs away from the dispatcher. A one-second caller timeout and reused in-flight task prevent an unbounded worker backlog. A permanently hung provider leaves tracking unavailable while the rug remains functional; a process-isolated UIA worker is a possible future improvement.

Explorer-scoped location, create/destroy and drag notifications request refreshed bounds. Active icon drags use a 50 ms target interval, other changes are coalesced around 75 ms, and an idle three-second fallback handles missed notifications. Explorer may expose a drag image's new rectangle only after drop; the app never fabricates positions. Hidden icons, unavailable providers and restarts produce diagnostics with an empty or last-known bump field. Names stay in memory and are never logged.

## Display coordinates

Native screen/UIA positions are physical pixels, including negative coordinates. One shared world is based at the virtual desktop's minimum physical coordinate and has an explicit pixels-per-world-unit scale. Each monitor translates that world into its own canvas and input region. A grab stays owned by its starting input window while crossing boundaries. Release-time DPI rebasing transforms particle histories, constraints, targets and roll state together, preserving screen geometry; the resulting size adjustment is persisted. The WPF manifest is PerMonitorV2. The WinForms-only DPI entry-point warning is selectively disabled because WinForms supplies only the tray.

Actual monitor surfaces forward `WM_DPICHANGED`; system topology notifications are coalesced and retained during async settings saves. Rebuild errors hide surfaces and retry with backoff. Removing a monitor relocates an inaccessible rug onto an available display. Two off-screen native viewports at modeled 100%/150% scaling verify crossing, hit regions and subpixel-continuous rebasing. This is not physical mixed-DPI hardware verification; attachment, live scale changes and Windows 11 acceptance remain pending.

## References

- [Window features and layered-window hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
- [SetWindowPos and z-order](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [SetCapture limitations for background windows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture)
- [UI Automation element discovery](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-obtainingelements)
- [UI Automation threading](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues)
