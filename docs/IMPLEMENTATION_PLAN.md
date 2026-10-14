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
- One rug belongs to one monitor. Moving it to another display is explicit; continuous cross-monitor cloth is deferred.

Status: all six milestone feature areas are implemented in a runnable preview. Release builds, automated tests, rendering checks and off-screen native integration are verified. Hands-on desktop validation remains pending, so the milestones and initial-release definition of done are not labeled fully complete. See VALIDATION.md and MANUAL_TESTS.md.
