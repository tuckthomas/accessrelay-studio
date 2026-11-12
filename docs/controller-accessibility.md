# Controller accessibility

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
