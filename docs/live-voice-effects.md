# Live voice effects

Voice adjustments update the running headphone preview or virtual microphone as
the controls move. No Apply action or audio restart is needed. The interface sends
the latest edit every 40 ms at most, with one request in flight; intermediate
positions are coalesced and the final position is retained. Errors appear beside
the controls and another edit retries. Opening Voice never starts capture.

Save voice settings stores the chain in an effects voice or attaches it to the
selected AI reference voice. Existing reference clips and old effects profiles
remain compatible. Live adjustment changes the current session; saving a named
voice remains explicit. Presets, bypass and reset also update live.

## Controls

| Area | Implemented controls |
| --- | --- |
| Voice character | Pitch ±12 semitones; independent timbre/envelope shift; timbre strength; wet/dry mix; output gain; bypass |
| EQ | Ten bands each before pitch/timbre and after effects, ±20 dB, with separate resets |
| Cleanup | Low cut; 50/60 Hz hum and second-harmonic filtering; noise gate; compressor threshold/ratio; broadband de-esser; adaptive spectral noise reduction |
| Character effects | Robot/ring modulation, harmony interval/mix, detune, growl, nasal, breathy, monotone, expressive pitch variation, delayed animal-style modulation |
| Modulation | Chorus rate/mix, phaser, vibrato, tremolo rate/depth, gapper rate/depth, underwater filtering |
| Distortion and space | Drive/distortion, diode and octave-up mode, bit depth, sample-rate reduction, echo delay/feedback/mix, reverb size/mix |
| Starting voices | Natural, Warm, Bright, Deep, Radio, Giant, Small character, Robot, Alien, Telephone, Broadcast, Cavern, Underwater |

The same effects are available after AI reference conversion. Pre EQ means before
the DSP pitch/timbre stage, not before the AI model. Pitch/timbre, harmony and pitch
variation use buffered spectral processing and add their processing delay. Noise
reduction estimates stationary noise; it is not a trained speech-isolation model.
Monotone and expressive pitch are mutually exclusive and depend on voiced pitch
tracking. These are independent DSP implementations, with no claim of matching a
competitor's proprietary algorithms or perceptual quality. MorphVOX's calibration
wizard, soundboard/background playback, global hotkeys, skins and proprietary
downloadable packs are separate capabilities outside this voice-transformation
update. No third-party voice assets or model weights were added.

## Comparison sources

- [MorphVOX pitch, timbre and pre/post EQ](https://screamingbee.com/docs/morphvoxpro5/tweakpanel)
- [MorphVOX voice effects](https://screamingbee.com/docs/morphvoxpro5/voiceeffectspanel)
- [EasyEffects](https://github.com/wwmm/easyeffects) for cleanup, dynamics, EQ and live effect controls.
- [Lyrebird](https://github.com/lyrebird-voice-changer/lyrebird) for live preset/pitch workflows; this project is archived.

## Focused verification

The voice page checks cover immediate changes, no Apply button, coalescing, final
edit delivery, errors/retry, reference-voice effects, selection ordering and
existing clip editing. The offline native checks use the actual VoiceSignal and
processor with generated audio: neutral/bypass/mute behavior, each effect's output,
finite sample bounds, pitch versus timbre, mains attenuation, noise attenuation,
intonation tracking, timed echo repetitions, diode octave-up, old settings and
saved profile roundtrips. They do not activate a microphone or claim listening
acceptance on the user's devices.

```powershell
pnpm --dir src/AccessRelayStudio/DesktopUi exec vitest run src/pages/VoicePage.test.tsx
dotnet run --project src/AccessRelayStudio/WindowsVerification/WindowsVerification.csproj -c Release --artifacts-path artifacts/VoiceEffectsChecks -- --voice-effects-only
```
