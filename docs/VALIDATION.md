# Validation — 2026-10-14

Status: **runnable preview; automated and off-screen checks verified; interactive release acceptance pending.** The application is not labeled a fully validated Windows release.

## Executed

Environment: Windows 10 build 19045, .NET SDK 10.0.401 / runtime 10.0.12, one display at 100% DPI. All app checks used off-screen windows without taking desktop focus.

| Check | Result |
| --- | --- |
| Locked ordinary NuGet restore | Passed |
| Release solution build | Passed, zero warnings / errors |
| xUnit | 24 passed, zero failed / skipped |
| Self-contained compressed win-x64 publish | Passed; portable executable about 83 MiB, plus license notices |
| Published executable `--diagnostics` | Passed; process exit code 0 |
| Real Explorer host and icon ListView discovery | Passed on this Windows 10 session |
| Real UI Automation icon rectangles | Available; actual icons read, no synthetic icon source |
| Visual and input HWND creation | Passed |
| Input layered alpha, no-activation style and shown state | Passed |
| Native applied region: cloth interior / outside | Included / excluded correctly |
| WPF rendered pixels: rug / outside | Opaque rug / transparent outside |
| Desktop z-order after five placements | Visual and input above the real desktop host; neither topmost |
| Missing desktop host | Rejected safely |
| Peek | Both native windows hidden |
| Focus observer | No window from the diagnostic app became foreground |
| Controller show, hide, Peek, end Peek, settings changes | Passed with visible presentation suppressed |
| Settings UI, original patterns and folded cloth | Rendered and visually inspected off-screen |
| Cloth after a large corner pull | Finite positions and eventual sleep |

The focus check observes foreground events belonging to the app, rather than failing when the person working at the computer changes their own foreground application.

Unit coverage includes gravity/Verlet motion, constraint convergence, determinism, fixed timestep across render rates, damping/sleep, extreme pointer impulses, invalid inputs, icon bump falloff/overlap/movement, rotation/resizing, hit testing, lift projection, screen-local conversion, negative coordinates, 100/125/150/200% coordinate scales, settings validation, persistence, corrupt-file backup and concurrent saves. These are core tests, **not shell integration tests**.

## Profiling

The final portable smoke run measured approximately 219 ms for 120 active balanced-grid physics steps (1.82 ms/step), and 16.2 ms per CPU-rendered 1000 × 760 frame of the folded/settled rug. Repeated local runs varied roughly 16–20 ms per rendered frame. Managed renderer allocation was about 322 bytes/frame for disposed Skia wrappers; working arrays and paint objects are reused. This excludes Windows composition and input-region work and is **not proof of 60 desktop FPS**. The render timer stops when the cloth sleeps or is hidden. Economy provides a smaller simulation and a 30 Hz target.

## Commands

```powershell
dotnet restore DesktopRug.slnx --locked-mode
dotnet format whitespace DesktopRug.slnx --no-restore
dotnet build DesktopRug.slnx -c Release --no-restore
dotnet test tests/DesktopRug.Tests/DesktopRug.Tests.csproj -c Release --no-build --no-restore
dotnet publish src/DesktopRug.App/DesktopRug.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/portable
.\artifacts\portable\DesktopRug.exe --diagnostics .\artifacts\final-check
```

Local evidence stays under ignored `artifacts/`: diagnostic JSON, original renderer PNGs, settings render and local test settings. Only generated rug artwork is included in `docs/media`; no real desktop screenshot, icon names, personal paths or raw shell capture is published.

## Remaining manual acceptance

Actual mouse dragging/corner manipulation on the visible desktop, real desktop icon activation during Peek, tray operation by a person, emergency-hotkey conflicts, Windows 11, Explorer restart, full-screen transitions, multiple monitors, live DPI changes, lock/resume and opt-in sign-in behavior were deliberately not exercised in the user's active work session. See [the complete checklist](MANUAL_TESTS.md).

The feature areas for all six milestones exist, but these manual gates remain open. Continuous cross-monitor cloth, robust triangle self-collision, a physical roll animation, touch/pen, installer and code signing are not implemented. The current conservative desktop layer hides the rug whenever another application has focus; this limitation is explicit in the product UI and README.
