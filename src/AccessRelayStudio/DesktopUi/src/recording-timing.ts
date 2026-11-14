export const recordingPhaseLabels = {
  downSweep: 'Move down', downHold: 'Hold down', upSweep: 'Move up', upHold: 'Hold up'
} as const;
export type RecordingPhase = keyof typeof recordingPhaseLabels;
type Interval = { start: number; end: number };
type Recording = { durationMs: number; frames: { timeMs: number; state: { rightY?: number } }[] };

// Editor preview of Core/MacroRecording.FindRightStickIntervals: 75% deflection
// marks a hold. Missing transitions stay absent; no synthetic samples are added.
export function recordingPhases(recording: Recording): Partial<Record<RecordingPhase, Interval>> {
  const frames = recording.frames, y = (i: number) => frames[i].state.rightY ?? 0;
  const down = frames.findIndex(frame => (frame.state.rightY ?? 0) <= -24576);
  if (down < 0) return {};
  let start = down;
  while (start > 0 && y(start - 1) < -1600) start--;
  let release = down + 1;
  while (release < frames.length && y(release) <= -24576) release++;
  const phases: Partial<Record<RecordingPhase, Interval>> = { downHold: { start: down, end: release } };
  if (start < down) phases.downSweep = { start, end: down };
  let up = release;
  while (up < frames.length && y(up) > -24576 && y(up) < 24576) up++;
  if (up === frames.length || y(up) < 24576) return phases;
  if (up > release) phases.upSweep = { start: release, end: up };
  let end = up + 1;
  while (end < frames.length && y(end) >= 24576) end++;
  phases.upHold = { start: up, end };
  return phases;
}

export function phaseDuration(recording: Recording, interval: Interval) {
  return (recording.frames[interval.end]?.timeMs ?? recording.durationMs) - recording.frames[interval.start].timeMs;
}

// Matches MacroRecording.WithIntervalDuration; native recordUpdate still validates
// the complete draft before saving. Only timestamps change, never sampled values.
export function retimeRecording<T extends Recording>(recording: T, changes: Partial<Record<RecordingPhase, string>>): T {
  const phases = recordingPhases(recording);
  const edits = (Object.entries(changes) as [RecordingPhase, string][]).map(([key, text]) => {
    const interval = phases[key], duration = Number(text);
    if (!interval) throw new Error(`${recordingPhaseLabels[key]} was not captured.`);
    const minimum = interval.end - interval.start;
    if (!text.trim() || !Number.isSafeInteger(duration) || duration < minimum)
      throw new Error(`${recordingPhaseLabels[key]} needs at least ${minimum} ms to keep every sample.`);
    return { interval, duration, shift: duration - phaseDuration(recording, interval) };
  });
  if (!edits.length) return recording;
  const total = recording.durationMs + edits.reduce((sum, edit) => sum + edit.shift, 0);
  if (total < 1 || total > 10000) throw new Error('The whole recording must last between 1 and 10,000 ms.');
  const frames = recording.frames.map(frame => ({ ...frame }));
  let result = { ...recording, frames } as T;
  // Shorten first so a valid final duration never exceeds the limit temporarily.
  for (const { interval: { start, end }, duration, shift } of edits.sort((a, b) => a.shift - b.shift)) {
    const origin = frames[start].timeMs, oldDuration = phaseDuration(result, { start, end });
    if (oldDuration <= 0) throw new Error('Correct the sample timestamps before changing phase timing.');
    let previous = origin;
    for (let i = start + 1; i < end; i++) {
      const scaled = Math.floor(((frames[i].timeMs - origin) * duration + oldDuration / 2) / oldDuration);
      frames[i].timeMs = Math.min(origin + duration - (end - i), Math.max(previous + 1, origin + scaled));
      previous = frames[i].timeMs;
    }
    for (let i = end; i < frames.length; i++) frames[i].timeMs += shift;
    result = { ...result, durationMs: result.durationMs + shift };
  }
  return result;
}
