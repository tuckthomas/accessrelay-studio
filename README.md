# AccessRelay Studio

AccessRelay Studio is an installable Windows accessibility app for adaptive controller input, gameplay capture and local voice processing. Its interface uses bundled React/TypeScript/CSS inside WebView2. Controller polling, release scheduling, capture, inference and audio processing stay in the C# engine. The installed app needs no web server, Node.js, pnpm or cloud hosting.

The [desktop architecture](docs/desktop-architecture.md) separates the portable engine,
Windows workers and React/WinUI presentation into projects with enforced dependency
boundaries. WinUI supplies the small native shell; React owns the application screens.

The interface is organized around **Workspace**, **Controller**, **Remote Play**, **Voice** and **Settings**. A shared visual system uses muted sage and ivory surfaces, navy text and mint accents derived from the application icon.

- **Adaptive input** supports Windows gamepads over USB or Bluetooth, live control feedback, configurable bindings, and a recorder that can include or ignore individual controls and axes. Optional [sticky buttons and tremor smoothing](docs/controller-accessibility.md) are saved with each controller setup.
- **Lua scripts** add programmable controller logic with live input reads, timed output, persistent numeric state, a source editor, and syntax checks. [Controller API and examples](docs/lua-scripting.md)
- **Game packs** supply reviewed Lua mappings and declarative tuning in separate controller setups. NBA source/training lives under `games/nba2k27/`; a game-neutral switch example demonstrates the [versioned pack format](docs/game-packs.md). No marketplace or third-party weights are bundled.
- **NBA 2K27 shot profiles** save player height, three-point rating, base and upper releases, meter-specific release type, four shot-speed levels, a linked 100% release blend, and camera view (**2K**, **2K Low**, **2K Legacy**). Manage these in **Remote Play** alongside capture. Camera changes invalidate the previous model/calibration association.
- **Remote Play Vision (experimental)** captures a selected window, records synchronized player/court/meter/feedback evidence and actual controller trajectories, and integrates selectable CPU or CUDA temporal inference with cancellable release deadlines. Local timing statistics and saved run comparisons assess pipeline performance. No trained gameplay model is bundled. Explicit reviewed evidence conversion is available; automatic label extraction and measured meter-off gameplay validation remain pending. [Model and training workflow](docs/vision-model-workflow.md) · [Implementation plan](docs/remote-play-vision-implementation-plan.md)
- **Voice access** offers pitch and tone effects, headphone preview, waveform trimming, and optional local reference-voice models and dialogue isolation. [Local voice commands](docs/voice-commands.md) add opt-in Windows phrase recognition and personalized sound templates for bounded controller presses or existing sequences.
- **Workspace** gives a concise view of active processing and entry points to each task. Controller setups and voice profiles are selected within their own workspaces; **Stop everything** remains available throughout the app.
- **Assistant access (MCP)** is opt-in under Settings. Compatible clients can inspect redacted status, propose reviewed controller changes, rehearse the actual Lua engine and run bounded local dataset/experiment jobs. [Setup, permissions and limits](docs/mcp-assistant.md)

## Build on Windows

Use Windows 11 x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Node.js 24 or newer supported by Vite, and pnpm 11. From the repository root in PowerShell, build the local interface, publish the app and open its complete output folder. These frontend development tools are not installed as application runtime dependencies.

```powershell
./scripts/Build-DesktopUi.ps1
dotnet publish .\src\AccessRelayStudio\AccessRelayStudio.csproj -c Release -o .\artifacts\Run
.\artifacts\Run\AccessRelayStudio.exe
```

For an installed beta with an explicit version label and optional desktop shortcut, use [Publish-Beta.ps1](scripts/Publish-Beta.ps1). It preserves installed voice assets and their links, can retain the old Run folder as a stable backup, and can point the usual Run path at the new installation. An existing Run junction is retained as a `Run-Previous-*` rollback link when switching to a versioned installation; the old target and any process using it are untouched. Updating files in a running installation is refused. `-DeferRunActivation` installs and creates the shortcut while leaving the old path untouched; a later `-ActivateOnly` switches the checked path without rebuilding. Optional NVIDIA DLLs can be placed in the installation's `CudaRuntime` folder for process-local lookup; CPU inference needs no CUDA initialization. Model weights and runtime dependency binaries remain outside Git.

The source build includes the controller monitor, binding editor, macro recorder, built-in voice effects, and headphone preview. To add virtual Xbox output or a virtual microphone, follow the [virtual device setup](docs/virtual-devices.md) before publishing. The signed USB/IP transport requires Windows administrator approval for its initial driver installation. The app and its helpers run with standard permissions after setup.

Install [optional voice models](docs/voice-models.md) into the publish folder when you want reference-voice conversion or background music removal. Model files are downloaded or built locally on request, and inference runs on the PC.

## Use the app

1. In **Controller**, connect a gamepad and choose a setup. Map controls, edit built-in timings, record a sequence or write Lua in the same workspace. Output and sequences start only when you enable them.
2. In **Voice**, choose a microphone, voice and destination. Audition through headphones or start the virtual microphone. Create reference voices from local audio/video clips with visual trimming and optional speech cleanup.
3. In **Remote Play**, choose a player/shot profile and source window, then use live capture, visual region setup and clip recording/review together. Models and detailed timing/calibration remain optional advanced tools.
4. With virtual microphone output running, select **AccessRelay Virtual Microphone** in the receiving app. Use **Settings** for the one-time virtual device driver installation and local profile folders.

Run the controller logic checks with `dotnet run --project .\src\AccessRelayStudio\Verification\Verification.csproj -c Release`.

The desktop host uses a local command bridge and bounded status updates. Preview images use a separate intercepted local resource origin; no listening HTTP server is created. Opening or switching workspaces never starts capture, microphone processing, scripts or virtual output. Hiding preview presentation leaves explicitly started backend work running.

The source picker refreshes while Remote Play is visible and capture is stopped. It lists application windows; a browser window shows its active tab rather than each background tab. Renamed windows retain their selection, while closed sources are removed without choosing an unrelated window.

**Live viewing does not save a recording.** Use the prominent **Record training clip…** action to save a bounded manual gameplay take; its recording/saved status, frame count and folder are shown separately. Recording does not require an AI model. Choose imported models by name from the model dropdown; no trained NBA 2K27 model is bundled.

The vision implementation uses the Windows GPU ONNX Runtime package, which also supplies CPU inference. Choose **CPU** or **CUDA** explicitly for vision; CUDA is the default and fails visibly if unavailable. CPU vision, capture-only observation and the existing CPU audio path do not initialize CUDA. CUDA vision requires compatible CUDA 13/cuDNN 9 dependencies for the pinned ONNX Runtime build. The scoped [experimental launcher](scripts/Start-VisionExperiment.ps1) changes dependency lookup only for its child process. Backend changes require matching validation/calibration context before automatic release can arm. Python is used by offline training/evaluation tools, not by the production vision loop.

## License

AccessRelay Studio’s original source is available under the [BSD Zero Clause License](LICENSE), SPDX `0BSD`. It allows use, modification, and redistribution without an attribution requirement. Dependencies, controller model assets, and optional weights retain their own licenses. See the [third-party notices](THIRD-PARTY-NOTICES.md).
