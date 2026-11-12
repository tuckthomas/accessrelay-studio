# Controller accessibility

## Nonvisual feedback

In **Controller → Accessibility → Nonvisual feedback**, enable audio cues,
controller pulses, or both. Both are off by default. Save preferences before using
**Test feedback**. These preferences apply to all setups and are stored separately
from exported controller profiles.

Profile changes use a low ascending tone pair and a 120 ms pulse, the first accepted
player lock in a physical shot uses a short tone/40 ms pulse, and an accepted release
output uses a higher tone pair/80 ms pulse. A cue reports a host event, not proof of
an in-game outcome or calibrated end-to-end latency. Volume and pulse intensity are
independent; rumble intensity is limited to 50%.

Sounds use the Windows default playback endpoint. Pulses use the selected physical
WinRT gamepad's vibration interface, never the virtual output. Unsupported or
disconnected devices are reported through status announcements. Some drivers expose
the interface without working motors, so test the physical response before relying
on it. The browser preview saves synthetic preferences but does not play native cues.

The input and release threads only publish to an eight-item queue. One background
worker performs device I/O, suppresses repeated observations, limits cue frequency,
and discards events older than 500 ms. Stop, context changes, lost tracking, disconnect
and configuration changes cancel active/queued feedback. Queue delay/error counters
are reported separately from controller output; a feedback device failure does not
stop input. Device timing and comfort still require real hardware validation.

## Switch navigation

Choose **Switch navigation** in the top bar. One-switch mode advances focus at the
configured interval; the select switch activates the outlined control. Two-switch
mode uses separate step and select inputs. Keyboard switches default to **Enter**
for step and **Space** for select. **F8** starts/stops and **Escape** stops scanning;
an open dialog remains available to dismiss with another Escape. Preferences are
remembered, but scanning never starts automatically when Studio opens.

Choose keyboard switches or the selected physical controller, including adaptive
paddles. Controller mode temporarily sends neutral gameplay output and pauses
sequences/vision. Release all controls before returning to gameplay. It cannot be
started during macro or gameplay training recording. Keyboard scanning can navigate
recording controls without routing a gamepad away from manual input. Scanning stops
when Studio loses focus, on Stop everything, or when its controller disconnects.

Scanning stays inside the active dialog/menu and skips unavailable controls. Select
a slider, number field or dropdown to open **Decrease / previous**, **Increase / next**,
and **Finish editing** actions. Text fields still need a text-entry device or the
Windows on-screen keyboard. **Stop scanning** is also part of the scanning ring.

Controller events use bounded ordered history with generation/sequence identifiers,
per-switch debounce, and fresh-release requirements after profile/device changes.
Repeated snapshots do not repeat selections. Lost or delayed history stops scanning
instead of selecting an item using uncertain input. Browser preview supports keyboard
switches; native physical-controller routing is tested separately on Windows.

## Sticky buttons

In **Controller → Accessibility**, select the buttons or triggers you want to toggle,
then choose **Save sticky controls**. Choices belong to the current controller setup;
all controls use their ordinary behavior by default. Saving does not connect output.

With controller output connected, release a selected control, then press it once to
hold its virtual output. Release and press again to turn that hold off. Selected
triggers toggle between fully pressed and released; they no longer pass analog
pressure through. Paddles retain their configured mappings.

Stop everything, output restart, disconnect/reconnect, changing the selected device,
or applying a controller setup clears sticky holds. Input already held during those
transitions must be released before it can start a new sticky hold.

Sticky input is manual intent composed before sequences/Lua. Those scripts can override
its output, but sticky state does not generate physical script-trigger presses. Vision
retains final output ownership and cannot arm when its shoot button is configured as
sticky. Macro and gameplay-training recordings use unmodified physical input; sticky
state is cleared on entry and does not resume when recording ends.

Setups containing sticky controls use profile format **4**. The app still reads legacy
formats 1–3 with sticky controls off. Older builds do not read format 4; retain a legacy
export if you need to return to one. Deselecting every sticky control and saving produces
the earlier format appropriate to the setup's other features.

Portable tests and Windows checks with simulated devices verify state transitions and
composition. Physical controller/trigger behavior and comfort still need device testing.

## Tremor smoothing

Use **Controller → Accessibility → Tremor smoothing** to set each stick's time constant
from 0 (off) to 100 ms. Save the choices with the controller setup. Larger values reduce
rapid changes but delay intentional movements: a sustained change reaches about 63% of
its target after one time constant and 95% after three. These are mathematical filter
response times, not a measured hardware latency guarantee.

The filter uses elapsed time rather than assuming a fixed polling rate. It runs on
manual input before sequences/Lua; script output remains unsmoothed. Returning to the
existing deadzone immediately neutralizes the stick. Stop/output changes, disconnect,
device/profile/deadzone changes, and recording transitions reset its history. A large
polling gap or clock rollback starts a fresh sample. Recording paths retain physical
input. Smoothing uses the same profile format 4 compatibility rules as sticky buttons.
# Co-pilot controllers

Controller → Accessibility → Co-pilot controllers combines two selected, verified
physical gamepads. Standard gamepad controls from adaptive hubs are supported through
the same Windows reader; arbitrary keyboard-only hubs are not controller inputs.
Merging is off at every launch, even when device identities and ownership preferences
were saved. The ordinary input selector is disabled while merging is enabled.

Buttons default to shared OR ownership. Either participant can keep a shared button
held after the other releases or disconnects. Individual buttons can instead belong
to primary or companion. Opposite D-pad directions cancel. Stick ownership selects a
whole vector; strongest compares squared magnitude and breaks ties toward primary.
Triggers can belong to either participant or use the larger value. Missing devices
contribute neutral values, without transferring their exclusive controls to the
other participant. Elite paddle capture, switch navigation and vibration stay with
primary. Each reader retains the existing physical-bus verification and virtual
Xbox identity exclusion while output is active.

Configuration pauses sequences, disarms vision and neutralizes output until merged
physical controls are neutral. Input topology changes clear synthetic sticky/filter/
macro state and disarm vision; the remaining participant's physical controls continue.
Sticky buttons and smoothing process the merged intent, then macros compose output,
then the host vision arbiter owns release suppression. Macro recordings capture the
merged physical state; connection changes cancel a take. Gameplay evidence additionally
records immutable contributor identities and ownership settings, and initial connection
state. Connection-version changes remain in the controller trace. Stop disconnects
output and requires a neutral merged sample before restarting co-pilot gameplay.

Portable verification covers merging, ownership, conflicts, disconnected contributors,
numeric limits and preferences that never auto-enable. Windows runtime checks use
simulated contributors. Real multi-device input, feedback-loop exclusion, adaptive
hardware behavior and native WebView interaction still require Windows acceptance.
