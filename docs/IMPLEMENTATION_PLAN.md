# Implementation plan

1. Establish a runnable .NET 10 WPF shell, procedural rug renderer, tray, and emergency hide.
2. Add deterministic cloth physics and an independently shaped input surface; test geometry and large impulses.
3. Discover real Explorer icon bounds through read-only UI Automation, on a worker thread.
4. Add settings, three procedural patterns, customization, sound (off by default), display handling, and recovery.
5. Build, run automated and off-screen Windows diagnostics, publish a portable executable, and document unexecuted interactive checks.

## Highest risks

- Explorer's icon host hierarchy is not a public contract. Enumerate rather than assume a single host.
- An always-topmost window would cover apps. Keep ordinary non-activating windows adjacent to the desktop host and gate visibility on the desktop being foreground.
- Transparent input must work across process boundaries. Separate a click-through visual window from a nearly transparent native input window, whose region is the union of the actual projected triangles. Peek hides both.
- Do not test by taking over the user's mouse, restarting Explorer, changing display settings, or showing a foreground window. Automated checks use off-screen windows and real read-only shell discovery; manual interaction remains explicitly unverified.
- A shared world now drives separate window pairs on every monitor. Crossing/rebasing is verified in native off-screen fixtures; physical mixed-DPI hardware acceptance remains open.

Status: the 0.2 implementation closes the previous desktop-layer, cross-monitor, Peek preference, contact, roll, touch/pen, and performance gaps. Core tests, real-shell observation, native off-screen input/occlusion fixtures, singleton objects, and rendering/pipeline checks provide distinct evidence. Hands-on physical validation remains pending; the original release acceptance gate is not labeled complete. See REQUIREMENTS.md, VALIDATION.md and MANUAL_TESTS.md.
