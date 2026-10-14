# Architecture

## Responsibilities

- `DesktopRug.Core`: framework-independent geometry, display coordinate mapping, validated settings, atomic persistence, smooth icon bump field, deterministic Verlet cloth.
- `Rendering/PatternFactory`: generates three original textures and optionally reads a user-supplied image. No commercial artwork, pre-rendered animation or network assets.
- `Rendering/RugRenderer`: projects the cloth into an orthographic screen plane; draws textured, depth-sorted triangles with normal-based vertex lighting, layered soft shadow strokes, edge thickness and fringe.
- `Windows/RugOverlayWindow`: transparent WPF + Skia visual surface, always non-activating and click-through.
- `Windows/RugInputWindow`: a distinct, non-activating native layered window with a 1/255-opacity background. Its native region is the union of the cloth's projected triangles, excluding shadows and empty space. It owns mouse gestures.
- `Services/DesktopWindowService`: discovers Explorer's icon host and manages ordinary z-order immediately above it.
- `Services/DesktopIconService`: read-only UI Automation on a worker thread. It searches only the discovered desktop ListView's children.
- `Services/DisplayTopologyService`: enumerates monitor bounds, work areas and DPI.
- `Services/RugController`: visibility, tray commands, geometry lifecycle, settings, display/session recovery and timer scheduling.
- `Services/AudioService`: precomputed, quiet synthetic noise envelopes; separate from physics and disabled by default.
- `Services/Diagnostics`: off-screen smoke testing, render inspection and bounded profiling. It does not substitute simulated icons for the app's real shell source.

The app uses concrete small services rather than a DI container and one-interface-per-class ceremony. The core remains independently testable without WPF or an interactive Windows session.

## Simulation

Balanced mode uses 36 × 26 particles. Economy uses 24 × 18 and High uses 44 × 32. Each particle stores its current, previous and interpolated rendering positions. The fixed step is 1/120 second with an eight-step accumulator cap after stalls. Structural, diagonal shear and two-hop bending constraints run in alternating sweep directions. Gravity, damping, floor friction, bounded grab target motion and particle velocities prevent large pointer impulses from exploding the mesh.

A grab radius weights neighboring particles. Middle drags also translate the fabric gently; corner grabs lift it and leave the resting fabric behind. A preallocated spatial hash provides coarse non-neighbor particle self-contact. It is not continuous triangle collision detection: tightly crossed folds can still intersect.

Icon rectangles are transformed from physical screen pixels to monitor-local DIPs. Smooth radial influences add together with a bounded saturation curve. Floor heights are sampled once per step and reused across solver iterations. Moving icons wake the simulation when their polled rectangles change. No icon positions are written.

After 90 quiet steps the cloth sleeps and its rendering timer stops. Windows lock, suspend, hiding, Peek and a foreground application also stop animation. Maintenance only handles shell/visibility checks and bounded icon refresh. A 16.7 ms timer is a scheduling target, not a measured throughput claim; Economy uses 33.3 ms.

Arrays, geometry buffers and paint objects are reused. Skia's copied vertex object and immutable path snapshots are disposed each frame. The profiler records their small bounded managed allocation; this is not claimed to be allocation-free. Avoid full-screen Gaussian blur intermediates: layered shadow strokes cut CPU rendering cost substantially in local measurements.

## Storage and lifecycle

Settings use normalized monitor-local center coordinates, named size, rotation and the last monitor identity. Missing monitors fall back to primary; geometry is clamped so a useful part remains visible. Shape deformation is intentionally not persisted. Appearance changes flatten the mesh. JSON writes are serialized, flushed and replaced atomically; unreadable JSON receives a recovery copy. A coalescing save loop retains the newest settings during overlapping writes.

A per-session mutex enforces a single instance and a named event signals an existing instance. No entry window is shown automatically. The tray is the lifetime owner. Shutdown unregisters hotkeys, releases mouse capture, removes WinEvent hooks, disposes native handles and saves settings. Diagnostics are a separate command path with their own output folder and no normal startup/login writes.
