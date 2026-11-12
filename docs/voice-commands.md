# Local voice commands and personal sounds

Open Voice → Voice commands. Preferences belong to the active **controller setup**,
independently of reference-voice effects. Select a physical command microphone and
add commands. Each command can briefly press a button/trigger (20–500 ms), or trigger
the existing sequence/Lua mapping for a control. Enable sequences before listening
when using the latter. Manual physical edges win simultaneous sequence-trigger ties.
Button pulses compose after macros without releasing physical or macro holds.

Phrase recognition uses an explicitly selected **installed Windows desktop speech
engine** through Microsoft's MIT-licensed `System.Speech` 10.0.0 adapter. Windows
supplies and licenses its installed acoustic/language models. No model weights are
redistributed, downloaded by Studio, or sent to a cloud recognizer. Recognition
loads only the active command phrase grammar. Language availability depends on the
installed Windows desktop speech components; Windows voice-access languages and
desktop SAPI engines are not interchangeable. The UI lists the actual engines.

Personal sounds need no Windows speech engine. Save a command, select a microphone,
then record up to three examples. Recording waits up to ten seconds for a short
sound and pauses after 120 ms silence. Features use 20 ms spectral frames, 40 ms
pre-roll and bounded dynamic time warping. Matching is duration constrained and
rejects close ties between commands. A similarity score is not calibrated probability.
This is an experimental personalized acoustic matcher, not a clinically validated
phoneme/dysarthria recognizer. Quiet sounds may need a lower activation level.
Templates contain spectral features; raw training audio is not persisted.

Microphone capture is an independent shared-mode PCM16/16 kHz session **before**
conversion/effects. It can run alongside voice conversion. It does not use MRX:
that existing cleanup path processes imported clips, not live microphone audio.
There is no acoustic echo canceller. Use headphones and test game speech/music,
background noise, breathing, fatigue and intended clicks/hums before gameplay.
Increasing the activation threshold trades soft-sound detection for noise rejection.

Listening is always off at launch and requires connected physical input and virtual
output. It disarms vision and excludes macro/gameplay recording. The bounded host
arbiter receives command IDs, confidence/similarity and timestamps, never controller
driver writes from recognition. It checks activation generations, refractory periods,
queue capacity, recognition age (250 ms) and sound-end age (1 s). A recognition stall
or full 500 ms audio queue stops capture, clears intents and requires an explicit
restart. Warm-up audio is discarded. Only one recognizer may retire at a time; a
blocked native engine prevents another start while Stop can close the microphone.
Stop, input topology/selection changes, controller setup changes, navigation and
sequence-setting changes cancel commands and externally started sequences. A 50 ms
watchdog closes capture after controller context changes even if the endpoint emits
no further frames. Recording a personal sound emits no gameplay actions.

The UI reports onset-to-host and sound-end-to-host composition times from QPC, with
20 ms framing and pre-roll uncertainty. These do not measure driver/console delivery.
The initial phrase backend recognizes complete utterances after silence detection;
it is not an ONNX streaming keyword spotter and makes no 30/100 ms latency promise.
A different streaming model can implement the same bounded worker interface after
accuracy, license, memory and actual device latency measurements justify it.

Portable checks use synthetic tones, fake stalled/failing recognizers and the real
host queue. Windows runtime checks simulate input/output and verify physical holds,
sequence execution, raw recording exclusion, Stop and profile cancellation. Real
microphone accuracy, installed engine operation, game-speech false activations,
dysarthric/personal sound performance, resource usage and output latency remain
acceptance checks. No real-user or acoustic accuracy result is claimed by those tests.
