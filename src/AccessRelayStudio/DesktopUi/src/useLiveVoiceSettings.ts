import { useEffect, useRef, useState } from 'react';

export const defaultEffects = {
  bypass: false, mix: 1, formantSemitones: 0, formantStrength: 1, preEqDb: Array(10).fill(0) as number[], postEqDb: Array(10).fill(0) as number[],
  lowCutHz: 0, humHz: 0, gateThresholdDb: -80, compressorThresholdDb: 0, compressorRatio: 3, deEsser: 0, noiseReduction: 0,
  robot: 0, robotHz: 80, distortion: 0, drive: 4, bitDepth: 16, downsampleHz: 48000, chorus: 0, chorusHz: 1.2, phaser: 0,
  vibrato: 0, tremolo: 0, tremoloHz: 5, gapper: 0, gapperHz: 8, growl: 0, nasal: 0, breathy: 0, underwater: 0,
  harmony: 0, harmonySemitones: 7, detune: 0, echo: 0, echoMs: 220, echoFeedback: .3, reverb: 0, reverbSize: .5,
  monotone: 0, hypertone: 0, diode: 0, octaveUp: false, animal: 0, animalDelayMs: 300,
};
export type VoiceEffects = typeof defaultEffects;
export type VoiceSettings = { preset: string; pitchSemitones: number; gainDb: number; muted: boolean; effects: VoiceEffects };
export const defaultSettings: VoiceSettings = { preset: 'clean', pitchSemitones: 0, gainDb: 0, muted: false, effects: defaultEffects };
export const normalizeVoice = (value: any): VoiceSettings => ({ ...defaultSettings, ...value, effects: { ...defaultEffects, ...value?.effects } });
const signature = (value: VoiceSettings) => JSON.stringify(value);

// Send the newest slider position at a bounded rate. A slow native response never
// builds a backlog of obsolete positions, locks the controls, or drops the final edit.
export function useLiveVoiceSettings(incoming: any, command: (action: string, args?: any) => Promise<any>, reportError: (message: string) => void) {
  const [settings, setSettings] = useState(() => normalizeVoice(incoming));
  const [pending, setPending] = useState(false);
  const desired = useRef(settings), applied = useRef(settings), worker = useRef<Promise<void> | null>(null);
  const mounted = useRef(true), failure = useRef(''), send = useRef(command), error = useRef(reportError);
  send.current = command; error.current = reportError;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const incomingSignature = signature(normalizeVoice(incoming));
  useEffect(() => {
    if (worker.current || signature(desired.current) !== signature(applied.current)) return;
    desired.current = applied.current = normalizeVoice(incoming);
    setSettings(desired.current);
  }, [incomingSignature]);

  function update(change: Partial<VoiceSettings> | ((current: VoiceSettings) => VoiceSettings)) {
    desired.current = typeof change === 'function' ? change(desired.current) : { ...desired.current, ...change };
    setSettings(desired.current); setPending(true); failure.current = ''; error.current('');
    if (worker.current) return;
    worker.current = (async () => {
      try {
        do {
          await new Promise(resolve => setTimeout(resolve, 40));
          const next = desired.current;
          if (signature(next) === signature(applied.current)) break;
          await send.current('settingsSave', { settings: next });
          applied.current = next;
        } while (signature(desired.current) !== signature(applied.current));
      } catch (reason) {
        failure.current = reason instanceof Error ? reason.message : String(reason);
        if (mounted.current) error.current(failure.current);
      } finally {
        worker.current = null;
        if (mounted.current) setPending(signature(desired.current) !== signature(applied.current));
      }
    })();
  }
  async function flush() {
    while (worker.current) await worker.current;
    if (failure.current) throw new Error(failure.current);
  }
  return { settings, update, pending, flush };
}
