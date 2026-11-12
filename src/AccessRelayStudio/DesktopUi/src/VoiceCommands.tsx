import { useEffect, useState } from 'react';
import { Button, Card, Field, Notice, useUnsavedChanges, type PageProps } from './ui';

const defaults = { schemaVersion: 1, recognizerId: '', minimumRms: .015, bindings: [] };
const controls = ['a', 'b', 'x', 'y', 'leftBumper', 'rightBumper', 'leftTrigger', 'rightTrigger', 'leftStick', 'rightStick', 'dPadUp', 'dPadDown', 'dPadLeft', 'dPadRight', 'menu', 'view'];
const label = (value: string) => ({ a: 'A', b: 'B', x: 'X', y: 'Y' }[value] || value.replace(/([A-Z])/g, ' $1'));
export function VoiceCommands({ state, status, command }: PageProps) {
  const saved = JSON.stringify(state.commands?.settings || defaults), profile = state.commands?.profileId || '';
  const [settings, setSettings] = useState<any>(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [microphone, setMicrophone] = useState(state.microphoneId || ''), [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify(settings) !== baseline;
  const recognition = status.commands?.recognition, output = status.commands?.output;
  const running = recognition?.running && !recognition?.learning && output?.enabled;
  useUnsavedChanges(dirty);
  useEffect(() => { setSettings(JSON.parse(saved)); setBaseline(saved); setError(''); }, [profile]);
  useEffect(() => { if (!dirty) { setSettings(JSON.parse(saved)); setBaseline(saved); } }, [saved]);
  const update = (id: string, patch: any) => setSettings((value: any) => ({ ...value, bindings: value.bindings.map((binding: any) => binding.id === id ? { ...binding, ...patch } : binding) }));
  const execute = async (action: string, args?: any) => {
    setBusy(action); setError(''); setMessage('');
    try { const result = await command(action, args); if (result?.message) setMessage(result.message); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; } finally { setBusy(''); }
  };
  return <div className="page-stack"><Card title="Local voice commands" description={'Commands belong to controller setup “' + (state.commands?.profileName || 'the active setup') + '”. Listening starts only when you explicitly enable it.'}>
    <div className="stack"><p className="subtle">Use fixed phrases or record up to three examples of a personal sound, such as a hum or vowel. Sound matching is experimental and personalized; its score is not an accuracy guarantee. Test background game speech and noise before relying on a command. Live audio stays on this PC.</p>
      <div className="form-grid"><Field label="Command microphone"><select value={microphone} disabled={!!busy || !!recognition?.running} onChange={event => setMicrophone(event.target.value)}>
        <option value="">Choose a physical microphone</option>{(state.devices?.microphones || []).map((device: any) => <option key={device.id} value={device.id}>{device.name}</option>)}
      </select></Field><Field label="Local phrase recognizer"><select value={settings.recognizerId} disabled={!!busy || !!running} onChange={event => setSettings({ ...settings, recognizerId: event.target.value })}>
        <option value="">Personal sounds only</option>{(state.commands?.engines || []).map((engine: any) => <option key={engine.id} value={engine.id}>{engine.name} ({engine.culture})</option>)}
      </select></Field><Field label="Sound activation level" hint="Raise this to ignore quiet background noise; lowering it can help soft vocalizations."><input type="range" min="1" max="100" step="1" value={Math.round(settings.minimumRms * 1000)} disabled={!!busy || !!running} onChange={event => setSettings({ ...settings, minimumRms: Number(event.target.value) / 1000 })} /></Field></div>
      {!state.commands?.engines?.length && <Notice announce={false}>No Windows desktop speech engine is available. Personal sound templates can still be used. Install a supported Windows speech language to use phrases.</Notice>}
      {settings.bindings.map((binding: any, index: number) => <fieldset className="stack" key={binding.id}><legend>Command {index + 1}</legend><div className="form-grid">
        <Field label={'Command ' + (index + 1) + ' name'}><input maxLength={80} value={binding.name} disabled={!!busy || !!running} onChange={event => update(binding.id, { name: event.target.value })} /></Field>
        <Field label={'Command ' + (index + 1) + ' phrase'} hint="Leave blank for personal sounds only."><input maxLength={80} value={binding.phrase} disabled={!!busy || !!running} onChange={event => update(binding.id, { phrase: event.target.value })} /></Field>
        <Field label={'Command ' + (index + 1) + ' action'}><select value={binding.action} disabled={!!busy || !!running} onChange={event => update(binding.id, { action: event.target.value })}><option value="pulse">Brief button press</option><option value="sequence">Run this control's sequence</option></select></Field>
        <Field label={'Command ' + (index + 1) + ' control'}><select value={binding.control} disabled={!!busy || !!running} onChange={event => update(binding.id, { control: event.target.value })}>{[...controls, ...(binding.action === 'sequence' ? ['smallLeft', 'smallRight', 'largeLeft', 'largeRight'] : [])].map(control => <option key={control} value={control}>{label(control)}</option>)}</select></Field>
        <Field label={'Command ' + (index + 1) + ' duration (ms)'}><input type="number" min={20} max={500} value={binding.durationMs} disabled={!!busy || !!running || binding.action !== 'pulse'} onChange={event => update(binding.id, { durationMs: Number(event.target.value) })} /></Field>
        <Field label={'Command ' + (index + 1) + ' minimum score'}><input type="number" min={.5} max={1} step={.01} value={binding.minimumConfidence} disabled={!!busy || !!running} onChange={event => update(binding.id, { minimumConfidence: Number(event.target.value) })} /></Field>
        <Field label={'Command ' + (index + 1) + ' repeat delay (ms)'}><input type="number" min={200} max={5000} value={binding.refractoryMs} disabled={!!busy || !!running} onChange={event => update(binding.id, { refractoryMs: Number(event.target.value) })} /></Field>
      </div><p className="subtle">{binding.templates.length} of 3 personal sound examples saved. Recording waits up to 10 seconds for one 60–1500 ms sound, followed by a short silence.</p>
        <div className="row"><Button disabled={dirty || !microphone || !!busy || !!running} onClick={() => void execute('commandLearn', { id: binding.id, microphoneId: microphone })}>Record sound for command {index + 1}</Button>
          <Button disabled={!!busy || !!running || binding.templates.length === 0} onClick={() => update(binding.id, { templates: [] })}>Clear examples for command {index + 1}</Button>
          <Button disabled={!!busy || !!running} onClick={() => setSettings({ ...settings, bindings: settings.bindings.filter((item: any) => item.id !== binding.id) })}>Remove command {index + 1}</Button></div>
      </fieldset>)}
      <Button disabled={!!busy || !!running || settings.bindings.length >= 24} onClick={() => setSettings({ ...settings, bindings: [...settings.bindings, { id: crypto.randomUUID(), name: 'New command', phrase: '', control: 'a', action: 'pulse', durationMs: 100, minimumConfidence: .95, refractoryMs: 750, templates: [] }] })}>Add voice command</Button>
      <p className="subtle">Connect controller input and output before listening. Enable sequences first if a command runs one. Listening disarms vision and prevents macro/gameplay recording. Stop, controller/setup changes, navigation and sequence setting changes cancel listening; enable it again explicitly. Personal sound recording sends no controller actions.</p>
      {(error || recognition?.error || state.commands?.error) && <Notice tone="danger" announce={false}>{error || recognition?.error || state.commands?.error}</Notice>}
      {message && <Notice tone="success">{message}</Notice>}
      <div role="status" aria-live="polite">{recognition?.running && recognition?.ready === false ? 'Starting local commands…' : recognition?.learning ? 'Recording a personal sound…' : running ? 'Local commands are listening.' : 'Local commands are off.'}</div>
      {output?.lastCommand && <p className="subtle">Last composed command: {output.lastCommand}. Onset to host: {output.onsetToHostMs?.toFixed(0)} ms. Sound end to host: {output.endToHostMs?.toFixed(0)} ms. Console receipt is not measured.</p>}
      <div className="row"><Button variant="primary" disabled={!dirty || !!busy || !!running} onClick={async () => { if (await execute('commandsSave', { settings })) { setBaseline(JSON.stringify(settings)); setMessage('Voice commands saved. Listening remains off.'); } }}>Save voice commands</Button>
        <Button disabled={dirty || !!busy || running || !microphone || settings.bindings.length === 0} onClick={() => void execute('commandsStart', { microphoneId: microphone })}>Start command listening</Button>
        <Button disabled={!recognition?.running && !output?.enabled && !busy} onClick={() => void command('commandsStop').catch(e => setError(String(e)))}>Stop commands and recording</Button>
        <Button disabled={!dirty || !!busy || !!running} onClick={() => { setSettings(JSON.parse(saved)); setBaseline(saved); }}>Discard command edits</Button></div>
    </div></Card></div>;
}
