import { useEffect, useState } from 'react';
import { Select, SelectOption, Button, Card, Field, Notice, Toggle, useUnsavedChanges, type PageProps } from './ui';

const defaults = { enabled: false, primaryDeviceId: '', companionDeviceId: '', leftStick: 'primary', rightStick: 'companion', triggers: 'strongest', buttons: {} };
export function CoPilotInput({ state, status, command }: PageProps) {
  const saved = JSON.stringify({ ...defaults, ...state.coPilot });
  const [settings, setSettings] = useState<any>(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify(settings) !== baseline;
  useUnsavedChanges(dirty);
  useEffect(() => { if (!dirty) { setSettings(JSON.parse(saved)); setBaseline(saved); } }, [saved]);
  const devices = status.devices || state.devices || [], locked = busy || status.training || status.recording?.isRecording || !!status.recording?.pending;
  const update = (key: string, value: unknown) => { setSettings((current: any) => ({ ...current, [key]: value })); setMessage(''); };
  const valid = !settings.enabled || (settings.primaryDeviceId && settings.companionDeviceId && settings.primaryDeviceId !== settings.companionDeviceId);
  return <Card title="Co-pilot controllers" description="Combine two verified physical controllers into one output. Preferences are remembered; merging starts only when you enable and save it.">
    <div className="stack"><Toggle label="Enable co-pilot merging" checked={settings.enabled} disabled={locked} onChange={value => update('enabled', value)} />
      <p className="subtle">Shared buttons stay pressed while either participant holds them. Opposite D-pad directions cancel. A stick always comes from one participant; strongest wins by magnitude, with primary winning ties. Triggers use the larger value in strongest mode. Elite paddles, navigation and rumble belong to primary.</p>
      <div className="form-grid">{(['primary', 'companion'] as const).map(role => <Field key={role} label={role === 'primary' ? 'Primary controller' : 'Companion controller'}>
        <Select value={settings[role + 'DeviceId']} disabled={locked} onValueChange={selectionValue => update(role + 'DeviceId', selectionValue)}>
          <SelectOption value="">Choose a physical controller</SelectOption>
          {settings[role + 'DeviceId'] && !devices.some((device: any) => device.id === settings[role + 'DeviceId']) && <SelectOption value={settings[role + 'DeviceId']}>Saved controller unavailable</SelectOption>}
          {devices.map((device: any) => <SelectOption key={device.id} value={device.id}>{device.name}</SelectOption>)}
        </Select></Field>)}
        {(['leftStick', 'rightStick', 'triggers'] as const).map(control => <Field key={control} label={control === 'leftStick' ? 'Left stick owner' : control === 'rightStick' ? 'Right stick owner' : 'Trigger owner'}>
          <Select value={settings[control]} disabled={locked} onValueChange={selectionValue => update(control, selectionValue)}>
            <SelectOption value="primary">Primary</SelectOption><SelectOption value="companion">Companion</SelectOption><SelectOption value="strongest">Strongest</SelectOption>
          </Select></Field>)}
      </div>
      <details><summary>Button ownership</summary><div className="form-grid">{(state.controls || []).filter((control: any) => !/^(small|large)/.test(control.id) && !/Trigger$/.test(control.id)).map((control: any) =>
        <Field key={control.id} label={control.label + ' owner'}><Select value={settings.buttons[control.id] || 'shared'} disabled={locked} onValueChange={selectionValue => {
          const buttons = { ...settings.buttons }; if (selectionValue === 'shared') delete buttons[control.id]; else buttons[control.id] = selectionValue; update('buttons', buttons);
        }}><SelectOption value="shared">Shared</SelectOption><SelectOption value="primary">Primary</SelectOption><SelectOption value="companion">Companion</SelectOption></Select></Field>)}</div></details>
      {status.coPilot?.enabled && <Notice announce={false}>Primary: {status.coPilot.primaryConnected ? 'connected' : 'unavailable'}. Companion: {status.coPilot.companionConnected ? 'connected' : 'unavailable'}.</Notice>}
      <p className="subtle">Saving pauses sequences, disarms vision and requires neutral controls. Disconnecting a participant clears synthetic holds and sequences; the other's physical holds continue under the chosen ownership rules. Recordings capture merged physical input. Stop everything disconnects output; release all controls before reconnecting.</p>
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
      <div className="row"><Button variant="primary" busy={busy} disabled={!dirty || locked || !valid} onClick={async () => {
        const submitted = structuredClone(settings); setBusy(true); setError('');
        try { await command('coPilotSave', { settings: submitted }); setBaseline(JSON.stringify(submitted)); setMessage(submitted.enabled ? 'Co-pilot saved. Release controls before continuing.' : 'Co-pilot disabled.'); }
        catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
      }}>Save co-pilot preferences</Button><Button disabled={!dirty || locked} onClick={() => { setSettings(JSON.parse(saved)); setBaseline(saved); setError(''); }}>Discard co-pilot edits</Button></div>
    </div>
  </Card>;
}
