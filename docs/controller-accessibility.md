# Controller accessibility

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
