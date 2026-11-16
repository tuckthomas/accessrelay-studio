"""Measure a virtual stick gesture at XInput, beyond Studio's requested-output trace.

Run only after disconnecting Remote Play and stopping Studio controller output.
The helper owns and detaches its temporary controller. No physical input or user
profile is changed. --allow-test-input explicitly enables non-neutral reports.
With --commands, attach while Remote Play is disconnected, then reconnect the
client and append one JSON object per line to request a controlled practice shot.
Use {"stop": true} to detach. Commands never run automatically in this mode.
"""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import hashlib
from pathlib import Path
import queue
import struct
import subprocess
import threading
import time


class Gamepad(ctypes.Structure):
    _fields_ = [("buttons", wintypes.WORD), ("lt", ctypes.c_ubyte), ("rt", ctypes.c_ubyte),
                ("lx", ctypes.c_short), ("ly", ctypes.c_short), ("rx", ctypes.c_short), ("ry", ctypes.c_short)]


class State(ctypes.Structure):
    _fields_ = [("packet", wintypes.DWORD), ("gamepad", Gamepad)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--usbip", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--allow-test-input", action="store_true")
    parser.add_argument("--commands", type=Path, help="New JSONL file for explicitly triggered practice shots.")
    parser.add_argument("--gesture", type=Path, help="Prepared right-stick trajectory to replay only on an explicit play command.")
    parser.add_argument("--validate-gesture", action="store_true", help="Validate the gesture and exit without loading XInput or starting output.")
    parser.add_argument("--competing-down", action="store_true", help="Compare a replay against a second owned controller that keeps its down hold until the final neutral.")
    args = parser.parse_args()
    if not args.allow_test_input and not args.validate_gesture:
        parser.error("Close Remote Play and stop Studio output, then pass --allow-test-input.")
    if args.commands and args.commands.exists():
        parser.error("Use a new command file so old gestures cannot be replayed.")
    if args.competing_down and not (args.gesture and args.commands):
        parser.error("The competing-down comparison requires a prepared gesture and explicit command mode.")
    gesture = None
    if args.gesture:
        if not args.commands and not args.validate_gesture:
            parser.error("A recorded gesture requires explicit command mode.")
        gesture = json.loads(args.gesture.read_text(encoding="utf-8"))["points"]
        if not 2 <= len(gesture) <= 2000:
            parser.error("Recorded gesture must contain 2 to 2000 points.")
        previous_at = -1
        for point in gesture:
            at = point["atMs"]
            if not isinstance(at, (int, float)) or not previous_at < at <= 3000 or at < 0:
                parser.error("Gesture timestamps must increase within 0 to 3000 ms.")
            for axis in ("rightX", "rightY"):
                if not isinstance(point[axis], int) or not -32768 <= point[axis] <= 32767:
                    parser.error("Recorded stick axes must be signed 16-bit integers.")
            previous_at = at
        if gesture[-1]["rightX"] != 0 or gesture[-1]["rightY"] != 0:
            parser.error("Recorded gesture must end at neutral.")
        down_at = next((p["atMs"] for p in gesture if p["rightY"] == -32768), None)
        if down_at is None or not any(p["atMs"] > down_at and p["rightY"] == 32767 for p in gesture):
            parser.error("Recorded shot must contain full down followed by full up.")
    if args.validate_gesture:
        if gesture is None:
            parser.error("--validate-gesture requires --gesture.")
        print(f"SUCCESS Validated {len(gesture)} right-stick points; no controller attached or input generated.")
        return
    host, usbip = args.host.resolve(strict=True), args.usbip.resolve(strict=True)
    native = ctypes.WinDLL("xinput1_4.dll")
    native.XInputGetState.argtypes = [wintypes.DWORD, ctypes.POINTER(State)]
    native.XInputGetState.restype = wintypes.DWORD

    def snapshot():
        result = {}
        for slot in range(4):
            state = State()
            if native.XInputGetState(slot, ctypes.byref(state)) == 0:
                pad = state.gamepad
                result[slot] = (state.packet, pad.ry, pad.rx, pad.lx, pad.ly, pad.buttons, pad.lt, pad.rt)
        return result

    def physical_active(states):
        return any(any(abs(v) >= 4000 for v in values[1:5]) or values[5] or values[6] >= 30 or values[7] >= 30
                   for index, values in states.items() if index in before)

    def require_expected_slots(states):
        expected = set(before) | {slot} | ({competitor_slot} if competitor_slot is not None else set())
        if set(states) != expected:
            report["controllerTopologyChanged"] = {"expected": sorted(expected), "actual": sorted(states),
                                                    "atUtc": datetime.now(timezone.utc).isoformat()}
            raise RuntimeError("Controller slots changed; no game outcome may be attributed to this test.")

    before = snapshot()
    process = subprocess.Popen([str(host), str(usbip)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    ready = queue.Queue()
    threading.Thread(target=lambda: ready.put(process.stdout.readline()), daemon=True).start()
    errors = []
    threading.Thread(target=lambda: errors.append(process.stderr.read().decode(errors="replace")), daemon=True).start()
    slot = None
    competitor = None
    competitor_slot = None
    competitor_errors = []
    report = {"scope": "Actual XInput readback of an isolated virtual controller. No Xbox receipt or game recognition is established.",
              "host": str(host), "beforeSlots": list(before), "cases": []}

    def write(y, x=0, buttons=0):
        process.stdin.write(struct.pack("<HBBhhhh", buttons, 0, 0, 0, 0, x, y))
        process.stdin.flush()

    def write_competitor(y, x=0):
        if competitor is not None and competitor.poll() is None:
            competitor.stdin.write(struct.pack("<HBBhhhh", 0, 0, 0, 0, 0, x, y))
            competitor.stdin.flush()

    def cases():
        if not args.commands:
            for cadence in (2, 4, 8):
                for repeat in range(3):
                    yield cadence, repeat, 100, 56, 48
            return
        args.commands.parent.mkdir(parents=True, exist_ok=True)
        args.commands.touch(exist_ok=False)
        print(f"READY XInput slot {slot}; waiting for explicit commands in {args.commands}", flush=True)
        with args.commands.open(encoding="utf-8") as stream:
            idle = time.perf_counter()
            repeat = 0
            while time.perf_counter() - idle < 600:
                if process.poll() is not None:
                    raise RuntimeError("Virtual output exited while waiting for a test command.")
                position = stream.tell()
                line = stream.readline()
                if not line.endswith("\n"):
                    stream.seek(position)
                    time.sleep(.05)
                    continue
                params = json.loads(line)
                if params.get("stop"):
                    return
                pickup = params == {"pickup": True}
                if gesture is not None and not pickup and params != {"play": True}:
                    raise ValueError('Recorded playback requires exactly {"play": true}.')
                hold, duration, up_hold = (int(params.get(k, default)) for k, default in
                                            (("holdMs", 830), ("strokeMs", 56), ("upHoldMs", 200)))
                if not (100 <= hold <= 2500 and 8 <= duration <= 500 and 2 <= up_hold <= 2000):
                    raise ValueError("Practice gesture duration is outside the test limits.")
                require_expected_slots(snapshot())
                # A manual shot cannot be used as evidence for a generated one.
                # Require a quiet baseline and abort on manual activity below.
                began_wait = quiet_since = time.perf_counter()
                while time.perf_counter() - quiet_since < 3:
                    physical = snapshot()
                    require_expected_slots(physical)
                    if physical_active(physical):
                        quiet_since = time.perf_counter()
                    if time.perf_counter() - began_wait > 30:
                        raise RuntimeError("No quiet physical-input window; no practice gesture sent.")
                    time.sleep(.005)
                if pickup:
                    pickup_at = time.perf_counter()
                    released = False
                    write(0, buttons=0x1000)
                    try:
                        while time.perf_counter() - pickup_at < 1.2:
                            physical = snapshot()
                            require_expected_slots(physical)
                            if physical_active(physical):
                                raise RuntimeError("Manual input interrupted ball pickup; no shot sent.")
                            if not released and time.perf_counter() - pickup_at >= .1:
                                write(0)
                                released = True
                            time.sleep(.002)
                    finally:
                        write(0)
                    report.setdefault("preparations", []).append({"action": "A button pickup", "manualInputInterrupted": False})
                    print("PICKUP Sent A then neutral. Verify ball possession visually before the play command.", flush=True)
                    idle = time.perf_counter()
                    continue
                yield 2, repeat, hold, duration, up_hold
                repeat += 1
                idle = time.perf_counter()

    try:
        if ready.get(timeout=20).strip() != b"READY":
            raise RuntimeError("Virtual controller did not become ready: " + "".join(errors))
        deadline = time.perf_counter() + 8
        while time.perf_counter() < deadline:
            added = set(snapshot()) - set(before)
            if len(added) > 1:
                raise RuntimeError("More than one new controller appeared; cannot identify the test output.")
            if added:
                slot = added.pop()
                break
            time.sleep(.01)
        if slot is None:
            raise RuntimeError("The test controller did not appear in XInput.")
        report["testSlot"] = slot
        write(0)
        if args.competing_down:
            competitor = subprocess.Popen([str(host), str(usbip)], stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
            competitor_ready = queue.Queue()
            threading.Thread(target=lambda: competitor_ready.put(competitor.stdout.readline()), daemon=True).start()
            threading.Thread(target=lambda: competitor_errors.append(competitor.stderr.read().decode(errors="replace")), daemon=True).start()
            if competitor_ready.get(timeout=20).strip() != b"READY":
                raise RuntimeError("Comparison source controller did not become ready.")
            deadline = time.perf_counter() + 8
            while time.perf_counter() < deadline:
                added = set(snapshot()) - set(before) - {slot}
                if len(added) > 1:
                    raise RuntimeError("More than one comparison controller appeared.")
                if added:
                    competitor_slot = added.pop()
                    break
                time.sleep(.01)
            if competitor_slot is None:
                raise RuntimeError("Comparison controller did not appear in XInput.")
            report["competingDownSlot"] = competitor_slot
            write_competitor(0)
        for cadence, repeat, hold, duration, up_hold in cases():
                commands = [(0, -32768)]
                points = list(range(cadence, duration, cadence)) + [duration]
                commands += [(hold + ms, round(-32768 + 65535 * ms / duration)) for ms in points]
                commands += [(hold + duration + up_hold, 0)]
                if gesture is not None:
                    commands = [(p["atMs"], p["rightY"], p["rightX"]) for p in gesture]
                else:
                    commands = [(at, y, 0) for at, y in commands]
                case = {"producerCadenceMs": cadence, "repeat": repeat, "holdMs": hold, "strokeDurationMs": duration, "upHoldMs": up_hold,
                        "commands": [], "observations": [], "physicalObservations": [], "manualInputInterrupted": False,
                        "gameplayAttributionValid": False, "startedUtc": datetime.now(timezone.utc).isoformat()}
                if gesture is not None:
                    case["gestureFile"] = str(args.gesture.resolve())
                    case["gestureSha256"] = hashlib.sha256(args.gesture.read_bytes()).hexdigest()
                    case["holdMs"] = case["strokeDurationMs"] = case["upHoldMs"] = None
                case["competingObservations"] = []
                held_down = next((p for p in gesture or [] if p["rightY"] == -32768), None)
                began = time.perf_counter()
                index = 0
                last = None
                while (elapsed := (time.perf_counter() - began) * 1000) < commands[-1][0] + 250:
                    physical = snapshot()
                    require_expected_slots(physical)
                    case["physicalObservations"].append({"atMs": elapsed, "states": {k: v for k, v in physical.items() if k in before}})
                    if competitor_slot is not None:
                        case["competingObservations"].append({"atMs": elapsed, "state": physical.get(competitor_slot)})
                    if args.commands and physical_active(physical):
                        write(0)
                        write_competitor(0)
                        case["manualInputInterrupted"] = True
                        break
                    while index < len(commands) and elapsed >= commands[index][0]:
                        planned, y, x = commands[index]
                        write(y, x)
                        if competitor is not None:
                            if index == len(commands) - 1:
                                write_competitor(0)
                            elif held_down is not None and planned > held_down["atMs"]:
                                write_competitor(-32768, held_down["rightX"])
                            else:
                                write_competitor(y, x)
                        case["commands"].append({"plannedMs": planned, "sentMs": (time.perf_counter() - began) * 1000, "rightY": y, "rightX": x})
                        index += 1
                    state = State()
                    if native.XInputGetState(slot, ctypes.byref(state)) != 0:
                        raise RuntimeError("Test controller disconnected during the stroke: " + "".join(errors))
                    if state.packet != last:
                        last = state.packet
                        case["observations"].append({"atMs": (time.perf_counter() - began) * 1000,
                                                     "packet": state.packet, "rightY": state.gamepad.ry, "rightX": state.gamepad.rx})
                    time.sleep(.00025)
                lags = []
                for observed in case["observations"]:
                    sent = [c for c in case["commands"] if c["sentMs"] <= observed["atMs"]
                            and c["rightY"] == observed["rightY"] and c["rightX"] == observed["rightX"]]
                    if sent:
                        lags.append(observed["atMs"] - sent[-1]["sentMs"])
                up = next((o["atMs"] for o in case["observations"] if o["rightY"] == 32767), None)
                ramp_after = hold
                if gesture is not None:
                    down_at = next(p["atMs"] for p in gesture if p["rightY"] == -32768)
                    ramp_after = next(p["atMs"] for p in gesture if p["atMs"] > down_at and p["rightY"] > -32768)
                first = next((o["atMs"] for o in case["observations"] if -32768 < o["rightY"] < 32767 and o["atMs"] >= ramp_after), None)
                case["deliveredUpAtMs"] = up
                case["deliveredRampSpanMs"] = up - first if up is not None and first is not None else None
                case["maxObservedReportLagMs"] = max(lags) if lags else None
                case["observedTransitionCount"] = len(case["observations"])
                report["cases"].append(case)
                args.output.parent.mkdir(parents=True, exist_ok=True)
                args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
                print(json.dumps({k: v for k, v in case.items() if k not in ("commands", "observations", "physicalObservations", "competingObservations")}), flush=True)
                if case["manualInputInterrupted"]:
                    raise RuntimeError("Physical input changed during the test. Gesture cancelled; no game outcome may be attributed to it.")
    finally:
        cleanup_errors = []
        for owned, neutral in ((competitor, write_competitor), (process, write)):
            if owned is None:
                continue
            try:
                if owned.poll() is None:
                    neutral(0)
                    time.sleep(.1)
                    owned.stdin.close()
                    owned.wait(timeout=12)
            except (OSError, subprocess.TimeoutExpired) as error:
                cleanup_errors.append(str(error))
            finally:
                if owned.poll() is None:
                    owned.kill()
                    owned.wait(timeout=5)
        if competitor is not None:
            report["competingExitCode"] = competitor.returncode
            report["competingErrors"] = "".join(competitor_errors)
        report["cleanupErrors"] = cleanup_errors
        report["helperExitCode"] = process.returncode
        report["helperErrors"] = "".join(errors)
        report["afterSlots"] = list(snapshot())
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    if process.returncode != 0 or slot in snapshot() or competitor_slot in snapshot() or (competitor is not None and competitor.returncode != 0):
        raise RuntimeError("The test controller did not detach cleanly: " + "".join(errors))
    if any(c["deliveredUpAtMs"] is None for c in report["cases"]):
        raise RuntimeError("At least one full-up endpoint was not observed. See the evidence file.")
    print("SUCCESS Isolated XInput stroke measurement and controller detach: " + str(args.output))


if __name__ == "__main__":
    main()
