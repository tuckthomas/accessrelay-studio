# Third-party notices

The root [0BSD license](LICENSE) covers original AccessRelay Studio code. The components below retain their upstream terms.

| Component | License and provenance |
| --- | --- |
| Three.js and addons | MIT. [Viewer notice and build information](src/AccessRelayStudio/Viewer/THIRD-PARTY-NOTICES.md) |
| Standard and Elite controller meshes | CC0 1.0 source mesh with AccessRelay adaptations. [Model provenance](src/AccessRelayStudio/Model/README.md) |
| DualShock 4 and Switch Pro meshes | MIT. [Model provenance](src/AccessRelayStudio/Model/3DcoPlus.PROVENANCE.md) |
| NAudio | MIT. [NAudio notice](src/AccessRelayStudio/Assets/NAudio-LICENSE.md) |
| ONNX Runtime | MIT with accompanying dependency notices. Restored through NuGet. [ONNX Runtime license](https://github.com/microsoft/onnxruntime/blob/main/LICENSE) |
| Windows App SDK, Windows SDK build tools, and WebView2 | Microsoft software license terms supplied with their packages and installers. [Windows App SDK redistribution](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps) · [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) |
| USB/IP transport and build helpers | The signed `usbip-win2` package is BSD-2-Clause with BSD-3-Clause CLI11. The helpers use MIT `usbip-audio` and MIT-licensed Windows bindings. Xbox device descriptors and initial status packets include BSD-3-Clause material from ViGEmBus. [Driver provenance](src/AccessRelayStudio/Audio/VirtualMicDriver/Package/USBIP-WIN2.PROVENANCE.md) · [Microphone dependencies](src/AccessRelayStudio/Audio/VirtualMicHost/README.md) · [Xbox descriptor notice](src/AccessRelayStudio/Audio/VirtualMicHost/XBOX360.PROVENANCE.md) |
| Optional voice models and native inference runtime | Obtain these separately under their own terms. MeanVC2’s upstream conversion includes CC BY-SA 3.0 speaker encoder components and Apache-2.0 core components. MioCodec V2 uses MIT source weights. The optional MRX export uses MIT. [Exact sources and licenses](docs/voice-models.md) |

Xbox, DualShock, DualSense, and Switch Pro identify compatible devices or layouts. AccessRelay Studio is an independent project.
