import { sequenceTimingFields, type SequencePhase } from './sequence-timing';

export const recordingPhaseLabels = Object.fromEntries(sequenceTimingFields.map(field => [field.id, field.label])) as Record<SequencePhase, string>;
export type RecordingPhase = SequencePhase;
type Interval = { start: number; end: number };
type State = { [key: string]: number | undefined; rightX?: number; rightY?: number };
type Recording = { channels?: string | number; durationMs: number; frames: { timeMs: number; state: State }[] };
export type FlickDirection = { flickX: number; flickY: number };
const phaseOrder: SequencePhase[] = ['startDelay', 'flick', 'settle', 'downSweep', 'downHold', 'upSweep', 'upHold', 'centerHold'];
const optionalPhases: SequencePhase[] = ['startDelay', 'downSweep', 'centerHold', 'flick', 'settle'];
const neutral = (state: State) => Math.abs(state.rightX ?? 0) <= 1600 && Math.abs(state.rightY ?? 0) <= 1600;

export function hasRecordingAxis(recording: Recording, axis: 'rightX' | 'rightY') {
  if (typeof recording.channels === 'number') return (recording.channels & (axis === 'rightX' ? 0x80000 : 0x200000)) !== 0;
  if (typeof recording.channels === 'string') return recording.channels.toLowerCase().split(/[,|\s]+/)
    .some(channel => ['all', 'rightstick', axis === 'rightX' ? 'rightstickx' : 'rightsticky'].includes(channel));
  return recording.frames.some(frame => frame.state[axis] !== undefined);
}

// Holds use MacroRecording's 75% threshold. Find the first complete down/up
// movement, skipping a separate lead-in flick that returns before the main hold.
export function recordingPhases(recording: Recording): Partial<Record<SequencePhase, Interval>> {
  const frames = recording.frames, y = (i: number) => frames[i].state.rightY ?? 0;
  const phases: Partial<Record<SequencePhase, Interval>> = {};
  let first = frames.findIndex(frame => !neutral(frame.state) || Object.entries(frame.state)
    .some(([key, value]) => !['rightX', 'rightY'].includes(key) && !!value));
  if (first < 0) first = frames.length;
  if (first > 0) phases.startDelay = { start: 0, end: first };
  if (!hasRecordingAxis(recording, 'rightY')) return phases;
  let fallback: { down: number; release: number } | undefined;
  for (let down = 0; down < frames.length; down++) {
    if (y(down) > -24576) continue;
    let release = down + 1;
    while (release < frames.length && y(release) <= -24576) release++;
    fallback ??= { down, release };
    let up = release;
    while (up < frames.length && y(up) > -24576 && y(up) < 24576) up++;
    if (up < frames.length && y(up) >= 24576) {
      let end = up + 1;
      while (end < frames.length && y(end) >= 24576) end++;
      phases.upHold = { start: up, end };
      if (up > release) phases.upSweep = { start: release, end: up };
      if (end < frames.length) {
        let tail = end;
        while (tail < frames.length && !neutral(frames[tail].state)) tail++;
        while (tail < frames.length && neutral(frames[tail].state)) tail++;
        phases.centerHold = { start: end, end: tail };
      }
      fallback = { down, release }; break;
    }
    down = release - 1;
  }
  if (!fallback) return phases;
  const { down, release } = fallback;
  let start = down;
  while (start > 0 && y(start - 1) < -1600) start--;
  phases.downHold = { start: down, end: release };
  if (start < down) phases.downSweep = { start, end: down };
  if (first < start && !neutral(frames[first].state)) {
    let rest = first;
    while (rest < start && !neutral(frames[rest].state)) rest++;
    if (rest < start && frames.slice(rest, start).every(frame => neutral(frame.state))) {
      phases.flick = { start: first, end: rest };
      phases.settle = { start: rest, end: start };
    }
  }
  return phases;
}

export function phaseDuration(recording: Recording, interval: Interval) {
  return (recording.frames[interval.end]?.timeMs ?? recording.durationMs) - recording.frames[interval.start].timeMs;
}

export function recordingFlickDirection(recording: Recording): FlickDirection {
  const interval = recordingPhases(recording).flick;
  const peak = interval && recording.frames.slice(interval.start, interval.end).reduce((best, frame) =>
    Math.hypot(frame.state.rightX ?? 0, frame.state.rightY ?? 0) > Math.hypot(best.state.rightX ?? 0, best.state.rightY ?? 0) ? frame : best);
  return { flickX: peak ? peak.state.rightX ?? 0 : 0, flickY: peak ? peak.state.rightY ?? 0 : -32767 };
}

// Local preview; native recordUpdate validates and saves the complete draft once.
// Existing phases keep their sampled values unless explicitly removed or redirected.
export function retimeRecording<T extends Recording>(recording: T, changes: Partial<Record<SequencePhase, string>>,
  direction?: FlickDirection): T {
  if (!Object.keys(changes).length && !direction) return recording;
  const phases = recordingPhases(recording), count = recording.frames.length;
  const mainStart = phases.downSweep?.start ?? phases.downHold?.start ?? count;
  const insertAt: Record<SequencePhase, number> = {
    startDelay: 0, flick: phases.settle?.start ?? mainStart, settle: mainStart, downSweep: phases.downHold?.start ?? count,
    downHold: mainStart, upSweep: phases.upHold?.start ?? phases.downHold?.end ?? count,
    upHold: phases.downHold?.end ?? count, centerHold: phases.upHold?.end ?? count
  };
  const edits = (Object.entries(changes) as [SequencePhase, string][]).map(([key, text]) => {
    const interval = phases[key] ?? { start: insertAt[key], end: insertAt[key] }, duration = Number(text);
    const minimum = optionalPhases.includes(key) ? 0 : Math.max(1, interval.end - interval.start);
    if (!text.trim() || !Number.isSafeInteger(duration) || duration < minimum)
      throw new Error(`${recordingPhaseLabels[key]} needs at least ${minimum} ms.`);
    if (duration > 0 && interval.start === interval.end && key !== 'startDelay' && !hasRecordingAxis(recording, 'rightY'))
      throw new Error('This recording does not include the right-stick vertical axis.');
    const samples = interval.end - interval.start || (key === 'downSweep' ? 3 : key === 'upSweep' ? 4 : 1);
    if (duration > 0 && duration < samples) throw new Error(`${recordingPhaseLabels[key]} needs at least ${samples} ms to keep every sample.`);
    return { key, ...interval, duration, oldDuration: phases[key] ? phaseDuration(recording, interval) : 0 };
  }).filter(edit => edit.duration !== edit.oldDuration);
  const durationFor = (key: SequencePhase) => changes[key] !== undefined ? Number(changes[key]) : phases[key] ? phaseDuration(recording, phases[key]!) : 0;
  if (durationFor('flick') > 0 && durationFor('settle') < 1) throw new Error('A flick needs at least 1 ms of settle time before the main movement.');
  if (durationFor('settle') > 0 && durationFor('flick') < 1) throw new Error('Set a flick duration before adding settle time.');
  if (direction) {
    if (![direction.flickX, direction.flickY].every(value => Number.isInteger(value) && value >= -32768 && value <= 32767))
      throw new Error('Flick direction must be between -32768 and 32767.');
    if (!durationFor('flick')) throw new Error('Set a flick duration before changing its direction.');
    if ((!hasRecordingAxis(recording, 'rightX') && direction.flickX !== 0) || (!hasRecordingAxis(recording, 'rightY') && direction.flickY !== 0))
      throw new Error('The flick direction uses an axis that was not recorded.');
    if (Math.hypot(direction.flickX, direction.flickY) <= 1600) throw new Error('Choose a flick direction outside the stick center.');
  }
  const durationMs = recording.durationMs + edits.reduce((total, edit) => total + edit.duration - edit.oldDuration, 0);
  if (durationMs < 1 || durationMs > 10000) throw new Error('The whole recording must last between 1 and 10,000 ms.');
  if (edits.some(edit => edit.start < edit.end && edit.oldDuration <= 0))
    throw new Error('Correct the sample timestamps before changing phase timing.');
  const source = recording.frames.map(frame => ({ ...frame, state: { ...frame.state } }));
  if (direction && phases.flick) {
    const old = recordingFlickDirection(recording), length = old.flickX ** 2 + old.flickY ** 2;
    const a = (direction.flickX * old.flickX + direction.flickY * old.flickY) / length;
    const b = (direction.flickY * old.flickX - direction.flickX * old.flickY) / length;
    const clamp = (value: number) => Math.max(-32768, Math.min(32767, Math.round(value)));
    for (let i = phases.flick.start; i < phases.flick.end; i++) {
      const state = source[i].state, x = state.rightX ?? 0, y = state.rightY ?? 0;
      if (hasRecordingAxis(recording, 'rightX')) state.rightX = clamp(a * x - b * y);
      if (hasRecordingAxis(recording, 'rightY')) state.rightY = clamp(b * x + a * y);
    }
  }
  const frames: typeof source = [];
  let cursor = 0, shift = 0;
  for (const edit of edits.sort((a, b) => a.start - b.start || phaseOrder.indexOf(a.key) - phaseOrder.indexOf(b.key))) {
    const { key, start, end, duration, oldDuration } = edit;
    while (cursor < start) { frames.push({ ...source[cursor], timeMs: source[cursor].timeMs + shift }); cursor++; }
    const origin = (source[start]?.timeMs ?? recording.durationMs) + shift;
    if (start === end && duration > 0) {
      const template = source[start] ?? source.at(-1)!;
      const points = key === 'downSweep' ? [-8192, -16384, -24575] : key === 'upSweep' ? [-24575, -8192, 8192, 24575] :
        [key === 'flick' ? direction?.flickY ?? -32767 : key === 'downHold' ? -32767 : key === 'upHold' ? 32767 : 0];
      points.forEach((rightY, i) => {
        const state: State = { ...template.state, rightY, rightX: key === 'flick' ? direction?.flickX ?? 0 : 0 };
        if (key === 'startDelay') Object.keys(state).forEach(axis => { state[axis] = 0; });
        frames.push({ ...template, timeMs: origin + Math.floor(i * duration / points.length), state });
      });
    } else if (duration > 0) {
      let previous = origin - 1;
      for (let i = start; i < end; i++) {
        const scaled = Math.floor(((source[i].timeMs - source[start].timeMs) * duration + oldDuration / 2) / oldDuration);
        const timeMs = Math.min(origin + duration - (end - i), Math.max(previous + 1, origin + scaled));
        frames.push({ ...source[i], timeMs }); previous = timeMs;
      }
    }
    cursor = end; shift += duration - oldDuration;
  }
  while (cursor < count) { frames.push({ ...source[cursor], timeMs: source[cursor].timeMs + shift }); cursor++; }
  if (!frames.length || frames.length > 4096) throw new Error('The recording must contain between 1 and 4,096 samples.');
  if (frames[0].timeMs !== 0 || frames.some((frame, i) => !Number.isSafeInteger(frame.timeMs) || frame.timeMs >= durationMs || (i > 0 && frame.timeMs <= frames[i - 1].timeMs)))
    throw new Error('Correct the sample timestamps before changing phase timing.');
  return { ...recording, frames, durationMs } as T;
}
