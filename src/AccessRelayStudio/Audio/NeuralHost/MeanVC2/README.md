# MeanVC2 local voice conversion host

This optional host runs the June 2026 MeanVC2 streaming zero-shot voice converter in audio.cpp. It uses the upstream 120 ms synthesis and 40 ms lookahead quality checkpoint, quantized to Q4_K. It retains streaming state between requests. The imported voice profile supplies up to the first ten seconds of target speech. Conversion runs locally without a network service or temporary audio files.

The host tries Vulkan and falls back to CPU if Vulkan setup fails. With the RTX 2070 on the development PC, 160 ms input blocks took 35–37 ms median inference plus pipe I/O, with individual blocks up to 69 ms. Model startup took 39 seconds cold and 1.3 seconds after warming the graphics cache. These are host timings before microphone capture, frame accumulation, output buffering, and the receiving app. The CPU comparison processed the same blocks in 104–110 ms median.

## Sources and licensing

- The [MeanVC2 source and core model](https://github.com/ASLP-lab/MeanVC2/tree/13acf84c1bf135ea5edad9c245b345289b06b33e) by ASLP@NPU are Apache 2.0. [MeanVC2 paper](https://arxiv.org/abs/2606.09050)
- The WavLM-Large and ECAPA-TDNN speaker-verification weights were published by Microsoft UniSpeech. The [MeanVC2 downloader](https://github.com/ASLP-lab/MeanVC2/blob/13acf84c1bf135ea5edad9c245b345289b06b33e/initialization.py) uses Microsoft's exact [fine-tuned WavLM-Large checkpoint](https://github.com/microsoft/UniSpeech/tree/main/downstreams/speaker_verification). The `speaker_wavlm` and `speaker_ecapa` sections of the bundled GGUF are format-converted and quantized adaptations of those weights. Those sections are provided under the accompanying CC BY-SA 3.0 license. The other components retain their own licenses. [Microsoft license](https://github.com/microsoft/UniSpeech/blob/main/LICENSE)
- The setup command downloads the [audio.cpp Q4_K conversion](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/a27679cead37af35763d022bf632cd024ffaf482/MeanVC2-GGUF) byte-for-byte. SHA-256 `29E0B2F519C96242A72632A471EB18F3F3A0F82D699274A40A2974FB196538AA`. The [audio.cpp package specification](https://github.com/0xShug0/audio.cpp/blob/v0.9.4/model_specs/meanvc2.json) identifies its `vc`, `vocos`, `asr`, `speaker_wavlm` and `speaker_ecapa` tensor sections.
- The [audio.cpp 0.9.4 Windows x64 Vulkan library](https://github.com/0xShug0/audio.cpp/releases/tag/v0.9.4) by ShugoAI LLC is Apache 2.0. The installed `audiocpp.dll` SHA-256 is `0789DCAAF524070A25D5266E1326E60AA6BBFCB58DFAFDD8DE2C99171BCB30FD`. [Microsoft Visual C++ runtime prerequisite](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)

## Build and protocol

In an x64 Visual Studio developer command prompt, set `AUDIOCPP_SDK` to the extracted audio.cpp C API library archive and run this command from the host source directory.

```bat
cl /nologo /EHsc /std:c++20 /O2 /MT /DNOMINMAX /DAUDIOCPP_USE_DLL /I"%AUDIOCPP_SDK%\include" host.cpp /FePlaySignal.MeanVC2Host.exe /link "%AUDIOCPP_SDK%\libs\audiocpp.lib"
```

Run the executable with `--reference <path to mono PCM16 WAV>`. It writes `READY` followed by a newline after warmup. Send requests of 7,680 little-endian float32 samples at 48 kHz. Each response is a little-endian int32 sample count followed by that many float32 samples at 48 kHz. Output counts can vary, including zero while conversion buffers. Close stdin to stop.
