import { describe, expect, it } from 'vitest';
import { phaseDuration, recordingPhases, retimeRecording } from './recording-timing';

const take = () => ({ id: 'recording', name: 'Shot', durationMs: 250, frames:
  [[0, 0], [10, -4000], [20, -18000], [40, -32768], [80, -30000],
    [120, -5000], [140, 12000], [160, 32767], [190, 29000], [210, 0]]
    .map(([timeMs, rightY], i) => ({ timeMs, state: { rightY, rightX: i * 33, buttons: i % 2 } })) });

describe('recorded phase timing previews', () => {
  it('measures captured transitions and holds, excluding neutral lead-in and tail', () => {
    const recording = take(), phases = recordingPhases(recording);
    expect(Object.fromEntries(Object.entries(phases).map(([key, interval]) => [key, phaseDuration(recording, interval)])))
      .toEqual({ downSweep: 30, downHold: 80, upSweep: 40, upHold: 50 });
  });
  it('retimes multiple phases without changing any captured inputs or the original draft', () => {
    const recording = take(), original = structuredClone(recording);
    const preview = retimeRecording(recording, { downHold: '160', upSweep: '20' });
    expect(recording).toEqual(original);
    expect(preview.frames.map(frame => frame.state)).toEqual(recording.frames.map(frame => frame.state));
    expect(preview.frames.map(frame => frame.timeMs)).toEqual([0, 10, 20, 40, 120, 200, 210, 220, 250, 270]);
    expect(preview.durationMs).toBe(310);
    const phases = recordingPhases(preview);
    expect(phaseDuration(preview, phases.downHold!)).toBe(160);
    expect(phaseDuration(preview, phases.upSweep!)).toBe(20);
  });
  it('keeps direct sample jumps and absent or interrupted movements unavailable', () => {
    expect(recordingPhases({ durationMs: 20, frames: [{ timeMs: 0, state: { rightY: -32768 } },
      { timeMs: 10, state: { rightY: 32767 } }] })).toEqual({ downHold: { start: 0, end: 1 }, upHold: { start: 1, end: 2 } });
    expect(recordingPhases({ durationMs: 10, frames: [{ timeMs: 0, state: {} }] })).toEqual({});
    const recording = take(); recording.frames[7].state.rightY = -32768;
    expect(recordingPhases(recording).upHold).toBeUndefined();
  });
  it('rejects invalid or excessive durations while retaining every sample on compression', () => {
    for (const value of ['', '-1', '1', '2.5', 'NaN'])
      expect(() => retimeRecording(take(), { downHold: value })).toThrow('at least 2 ms');
    expect(() => retimeRecording(take(), { downHold: '10000' })).toThrow('10,000 ms');
    const compressed = retimeRecording(take(), { downHold: '2' });
    expect(compressed.frames.slice(3, 6).map(frame => frame.timeMs)).toEqual([40, 41, 42]);
    expect(compressed.frames.length).toBe(take().frames.length);
  });
});
