# Desktop architecture

React renders the application screens. WinUI supplies the native window, WebView2,
title bar, loading/recovery controls and emergency stop. The app starts DesktopWindow;
the legacy MainWindow and its native application panels/editors have been removed.
The existing 3D controller viewer is still bundled and used by the React controller page.

The compiler enforces this dependency direction:

```text
AccessRelayStudio (WinUI shell and desktop presentation adapters)
    -> AccessRelayStudio.Runtime (Windows device/audio/vision workers)
        -> AccessRelayStudio.Engine (portable Core logic)
```

`AccessRelayStudio.Engine` targets plain `net10.0`. It owns controller/macro
composition, profiles, Lua, cancellation, recognition queues and shared contracts.
Its only non-BCL dependency is MoonSharp. It has no Windows or presentation reference.

`AccessRelayStudio.Runtime` targets Windows and owns StudioRuntime, timers, physical
input, virtual output, WASAPI/speech, capture and ONNX inference. It references the
engine and the managed ONNX/NAudio/System.Speech packages, with no WinUI or desktop
presentation dependency. The existing built-in NBA vision adapter is linked into
this runtime; portable Core remains game independent. This cleanup does not change
the game-pack trust boundary or move timing-critical work into JavaScript.

The WinUI executable explicitly compiles only App, DesktopWindow, BuildInfo, Desktop
presentation adapters and the workspace performance monitor. Desktop services own
native file pickers and translate the versioned WebView2 command/status protocol.
The host references the engine/runtime assemblies and deploys the Windows native
ONNX package, app assets and virtual-device/neural helpers as before. Paths continue
to resolve against AppContext.BaseDirectory, independent of the worker assembly.

Source files remain at their established Core/Audio/Input/Output/Vision paths and
are linked by their owning library projects, preserving helper/build/training paths.
The host's explicit Compile list prevents those files from also becoming host types.
Internal access is granted only to the host, runtime and named verification/tools
that require it; implementation types are not widened into a public plugin API.

Portable engine verification and the pack author tool reference the real engine.
Windows verification uses the real compiled runtime and contracts with explicitly
named fake devices, rather than recompiling workers against duplicate contracts.
Portable CPU-only vision/evidence tools link the relevant platform-independent
sources and reference the engine, allowing those checks to run without WinUI or
Windows hardware.

Windows CI inspects the published assemblies with ProjectBoundaryVerification:
engine references are limited to the BCL/Lua interpreter; runtime references cannot
include WinUI/desktop presentation; and the host cannot contain duplicate engine,
worker or legacy MainWindow types. This uses metadata and never executes native
code. Publishing and simulated-device checks remain separate from physical-device
and real WebView2 acceptance in the accessibility roadmap.

CI also checks that published native ONNX providers, managed audio/speech libraries,
React assets, the 3D viewer and built-in packs exist. Optional virtual-device helpers
are hash-checked against their staged sources when present; CI does not manufacture
missing helper binaries or claim physical output acceptance.
