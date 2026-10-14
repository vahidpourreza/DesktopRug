# Architecture

## Responsibilities

- `DesktopRug.Core`: framework-independent geometry, display coordinate mapping, validated settings, atomic persistence, smooth icon bump field, deterministic Verlet cloth.
- `Rendering/PatternFactory`: generates three original textures and optionally reads a user-supplied image. No commercial artwork, pre-rendered animation or network assets.
- `Rendering/RugRenderer`, `AdaptiveRugMesh`, `RugMaterialRasterizer`: project and depth-sort the cloth, merge sufficiently coplanar patches, and draw full-resolution texture/vertex lighting with a specialized AVX2-or-scalar material pass. Skia composes the surface with shadows, edge thickness and fringe.
- `Windows/RugOverlayWindow`: transparent WPF + Skia visual surface, always non-activating and click-through.
- `Windows/RugInputWindow` and `RugHitRegion`: a distinct, non-activating native layered input window per monitor. A reusable scanline mask unions projected triangles and emits coalesced native region rectangles, excluding shadows, holes and empty space. Mouse, touch and pen share gesture handling.
- `Services/DesktopWindowService`: discovers Explorer's icon host and manages ordinary z-order immediately above it.
- `Services/DesktopIconService` and `DesktopIconWatcher`: batched read-only UI Automation on a worker thread, awakened by Explorer-scoped location/drag notifications with bounded fast and idle refresh intervals.
- `Services/DisplayTopologyService`: enumerates monitor bounds, work areas and DPI.
- `Services/RugController`: per-monitor surfaces sharing one cloth, visibility/occlusion, settings, display/session recovery and scheduling. `RenderClock` uses composition events and `FrameCadence` maintains the requested rate on different refresh-rate displays.
- `PeekState`, `PeekHotkeyBinding`, `HotkeyService`: persisted timed/toggle/hold preferences, validation and transactional native shortcut replacement.
- `Services/AudioService`: precomputed, quiet synthetic noise envelopes; separate from physics and disabled by default.
- `Services/Diagnostics`: off-screen smoke testing, render inspection and bounded profiling. It does not substitute simulated icons for the app's real shell source.

The app uses concrete small services rather than a DI container and one-interface-per-class ceremony. The core remains independently testable without WPF or an interactive Windows session.

## Simulation

Balanced mode uses 36 × 26 particles. Economy uses 24 × 18 and High uses 44 × 32. Each particle stores its current, previous and interpolated rendering positions. The fixed step is 1/120 second with an eight-step accumulator cap after stalls. Structural, diagonal shear and two-hop bending constraints run in alternating sweep directions. Gravity, damping, floor friction, bounded grab target motion and particle velocities prevent large pointer impulses from exploding the mesh.

A grab radius weights neighboring particles. Middle drags translate the fabric gently; corners lift it and leave resting fabric behind. `ClothContactSolver` uses a preallocated swept spatial hash, moving vertex/triangle crossing tests, thickness contacts, normal-velocity cancellation and friction. Work is bounded; exhaustion is observable. Pathological edge–edge intersections are not mathematically eliminated. Roll/unroll guides the existing material through a spiral over time; grabbing releases the guide into free cloth physics.

Icon rectangles are transformed from physical screen pixels to a shared `RugCoordinateSpace`, based at the virtual desktop's minimum screen coordinate. Each monitor draws and hit-tests that same world. DPI rebasing preserves every particle/history/interaction state in physical screen coordinates. Smooth radial icon influences add with bounded saturation. Floor samples are reused during solver iterations and reconciled after movement. No icon positions are written.

After quiet settling the cloth sleeps and detaches from composition events. Lock, suspend, hiding and Peek stop animation; complete opaque occlusion also pauses it unless a grab is active. The rug remains visible in exposed desktop areas when other apps have focus. Economy targets 30 Hz; Balanced/High target 60 Hz. Cadence is tested against 60/120/144 Hz event streams rather than relying on a coarse dispatcher timer.

Arrays, region masks, raster targets and paint objects are reused. Skia path snapshots remain small bounded allocations; the whole app is not claimed allocation-free. Layered shadow strokes avoid full-screen Gaussian blur intermediates. A retained reference renderer enables pixel comparisons; diagnostics measure CPU rasterization separately from the complete off-screen WPF/physics/input loop.

## Storage and lifecycle

Settings use normalized monitor-local center, named size plus scale, rotation, Peek configuration and last monitor identity. Crossing DPI boundaries preserves physical dimensions and updates the persisted scale. Missing monitors move the cloth onto an available display. Shape deformation is intentionally not persisted. JSON writes are serialized, flushed and replaced atomically; unreadable JSON receives a recovery copy. Explicit settings application drains pending position saves, reserves the new shortcut without losing the old one, awaits persistence, and retains errors in the dialog. Display notifications arriving during an apply are queued and topology is rediscovered before reconstruction.

`SingleInstanceService` uses a per-session mutex and an early-created named event, marshals show requests onto the owner thread, and handles abandoned ownership and shutdown races. No entry window is shown automatically. The tray owns lifetime. Shutdown saves settings, removes hotkeys/hooks/surfaces and releases primary ownership last. Diagnostic kernel objects use unique names and never signal a live app. Diagnostic outputs are isolated and no sign-in setting is written.
