# Lua controller scripts

Open **Lua Scripts**, choose **New script**, enter a name and Lua source, then choose **Check syntax**. Pick a controller input under **Assign when saved** and choose **Save & apply**. Turn on **Macro scripts** on Dashboard and press that input. Existing Lua scripts also appear in the Bindings assignment picker. Lua sources travel with macro profile import/export; profiles containing Lua use format version 3. Older profiles remain readable.

```lua
-- Mirror the left stick's vertical movement to the right stick while held.
while input.down(input.trigger()) do
    output.axis("RightY", input.axis("LeftY"))
    wait(0)
end
```

```lua
-- Alternate two button sequences across successive presses.
local count = state.get("count") + 1
state.set("count", count)
if count % 2 == 1 then
    output.button("A", true)
else
    output.button("B", true)
end
wait(80)
output.clear()
```

| API | Behavior |
| --- | --- |
| `input.down("A")` | Read the current physical button, trigger, or paddle, before consumption or macro overrides. Trigger buttons use the existing threshold. |
| `input.axis("RightY")` | Read a physical axis as percent. Stick axes: −100 to +100; triggers: 0 to 100. |
| `input.trigger()` | Return the name of the control that started this run. |
| `output.button("A", true)` | Hold a virtual controller button pressed. `false` holds it released. |
| `output.axis("RightY", 50)` | Override one axis until released or the script ends. |
| `output.release("RightY")` | Relinquish one axis or button to normal input. |
| `output.clear()` | Relinquish every overridden channel. |
| `wait(80)` | Yield until at least 80 ms later. Values are 0–10000 ms; zero resumes on a later input sample. |
| `elapsed()` | Milliseconds since the current trigger. |
| `state.get("count")` | Read a persistent numeric value, defaulting to zero. |
| `state.set("count", 1)` | Save a finite number. Up to 64 keys, each 1–64 characters. |

Axis names are `LeftX`, `LeftY`, `RightX`, `RightY`, `LeftTrigger`, and `RightTrigger`. Input names follow `ControlId`, including `A`, `B`, `X`, `Y`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`, `Menu`, `View`, `LeftStick`, `RightStick`, `LeftBumper`, `RightBumper`, and the four paddles `SmallLeft`, `SmallRight`, `LargeLeft`, `LargeRight`. Names are case insensitive. Paddles are input-only. Use the axis API to write trigger values.

## Execution and limits

MoonSharp 2.0.0 provides Lua 5.2-style syntax, including variables, functions, tables, arithmetic, conditions, and loops. This host exposes `math`, `bit32`, `pairs`, `ipairs`, and `next`. Other standard library modules are omitted. There is no file, process, network, CLR, dynamic loading, debugger, or user-created coroutine API.

Scripts compile when checked or applied, outside the sampling loop. Each press starts the compiled top-level chunk; top-level locals start fresh. `state` survives successive triggers until apply, script toggle, stop, or disconnect resets the engine. Avoid replacing the host's API globals.

The current engine runs one sequence at a time. A different bound control takes over using the existing trigger priority. A completed script, runtime error, stop, disconnect, or profile apply releases its overrides. Controls the script does not override continue following manual input. An output followed immediately by script completion is released on that same sample: use `wait()` to make a press observable.

A resume may execute at most 4000 interpreter instructions before it must yield through `wait()`. An unyielding loop stops with an error. Each run also stops after 60 seconds. A profile holds up to 32 scripts, each at most 32768 source characters. These limits protect the input loop from common mistakes; this in-process interpreter does **not** impose a hard memory quota or isolate hostile code. Run trusted scripts.

## Timing and performance

The Windows input loop still targets 4 ms (250 Hz). `wait(1)` means resume on the first available sample at or after its deadline; it cannot create a 1 ms sampling schedule. Delays use the worker's monotonic clock. Neither this interval nor a script CPU benchmark measures end-to-end input latency.

The verification program checks timed outputs, manual input preservation, live reads, state, script cancellation/takeover, instruction budgets, errors, and profile persistence. It also prints a warmed CPU-only benchmark of 10000 Lua samples. The benchmark excludes input hardware, scheduling, drivers, and output transport; its result varies by machine and script.

The C# host and Rust virtual-device helper remain in place. A native engine rewrite should follow profiling of polling, script execution, allocation/GC, and report delivery. Native code can reduce allocation pressure and improve timing control, but changing the language does not change the polling period or remove Windows and USB scheduling delays.
