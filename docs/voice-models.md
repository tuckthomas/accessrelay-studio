# Optional voice models

AccessRelay Studio includes built-in voice effects. Add reference-voice conversion and dialogue separation to a published Windows x64 build with the setup command below. It downloads pinned upstream assets, verifies SHA-256, and puts each selected model in the folder Studio reads at startup. Model weights and compiled runtimes are kept outside Git.

## Install a model

Close AccessRelay Studio and use PowerShell 7. Replace `./artifacts/Run` if you published to a different folder containing `AccessRelayStudio.exe`.

```powershell
./scripts/Install-VoiceModels.ps1 -PublishDirectory ./artifacts/Run -Model MRX
```

MRX enables **Remove background music** in **Create voice profile**. The published app gets ONNX Runtime 1.31.1 from NuGet, including its MIT license and third-party notices. [ONNX Runtime package](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.31.1)

For **MeanVC2** and **MioCodec V2**, run the setup command in an **x64 MSVC developer shell** with Visual Studio 2022 Build Tools or later and the Desktop development with C++ workload. Install Microsoft's x64 Visual C++ Redistributable version 14.44 or later on the machine that runs the app. The setup command builds each host from the C++ in this repository and places the verified `audiocpp.dll` next to it. [Visual Studio Build Tools](https://learn.microsoft.com/en-us/cpp/build/building-on-the-command-line) · [Microsoft x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)

```powershell
./scripts/Install-VoiceModels.ps1 -PublishDirectory ./artifacts/Run -Model MeanVC2
```

MeanVC2 tries Vulkan and can fall back to CPU. MioCodec V2 requires a Vulkan-capable GPU with a Vulkan driver. Building its GGUF also needs Python with PyTorch and safetensors. The following environment uses the versions verified for the checkpoint conversion. [PyTorch installation](https://pytorch.org/get-started/locally/)

```powershell
$voiceTools = Join-Path $env:LOCALAPPDATA 'AccessRelayStudio/VoiceTools'
python -m venv $voiceTools
$voicePython = Join-Path $voiceTools 'Scripts/python.exe'
& $voicePython -m pip install --index-url https://download.pytorch.org/whl/cpu torch==2.10.0
& $voicePython -m pip install safetensors==0.8.0
./scripts/Install-VoiceModels.ps1 -PublishDirectory ./artifacts/Run -Model MioCodec -Python $voicePython
```

You can select multiple models with `-Model MRX,MeanVC2,MioCodec`. Verified downloads are reused from `%LOCALAPPDATA%\AccessRelayStudio\AssetCache\voice` or a directory passed with `-CacheDirectory`. If the command finds a file with the wrong hash, replace that cache entry with the pinned upstream file. Start Studio after setup to refresh the model choices.

## Files and licenses

| Model | Files installed under the publish folder | Terms |
| --- | --- | --- |
| MRX | `DialogueSeparation/mrx.int8.onnx` | MIT, MERL checkpoint and audiojs ONNX export. [Checkpoint license](https://github.com/merlresearch/cocktail-fork-separation/blob/19b3de827ebc4bfb014570cf92dd32b4ee3b6921/.reuse/dep5) · [ONNX export](https://huggingface.co/audiojs/mrx/tree/dedc823a0bea2ed085d19e8b7f32589d35169ee8) |
| MeanVC2 | `NeuralVoice/MeanVC2/meanvc2-120ms-40ms-q4_k.gguf`, `audiocpp.dll`, `PlaySignal.MeanVC2Host.exe` | ASLP's core, vocoder and ASR checkpoints are Apache 2.0. The converted UniSpeech speaker tensors are CC BY-SA 3.0. The combined GGUF carries component-specific terms. [ASLP checkpoints](https://huggingface.co/ASLP-lab/MeanVC2/tree/39cdd19522fe896c227da691314d9a0e3b995486) · [UniSpeech license](https://github.com/microsoft/UniSpeech/blob/main/LICENSE) · [GGUF source](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/a27679cead37af35763d022bf632cd024ffaf482/MeanVC2-GGUF) |
| MioCodec V2 | `NeuralVoice/MioCodec/miocodec-25hz-44khz-v2-q8_0.gguf`, `audiocpp.dll`, `PlaySignal.MioCodecHost.exe` | MIT-licensed Aratako MioCodec and the official TorchAudio redistribution of MIT-licensed Microsoft WavLM Base+. Setup combines the two checkpoints and quantizes them locally. [Aratako model](https://huggingface.co/Aratako/MioCodec-25Hz-44.1kHz-v2/tree/67faba34153fe74e6665991c432a7327e23c5c1c) · [WavLM license](https://docs.pytorch.org/audio/2.0.0/generated/torchaudio.pipelines.WAVLM_BASE_PLUS.html) |

The two reference models use audio.cpp v0.9.4 under Apache 2.0 with its third-party notices. The setup command copies the notices and the applicable model licenses alongside each model. The Microsoft Visual C++ Redistributable has separate Microsoft terms. [audio.cpp release](https://github.com/0xShug0/audio.cpp/releases/tag/v0.9.4) · [Microsoft redistribution terms](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)

## SHA-256 pins

| Asset | SHA-256 |
| --- | --- |
| [MRX ONNX export](https://huggingface.co/audiojs/mrx/resolve/dedc823a0bea2ed085d19e8b7f32589d35169ee8/mrx.int8.onnx) | `d876c92d224f5d8cd2207d528b23369da895a6de71fb24132416fcb16f9f8424` |
| [MeanVC2 Q4_K GGUF](https://huggingface.co/audio-cpp/audio.cpp-gguf/resolve/a27679cead37af35763d022bf632cd024ffaf482/MeanVC2-GGUF/meanvc2-120ms-40ms-q4_k.gguf) | `29e0b2f519c96242a72632a471eb18f3f3a0f82d699274a40a2974fb196538aa` |
| [MioCodec V2 safetensors](https://huggingface.co/Aratako/MioCodec-25Hz-44.1kHz-v2/resolve/67faba34153fe74e6665991c432a7327e23c5c1c/model.safetensors) | `8e319ef2231bad184f17cb73fd5a21b685c25c6c1622ef33ed9271187e81cd4a` |
| [MioCodec V2 configuration](https://huggingface.co/Aratako/MioCodec-25Hz-44.1kHz-v2/resolve/67faba34153fe74e6665991c432a7327e23c5c1c/config.yaml) | `bfabffffaaa5709b8dc69585111ee3d53c1b0609c23d293cd1b4903eafa5bec1` |
| [TorchAudio WavLM Base+ checkpoint](https://download.pytorch.org/torchaudio/models/wavlm_base_plus.pth) | `136a3e720c04f2c77bf7a4dc6a3868b14d5a2c145a988114b733cb1a8428be98` |
| [audio.cpp Windows x64 Vulkan C API portable SDK](https://github.com/0xShug0/audio.cpp/releases/download/v0.9.4/audio-v0.9.4-lib-windows-x64-vulkan-portable.zip) | `04536d037f665527c2bb9b2e5fbaeeac53cafba82024cda7a427c49dae000a78` |
| [audio.cpp Windows x64 Vulkan portable converter](https://github.com/0xShug0/audio.cpp/releases/download/v0.9.4/audio-v0.9.4-bin-windows-x64-vulkan-portable.zip) | `295ca309160e59ec3ce3cff0e07edb6709e28d43b4c0a5ad76760938ae6a4e5c` |
| `audiocpp.dll` extracted from the pinned SDK | `0789dcaaf524070a25d5266e1326e60aa6bbfcb58dfafdd8de2c99171bcb30fd` |

The MioCodec converter embeds input path labels and the configuration directory name in the generated GGUF. Setup verifies the input hashes and prints the generated file's SHA-256. Whole-file hashes can vary with metadata even when all 597 tensor payloads match.
