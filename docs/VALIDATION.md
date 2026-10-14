# Validation — 0.2 preview, 2026-10-14

Status: **implementation gaps addressed; core, native-fixture and off-screen pipeline checks verified; physical release acceptance pending.** See [requirement coverage](REQUIREMENTS.md). Compilation and fixtures alone do not complete the original release definition of done.

## Executed evidence

Environment: Windows 10 build 19045, .NET SDK 10.0.401 / runtime 10.0.12, one physical display at 100% DPI. Application checks used off-screen windows without taking desktop focus.

| Check | Result |
| --- | --- |
| Locked NuGet restore and Release build | Passed, zero warnings / errors |
| xUnit | 76 passed, zero failed / skipped |
| Self-contained compressed win-x64 publish | Passed; approximately 83 MiB executable plus license notices |
| Published executable diagnostics | Passed; exit code 0 |
| Real Explorer host/ListView discovery and icon rectangles | Available on this Windows 10 session |
| Native styles, layered input and applied region | Passed; rug included, outside excluded, non-activating, non-topmost |
| Actual WPF pixels | Opaque fabric and transparent outside |
| Desktop z-order after repeated placements | Above the real desktop host; missing host rejected |
| Native hit routing in owned off-screen fixtures | Rug, background, occluding application and Peek routes passed |
| Native mouse messages through actual WndProc | Grab, movement, corner lift and release passed |
| Shared 100%/150% modeled viewports | Boundary crossing passed; DPI rebase drift below 0.002 physical pixels |
| Touch / pen | Layout and simulated ownership/pressure/cancellation passed; no physical device used |
| Scanline hit-region union | Uncovered holes preserved; non-finite geometry fails closed |
| Cloth, roll and unroll | Finite states, fold settling and both animation transitions passed |
| Single instance | Seven real named-kernel-object checks passed |
| Settings persistence failure | Previous preferences retained, explicit error returned, pending state cleared |
| Renderer comparison | Flat/fold, scaled negative clip, scalar/AVX2 and split-view comparisons passed |
| Full off-screen WPF loop | 59.85 FPS over 90 measured intervals; 91 actual WPF paints |
| Foreground observer | No window from the diagnostic application became foreground |

Core cases cover Verlet motion, convergence, deterministic stepping, large impulses, swept contacts, separated layers, contact velocity/friction, zero per-step physics allocations, roll material length/reversal, full-state rebasing, icon bump fields, negative coordinates, settings/recovery/concurrent saves, Peek chord validation and conflict-preserving replacement, timed/toggle/hold transitions, and cadence at 60/120/144 Hz. They are **core tests, not shell integration tests**.

Native tests use actual HWNDs, window regions, WindowFromPoint, SendMessage and the application's real window procedure. Their background/occluder windows and monitor definitions are synthetic fixtures. Touch/pen samples are modeled inputs. Real Explorer observation is separate evidence; the fixtures never substitute fake icons in the product. The focus observer allows the user to change applications and only fails if this app activates a window.

## Performance

The portable off-screen WPF loop measured **59.85 FPS**, p95 frame interval **18.37 ms**, mean WPF paint **10.12 ms**, physics **3.55 ms/frame**, and input-region work **0.95 ms/frame**. This includes WPF composition callbacks, CPU rendering, physics and shaped-input updates. It does not measure visible-monitor presentation or certify another computer, high DPI or several physical monitors.

Profiling first exposed a 31.5 ms/frame native-region bottleneck and an approximately 16 FPS loop. Replacing GDI polygon union with a reusable scanline union reduced standalone region creation from roughly 36 ms to 1.3–1.5 ms (under 1 ms when clipped). Paired material checks improved flat rendering from about 15 ms to 7 ms and folded rendering from about 14 ms to 10 ms. Mean RGB differences stayed below 0.61/255; scalar/vector and seam checks pass.

Hardware-dependent timing is separate from functional pass/fail. Composition work detaches at rest, while hidden/locked/suspended, and during detected full occlusion. Economy targets 30 Hz. Major buffers are reused; small Skia wrapper allocations remain, so the entire application is not claimed allocation-free.

## Reproduction

Run from the repository root in PowerShell:

    dotnet restore DesktopRug.slnx --locked-mode
    dotnet format whitespace DesktopRug.slnx --no-restore
    dotnet build DesktopRug.slnx -c Release --no-restore
    dotnet test tests/DesktopRug.Tests/DesktopRug.Tests.csproj -c Release --no-build --no-restore
    dotnet publish src/DesktopRug.App/DesktopRug.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/release-0.2
    & 'artifacts/release-0.2/DesktopRug.exe' --diagnostics artifacts/release-02-check

Local evidence stays under ignored artifacts/release-02-check: aggregate JSON, native interaction report, rendered comparisons, frame-loop measurements, settings/roll images and isolated test settings. Only original generated rug artwork belongs in docs/media; no real desktop screenshot, icon labels, personal paths or raw shell capture is published.

## Remaining physical acceptance

Real mouse feel on the visible desktop, real icon activation during Peek, physical touch/pen devices, a real hotkey conflict, Windows 11, Explorer restart, fullscreen/virtual-desktop transitions, multiple physical monitors/live DPI changes, lock/resume, audio quality and opt-in sign-in were not exercised in the user's working session. See [the checklist](MANUAL_TESTS.md).

The implementation now includes persistent desktop layering, shared cross-monitor cloth, configurable Peek, swept contact handling, guided roll/unroll and touch/pen input. Pathological edge–edge tangles remain outside the finite contact solver's guarantee; Explorer controls when drag bounds become available. Installer work remains deferred as the brief specifies. Code signing was not required and is not supplied. The original release acceptance gate stays open until physical checks are recorded.
