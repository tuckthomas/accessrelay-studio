# AccessRelay Studio

AccessRelay Studio is a Windows accessibility app for adaptive controller input and local voice processing. It helps people record and replay controller sequences, tune controls for their own movement, and use a locally processed microphone signal in other Windows apps.

- **Adaptive input** supports Windows gamepads over USB or Bluetooth, live control feedback, configurable bindings, and a recorder that can include or ignore individual controls and axes.
- **Lua scripts** add programmable controller logic with live input reads, timed output, persistent numeric state, a source editor, and syntax checks. [Controller API and examples](docs/lua-scripting.md)
- **Voice access** offers pitch and tone effects, headphone preview, waveform trimming, and optional local reference-voice models and dialogue isolation.
- **Dashboard** gives controller scripts and live voice separate switches and profile selectors. Live performance metrics include CPU, RAM, GPU, VRAM, controller polling, and virtual output handoff.

## Build on Windows

Use Windows 11 x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root in PowerShell, publish the app and open it from its complete output folder.

```powershell
dotnet publish .\src\AccessRelayStudio\AccessRelayStudio.csproj -c Release -o .\artifacts\Run
.\artifacts\Run\AccessRelayStudio.exe
```

The source build includes the controller monitor, binding editor, macro recorder, built-in voice effects, and headphone preview. To add virtual Xbox output or a virtual microphone, follow the [virtual device setup](docs/virtual-devices.md) before publishing. The signed USB/IP transport requires Windows administrator approval for its initial driver installation. The app and its helpers run with standard permissions after setup.

Install [optional voice models](docs/voice-models.md) into the publish folder when you want reference-voice conversion or background music removal. Model files are downloaded or built locally on request, and inference runs on the PC.

## Use the app

1. Connect a gamepad and check its response in **Device Monitor**. Choose **Bindings** or **Macro Recorder** to set up the actions you need.
2. In **Live Voice**, choose your microphone and test the selected voice effect with **Headphone preview**. If you’ve installed a reference model, use **Create voice profile** to trim and preview a local reference clip you have permission to use.
3. Use **Dashboard** to select each profile and start the feature independently. With virtual microphone output running, choose **AccessRelay Virtual Microphone** as the input in the receiving app.

Run the controller logic checks with `dotnet run --project .\src\AccessRelayStudio\Verification\Verification.csproj -c Release`.

## License

AccessRelay Studio’s original source is available under the [BSD Zero Clause License](LICENSE), SPDX `0BSD`. It allows use, modification, and redistribution without an attribution requirement. Dependencies, controller model assets, and optional weights retain their own licenses. See the [third-party notices](THIRD-PARTY-NOTICES.md).
