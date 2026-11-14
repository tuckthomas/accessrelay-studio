export const sequenceTimingFields = [
  { id: 'startDelay', label: 'Before movement', min: 0 },
  { id: 'downSweep', label: 'Move down', min: 0 },
  { id: 'downHold', label: 'Hold down', min: 1 },
  { id: 'upSweep', label: 'Move up', min: 4 },
  { id: 'upHold', label: 'Hold up', min: 1 },
  { id: 'centerHold', label: 'Return to center', min: 1 },
  { id: 'flick', label: 'Flick duration', min: 1 },
  { id: 'settle', label: 'Settle time', min: 1 }
] as const;
export type SequencePhase = typeof sequenceTimingFields[number]['id'];
