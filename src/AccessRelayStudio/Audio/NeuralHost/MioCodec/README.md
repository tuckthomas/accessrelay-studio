# MioCodec V2 voice conversion

The host runs MioCodec 25 Hz 44.1 kHz V2 in Q8_0 format on Vulkan using the audio.cpp v0.9.4 C API. It receives `--reference <PCM16 WAV path>`, reads 7,680 mono Float32 samples at 48 kHz per stdin packet, and returns a 32-bit sample count followed by the same number of Float32 samples. Inference runs inside the native host. The reference uses up to its first three seconds. The host uses the prior 160 ms input packet as context and smooths the joins between returned blocks.

## Provenance

- MioCodec V2 weights and configuration are from Aratako revision `67faba34153fe74e6665991c432a7327e23c5c1c` under MIT. The original safetensors SHA-256 is `8e319ef2231bad184f17cb73fd5a21b685c25c6c1622ef33ed9271187e81cd4a`. [Model card and pinned files](https://huggingface.co/Aratako/MioCodec-25Hz-44.1kHz-v2/tree/67faba34153fe74e6665991c432a7327e23c5c1c)
- WavLM Base+ is from TorchAudio’s official MIT-licensed redistribution of Microsoft’s original pretrained checkpoint. SHA-256 `136a3e720c04f2c77bf7a4dc6a3868b14d5a2c145a988114b733cb1a8428be98`. The conversion script renames tensors and separates packed projections into Q, K, and V. The resulting tensors and the Aratako weights are quantized locally by `audiocpp_gguf`. [TorchAudio checkpoint license](https://docs.pytorch.org/audio/2.0.0/generated/torchaudio.pipelines.WAVLM_BASE_PLUS.html) · [Checkpoint file](https://download.pytorch.org/torchaudio/models/wavlm_base_plus.pth)
- The audio.cpp library and license notices are from the official Windows x64 Vulkan library release. [audio.cpp v0.9.4](https://github.com/0xShug0/audio.cpp/releases/tag/v0.9.4) · [Microsoft Visual C++ runtime prerequisite](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)

## Reproduce

Run `python convert_wavlm.py wavlm_base_plus.pth wavlm-torchaudio.safetensors` with PyTorch and safetensors. Place the pinned upstream `config.yaml` in an otherwise empty `mio-config` directory. From the audio.cpp Windows Vulkan distribution, use its GGUF converter and MioCodec model specification.

```powershell
audiocpp_gguf.exe --input weights=model.safetensors --input wavlm_weights=wavlm-torchaudio.safetensors --root mio-config --model-spec model_specs/miocodec.json --family miocodec --type q8_0 --output miocodec-25hz-44khz-v2-q8_0.gguf
```

Compile the host from an x64 MSVC Developer Command Prompt with the same release's C API import library and header.

```powershell
cl /std:c++20 /EHsc /O2 /MT /DNOMINMAX /I include /FePlaySignal.MioCodecHost.exe host.cpp libs/audiocpp.lib
```
