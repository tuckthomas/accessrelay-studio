import type { ReactNode } from 'react';
import { Button, Field, Toggle } from './ui';
import { defaultEffects, defaultSettings, type VoiceEffects, type VoiceSettings } from './useLiveVoiceSettings';

type Props = { settings: VoiceSettings; disabled: boolean; update: (change: (current: VoiceSettings) => VoiceSettings) => void };
const recipes: { name: string; pitch?: number; preset?: string; effects?: Partial<VoiceEffects> }[] = [
  { name: 'Natural' }, { name: 'Warm', preset: 'warm' }, { name: 'Bright', preset: 'bright' }, { name: 'Deep', preset: 'deep' },
  { name: 'Radio', preset: 'radio' }, { name: 'Giant', pitch: -5, effects: { formantSemitones: -5 } },
  { name: 'Small character', pitch: 5, effects: { formantSemitones: 5 } },
  { name: 'Robot', effects: { robot: .85, robotHz: 90, distortion: .15 } },
  { name: 'Alien', pitch: 3, effects: { formantSemitones: -3, chorus: .4, phaser: .5 } },
  { name: 'Telephone', effects: { lowCutHz: 300, postEqDb: [0, -20, -15, 0, 2, 3, 2, -10, -20, -20] } },
  { name: 'Broadcast', preset: 'warm', effects: { lowCutHz: 80, compressorThresholdDb: -18, compressorRatio: 3, deEsser: .4 } },
  { name: 'Cavern', effects: { reverb: .6, reverbSize: .8, echo: .25, echoMs: 320 } },
  { name: 'Underwater', effects: { underwater: .9, chorus: .3, vibrato: .25 } },
];

export function VoiceEffectsPanel({ settings, disabled, update }: Props) {
  const fx = settings.effects;
  function set(key: keyof VoiceEffects, value: any) {
    update(current => ({ ...current, effects: { ...current.effects, [key]: value,
      ...(key === 'monotone' && value > 0 ? { hypertone: 0 } : key === 'hypertone' && value > 0 ? { monotone: 0 } : {}) } }));
  }
  function slider(key: keyof VoiceEffects, label: string, min = 0, max = 1, step = .01, unit = '%') {
    const value = Number(fx[key]);
    const display = unit === '%' ? Math.round(value * 100) + '%' : value.toFixed(step < 1 ? 1 : 0) + (unit ? ` ${unit}` : '');
    return <Field label={<span>{label} <strong>{display}</strong></span>}><input type="range" aria-label={label} disabled={disabled}
      min={min} max={max} step={step} value={value} onChange={event => set(key, Number(event.target.value))} /></Field>;
  }
  function effect(key: keyof VoiceEffects, label: string, initial: number, children?: ReactNode) {
    const enabled = Number(fx[key]) > 0;
    return <details className="voice-effect" data-active={enabled}><summary>{label}</summary>
      <Toggle label={`${label} enabled`} checked={enabled} disabled={disabled} onChange={on => set(key, on ? initial : 0)} />
      {enabled && <div className="voice-effect-controls">{slider(key, `${label} amount`)}{children}</div>}</details>;
  }
  return <div className="voice-effects-panel">
    <div className="voice-effects-toolbar"><Field label="Voice preset"><select aria-label="Voice preset" value="" disabled={disabled} onChange={event => {
      const recipe = recipes[Number(event.target.value)]; if (!recipe) return;
      update(current => ({ ...defaultSettings, preset: recipe.preset || 'clean', pitchSemitones: recipe.pitch || 0,
        gainDb: current.gainDb, muted: current.muted, effects: { ...defaultEffects, ...recipe.effects } }));
    }}><option value="" disabled>Choose a starting voice</option>{recipes.map((recipe, index) => <option value={index} key={recipe.name}>{recipe.name}</option>)}</select></Field>
      <Button disabled={disabled} onClick={() => update(current => ({ ...defaultSettings, gainDb: current.gainDb, muted: current.muted }))}>Reset effects</Button>
      <Toggle label="Bypass effects" checked={fx.bypass} onChange={value => set('bypass', value)} disabled={disabled} /></div>
    <div className="voice-tuning">
      <Field label={<span>Pitch <strong>{settings.pitchSemitones.toFixed(1)} semitones</strong></span>}><input aria-label="Voice pitch in semitones" type="range" min="-12" max="12" step="0.1" value={settings.pitchSemitones} disabled={disabled}
        onChange={event => update(current => ({ ...current, pitchSemitones: Number(event.target.value) }))} /></Field>
      {slider('formantSemitones', 'Timbre', -12, 12, .1, 'semitones')}
      {slider('formantStrength', 'Timbre strength')}
      {slider('mix', 'Effects mix')}
      <Field label={<span>Output gain <strong>{settings.gainDb.toFixed(1)} dB</strong></span>}><input aria-label="Voice output gain in decibels" type="range" min="-18" max="12" step="0.5" value={settings.gainDb} disabled={disabled}
        onChange={event => update(current => ({ ...current, gainDb: Number(event.target.value) }))} /></Field>
    </div>
    <details className="voice-effect-group"><summary>Equalizer</summary>
      {(['preEqDb', 'postEqDb'] as const).map(key => <div key={key}><div className="voice-eq-heading"><h3>{key === 'preEqDb' ? 'Before pitch and timbre' : 'After voice effects'}</h3>
        <Button variant="ghost" disabled={disabled} onClick={() => set(key, Array(10).fill(0))}>Reset {key === 'preEqDb' ? 'pre' : 'post'} EQ</Button></div>
        <div className="voice-equalizer">{['31', '62', '125', '250', '500', '1k', '2k', '4k', '8k', '16k'].map((band, index) => <Field key={band} label={`${band} Hz · ${fx[key][index]} dB`}>
          <input type="range" min="-20" max="20" step="1" value={fx[key][index]} disabled={disabled} aria-label={`${key === 'preEqDb' ? 'Pre' : 'Post'} EQ ${band} Hz`}
            onChange={event => { const value = Number(event.target.value); update(current => ({ ...current, effects: { ...current.effects, [key]: current.effects[key].map((old, at) => at === index ? value : old) } })); }} /></Field>)}</div></div>)}
    </details>
    <details className="voice-effect-group"><summary>Microphone cleanup</summary><div className="voice-effect-grid">
      <div className="voice-effect-controls"><Toggle label="Noise gate" checked={fx.gateThresholdDb > -80} disabled={disabled} onChange={on => set('gateThresholdDb', on ? -45 : -80)} />
        {fx.gateThresholdDb > -80 && slider('gateThresholdDb', 'Gate threshold', -79, -10, 1, 'dB')}</div>
      <div className="voice-effect-controls"><Toggle label="Compressor" checked={fx.compressorThresholdDb < 0} disabled={disabled} onChange={on => set('compressorThresholdDb', on ? -18 : 0)} />
        {fx.compressorThresholdDb < 0 && <>{slider('compressorThresholdDb', 'Compressor threshold', -48, -1, 1, 'dB')}{slider('compressorRatio', 'Compression ratio', 1, 12, .5, ':1')}</>}</div>
      {effect('noiseReduction', 'Noise reduction', .5)}{effect('deEsser', 'De-esser', .5)}
      <div className="voice-effect-controls"><Toggle label="Low cut" checked={fx.lowCutHz > 0} disabled={disabled} onChange={on => set('lowCutHz', on ? 80 : 0)} />
        {fx.lowCutHz > 0 && slider('lowCutHz', 'Low cut frequency', 20, 400, 1, 'Hz')}</div>
      <Field label="Hum reduction"><select aria-label="Hum reduction" value={fx.humHz} disabled={disabled} onChange={event => set('humHz', Number(event.target.value))}>
        <option value="0">Off</option><option value="50">50 Hz</option><option value="60">60 Hz</option></select></Field>
    </div></details>
    <details className="voice-effect-group"><summary>Character and modulation</summary><div className="voice-effect-grid">
      {effect('robot', 'Robot', .8, slider('robotHz', 'Robot frequency', 20, 400, 1, 'Hz'))}
      {effect('harmony', 'Harmony', .5, slider('harmonySemitones', 'Harmony interval', -12, 12, 1, 'semitones'))}
      {effect('detune', 'Detune', .4)}{effect('growl', 'Growl', .5)}{effect('nasal', 'Nasal', .4)}{effect('breathy', 'Breathy', .3)}
      {effect('monotone', 'Monotone', .8)}{effect('hypertone', 'Expressive pitch', .8)}
      {effect('animal', 'Animal', .7, slider('animalDelayMs', 'Animal onset delay', 50, 1500, 10, 'ms'))}
      {effect('chorus', 'Chorus', .4, slider('chorusHz', 'Chorus rate', .1, 5, .1, 'Hz'))}{effect('phaser', 'Phaser', .5)}{effect('vibrato', 'Vibrato', .4)}
      {effect('tremolo', 'Tremolo', .5, slider('tremoloHz', 'Tremolo rate', .1, 20, .1, 'Hz'))}
      {effect('gapper', 'Gapper', .8, slider('gapperHz', 'Gapper rate', .5, 30, .5, 'Hz'))}{effect('underwater', 'Underwater', .8)}
    </div></details>
    <details className="voice-effect-group"><summary>Distortion and space</summary><div className="voice-effect-grid">
      {effect('distortion', 'Distortion', .4, slider('drive', 'Distortion drive', 1, 20, .5, ''))}
      {effect('diode', 'Diode', .6, <Toggle label="Octave-up mode" checked={fx.octaveUp} disabled={disabled} onChange={on => set('octaveUp', on)} />)}
      <div className="voice-effect-controls">{slider('bitDepth', 'Bit depth', 4, 16, 1, 'bits')}{slider('downsampleHz', 'Resample rate', 2000, 48000, 1000, 'Hz')}</div>
      {effect('echo', 'Echo', .3, <>{slider('echoMs', 'Echo delay', 30, 1000, 10, 'ms')}{slider('echoFeedback', 'Echo feedback', 0, .85, .01)}</>)}
      {effect('reverb', 'Reverb', .4, slider('reverbSize', 'Reverb room size'))}
    </div></details>
  </div>;
}
