# Lua controller scripts

Open **Controller → Scripts**. Choose **New** to write Lua, or **Record input** to capture buttons, triggers and/or individual stick axes. **Save as Lua script** opens the result in the same Lua editor. There is no separate sequence editor or built-in timing catalog. Under **Button mapping**, assign a saved script and use **Start controller** to enable output and scripts together.

Existing captures automatically become Lua scripts with their original names and exact captured values. Existing mappings keep their controls, enabled state and physical-button replacement settings. A previously mapped built-in action becomes an editable Lua script; unused built-in presets are removed. New profiles have no predefined actions. The original profile files are backed up before migration; active Lua-only profiles use format version 6, while older formats remain importable.

A recorded script contains its data directly in Lua:

```lua
-- Columns are raw RightX / RightY values, not percentages.
-- First column is milliseconds from the replay's start.
output.replay(140, "RightX,RightY", [[
0,0,-32768
80,0,32767
120,0,0
]])
```

Edit these values or add ordinary Lua logic before/after the replay. Only the named channels are overridden. Replays use absolute sample times and skip to the latest sample after a delayed poll, rather than accumulating delays. The last sample is held until the stated total duration, then its channels return to normal input. **Import** accepts `.lua` scripts and older recording `.json` files; **Export** writes a `.lua` file.

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
| `output.axis_raw("RightY", -32768)` | Exact raw stick override (−32768–32767); raw trigger values are 0–255. |
| `output.replay(140, "RightX,RightY", [[...]])` | Replay timestamped raw inputs on the named channels, including buttons or triggers. Up to 4096 samples / 10 seconds. |
| `wait_until(140)` | Yield until 140 ms from script start; overdue deadlines continue immediately. |
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

The current engine runs one script at a time. A different bound control takes over using the existing trigger priority. A completed script, runtime error, stop, disconnect, or profile apply releases its overrides. Controls the script does not override continue following manual input. An output followed immediately by script completion is released on that same sample: use `wait()` to make a press observable.

A resume may execute at most 4000 interpreter instructions before it must yield through `wait()`. An unyielding loop stops with an error. Each run also stops after 60 seconds. A profile holds up to 32 scripts, each at most 524288 source characters. These limits protect the input loop from common mistakes; this in-process interpreter does **not** impose a hard memory quota or isolate hostile code. Run trusted scripts.

## Timing and performance

The Windows input loop still targets 4 ms (250 Hz). `wait(1)` means resume on the first available sample at or after its deadline; it cannot create a 1 ms sampling schedule. Delays use the worker's monotonic clock. Neither this interval nor a script CPU benchmark measures end-to-end input latency.

The verification program checks timed outputs, manual input preservation, live reads, state, script cancellation/takeover, instruction budgets, errors, and profile persistence. It also prints a warmed CPU-only benchmark of 10000 Lua samples. The benchmark excludes input hardware, scheduling, drivers, and output transport; its result varies by machine and script.

The C# host and Rust virtual-device helper remain in place. A native engine rewrite should follow profiling of polling, script execution, allocation/GC, and report delivery. Native code can reduce allocation pressure and improve timing control, but changing the language does not change the polling period or remove Windows and USB scheduling delays.
