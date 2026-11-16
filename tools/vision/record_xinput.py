"""Passively record controller changes for comparison with a generated gesture.

This tool only calls XInputGetState. It never attaches a controller or sends input.
Create the --stop-file to finish early; an existing stop file is rejected.
"""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
from pathlib import Path
import time


class Gamepad(ctypes.Structure):
    _fields_ = [("buttons", wintypes.WORD), ("lt", ctypes.c_ubyte), ("rt", ctypes.c_ubyte),
                ("lx", ctypes.c_short), ("ly", ctypes.c_short), ("rx", ctypes.c_short), ("ry", ctypes.c_short)]


class State(ctypes.Structure):
    _fields_ = [("packet", wintypes.DWORD), ("gamepad", Gamepad)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stop-file", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=300)
    args = parser.parse_args()
    if args.output.exists() or args.stop_file.exists():
        parser.error("Use new output and stop-file paths.")
    if not 1 <= args.seconds <= 600:
        parser.error("Recording duration must be between 1 and 600 seconds.")
    native = ctypes.WinDLL("xinput1_4.dll")
    native.XInputGetState.argtypes = [wintypes.DWORD, ctypes.POINTER(State)]
    native.XInputGetState.restype = wintypes.DWORD
    report = {"scope": "Read-only XInput recording; no input generated. Game outcome must be separately identified.",
              "startedUtc": datetime.now(timezone.utc).isoformat(), "events": []}
    previous = {}
    began = time.perf_counter()
    print("READY Passive recording only; no controller output will be generated.", flush=True)
    try:
        while time.perf_counter() - began < args.seconds and not args.stop_file.exists():
            for slot in range(4):
                state = State()
                connected = native.XInputGetState(slot, ctypes.byref(state)) == 0
                pad = state.gamepad
                values = (connected, pad.buttons, pad.lt, pad.rt, pad.lx, pad.ly, pad.rx, pad.ry)
                if values == previous.get(slot):
                    continue
                previous[slot] = values
                report["events"].append({"atMs": (time.perf_counter() - began) * 1000, "slot": slot,
                    "connected": connected, "packet": state.packet, "buttons": pad.buttons,
                    "leftTrigger": pad.lt, "rightTrigger": pad.rt,
                    "leftX": pad.lx, "leftY": pad.ly, "rightX": pad.rx, "rightY": pad.ry})
            time.sleep(.001)
    finally:
        report["durationMs"] = (time.perf_counter() - began) * 1000
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"SUCCESS Saved {len(report['events'])} passive controller changes to {args.output}", flush=True)


if __name__ == "__main__":
    main()
