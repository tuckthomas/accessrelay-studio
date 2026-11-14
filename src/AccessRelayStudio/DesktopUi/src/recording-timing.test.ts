import { describe, expect, it } from 'vitest';
import { phaseDuration, recordingPhases, retimeRecording } from './recording-timing';

const take = () => ({ id: 'recording', name: 'Shot', durationMs: 250, frames:
  [[0, 0], [10, -4000], [20, -18000], [40, -32768], [80, -30000],
    [120, -5000], [140, 12000], [160, 32767], [190, 29000], [210, 0]]
    .map(([timeMs, rightY], i) => ({ timeMs, state: { rightY, rightX: i * 33, buttons: i % 2 } })) });

describe('recorded phase timing previews', () => {
  it('measures transitions, holds, neutral lead-in and the return to center', () => {
    const recording = take(), phases = recordingPhases(recording);
    expect(Object.fromEntries(Object.entries(phases).map(([key, interval]) => [key, phaseDuration(recording, interval)])))
      .toEqual({ startDelay: 10, downSweep: 30, downHold: 80, upSweep: 40, upHold: 50, centerHold: 40 });
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
    expect(recordingPhases({ durationMs: 10, frames: [{ timeMs: 0, state: {} }] })).toEqual({ startDelay: { start: 0, end: 1 } });
    const recording = take(); recording.frames = recording.frames.slice(0, 8); recording.frames[7].state.rightY = -32768;
    expect(recordingPhases(recording).upHold).toBeUndefined();
  });
  it('adds and measures all missing stages, including a separate flick and settle before the main hold', () => {
    const original = { channels: 'rightStick', durationMs: 100, frames: [
      { timeMs: 0, state: { rightX: 0, rightY: -32768, buttons: 0 } },
      { timeMs: 80, state: { rightX: 0, rightY: 32767, buttons: 0 } }] };
    const preview = retimeRecording(original, { startDelay: '50', flick: '60', settle: '40', downSweep: '30', upSweep: '40', centerHold: '80' });
    const phases = recordingPhases(preview);
    expect(Object.fromEntries(Object.entries(phases).map(([key, interval]) => [key, phaseDuration(preview, interval)])))
      .toEqual({ startDelay: 50, flick: 60, settle: 40, downSweep: 30, downHold: 80, upSweep: 40, upHold: 20, centerHold: 80 });
    expect(preview.frames.some(frame => frame.state.rightY === -32768)).toBe(true);
    expect(preview.durationMs).toBe(400);
    expect(preview.frames[0].timeMs).toBe(0);
    expect(preview.frames.every((frame, i) => frame.timeMs < preview.durationMs && (!i || frame.timeMs > preview.frames[i - 1].timeMs))).toBe(true);
    expect(original.frames.length).toBe(2);
    const removed = retimeRecording(preview, { startDelay: '0', flick: '0', settle: '0', downSweep: '0', centerHold: '0' });
    expect(removed.durationMs).toBe(140);
    expect(removed.frames[0].timeMs).toBe(0);
  });
  it('changes flick direction without redirecting the main movement or unsupported channels', () => {
    const original = take();
    const withFlick = retimeRecording(original, { flick: '60', settle: '40' });
    const redirected = retimeRecording(withFlick, {}, { flickX: 32767, flickY: 0 });
    const flick = recordingPhases(redirected).flick!;
    expect(redirected.frames[flick.start].state.rightX).toBe(32767);
    expect(redirected.frames[flick.start].state.rightY).toBe(0);
    expect(redirected.frames.slice(flick.end)).toEqual(withFlick.frames.slice(flick.end));
    expect(() => retimeRecording({ ...withFlick, channels: 'rightStickY' }, {}, { flickX: 32767, flickY: 0 })).toThrow('axis that was not recorded');
    expect(() => retimeRecording(original, { settle: '40' })).toThrow('Set a flick duration');
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
