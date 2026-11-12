import { useCallback, useEffect, useRef, useState } from 'react';
import { isDesignPreview } from '../api';
import { CoPilotInput } from '../CoPilotInput';
import { GamePacks } from '../GamePacks';
import { currentTheme, themeChanged } from '../theme';
import { ActionMenu, ActionMenuItem, Badge, Button, Card, ConfirmDialog, DiscardDialog, EmptyState, Field, Icon, Meter, Modal, Notice, SectionHeader, Tabs, Toggle, formatDuration, formatMs, hasUnsavedChanges, humanize, useUnsavedChanges, type PageProps } from '../ui';

const INITIAL_TIMING = { startDelay: 0, downSweep: 0, downHold: 512, upSweep: 80, upHold: 48, centerHold: 120, flick: 64, settle: 240, flickX: 0, flickY: -32767 };
const controlGlyph = (control: string) => ({ a: 'A', b: 'B', x: 'X', y: 'Y', leftBumper: 'LB', rightBumper: 'RB',
  leftTrigger: 'LT', rightTrigger: 'RT', leftStick: 'LS', rightStick: 'RS', smallLeft: 'P1', smallRight: 'P2', largeLeft: 'P3', largeRight: 'P4',
  dPadUp: '↑', dPadDown: '↓', dPadLeft: '←', dPadRight: '→', menu: 'M', view: 'V' } as Record<string, string>)[control] || control.slice(0, 2).toUpperCase();
const lower = (value: string) => value ? value[0].toLowerCase() + value.slice(1) : '';
const upper = (value: string) => value ? value[0].toUpperCase() + value.slice(1) : '';
const suggestedModel = (state: any, status: any) => {
  const devices = status.devices || state.devices || [], id = status.selectedDeviceId || state.selectedDeviceId;
  const device = devices.find((item: any) => item.id === id), name = String(device?.name || status.deviceName || '').toLowerCase();
  if (device?.isElite || name.includes('elite')) return 'elite-series-2';
  if (name.includes('dualsense') || device?.productId === 0x0ce6 || device?.productId === 0x0df2) return 'dualsense';
  if (name.includes('dualshock') || device?.vendorId === 0x054c) return 'dualshock-4';
  if (name.includes('switch') || device?.vendorId === 0x057e) return 'switch-pro';
  return device ? 'xbox-one' : 'elite-series-2';
};
const delayFields: [string, string, number][] = [
  ['startDelay', 'Before movement', 0], ['downSweep', 'Move down', 0], ['downHold', 'Hold down', 1], ['upSweep', 'Move up', 4],
  ['upHold', 'Hold up', 1], ['centerHold', 'Return to center', 1], ['flick', 'Flick duration', 1], ['settle', 'Settle time', 1]
];

export function ControllerPage({ state, status, command }: PageProps) {
  const [tab, setTab] = useState('mapping'), [profileAction, setProfileAction] = useState(''), [profileName, setProfileName] = useState('');
  const [profileBusy, setProfileBusy] = useState(false), [deleteProfile, setDeleteProfile] = useState(false), [error, setError] = useState(''), [busy, setBusy] = useState(''), [originalProfileName, setOriginalProfileName] = useState('');
  useUnsavedChanges(!!profileAction && profileName !== originalProfileName);
  const [nextAction, setNextAction] = useState<(() => void) | null>(null);
  const transition = (action: () => void) => { if (hasUnsavedChanges()) setNextAction(() => action); else action(); };
  const profile = state.activeProfile || {}, live = status || state.status || {}, devices = live.devices || state.devices || [];
  const act = async (action: string, args?: any) => { setBusy(action); setError(''); try { return await command(action, args); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); throw e; } finally { setBusy(''); } };
  const perform = (action: string, args?: any) => { void act(action, args).catch(() => {}); };
  const nameAction = (action: string) => { const value = action === 'profileCreate' ? '' : profile.name + (action === 'profileDuplicate' ? ' copy' : ''); setProfileName(value); setOriginalProfileName(value); setProfileAction(action); };
  const saveName = async () => { if (!profileName.trim()) return; setProfileBusy(true);
    try { await act(profileAction, { name: profileName.trim() }); setProfileAction(''); } catch {} finally { setProfileBusy(false); } };
  return <div className="page-stack">
    <SectionHeader eyebrow="Your controller workspace" title="Make the controls yours" description="Build a setup that feels right, then take it into your game."
      actions={<div className="row"><Field label="Controller setup"><select value={profile.id || ''} onChange={event => { const id = event.target.value; transition(() => perform('profileSelect', { id })); }}>
        {(state.profiles || []).map((item: any) => <option value={item.id} key={item.id}>{item.name}</option>)}</select></Field>
        <Button className="icon-button" title="Create a controller setup" aria-label="Create a controller setup" onClick={() => transition(() => nameAction('profileCreate'))}><Icon name="plus" /></Button></div>} />
    {error && <Notice tone="danger">{error}</Notice>}
    <div className="controller-topline"><div className="controller-device"><span className="device-icon"><Icon name="gamepad" size={25} /></span>
      <select value={live.selectedDeviceId ?? state.selectedDeviceId ?? ''} disabled={!!live.coPilot?.enabled} aria-label="Input controller" onChange={event => perform('deviceSelect', { id: event.target.value })}>
        <option value="">Choose a controller</option>{live.coPilot?.enabled && !devices.some((device: any) => device.id === live.selectedDeviceId) && <option value={live.selectedDeviceId}>Primary controller unavailable</option>}{devices.map((device: any) => <option key={device.id} value={device.id}>{device.name}</option>)}</select>
      <Button variant="ghost" className="icon-button" busy={busy === 'deviceRefresh'} title="Refresh controllers" aria-label="Refresh controllers" onClick={() => perform('deviceRefresh')}><Icon name="refresh" size={18} /></Button></div>
      <Badge tone={live.connected ? 'success' : 'neutral'}>{live.connected ? 'Input connected' : 'No input'}</Badge><div className="split-divider" />
      <Toggle label="Sequences" checked={!!live.scriptsEnabled} disabled={!live.connected || !!live.training || !!live.recording?.isRecording} onChange={enabled => perform('scriptsSet', { enabled })} />
      <Button variant={live.outputActive ? 'secondary' : 'primary'} busy={busy === 'outputStart' || !!live.isStarting} disabled={!live.outputActive && !live.connected}
        onClick={() => perform(live.outputActive ? 'outputStop' : 'outputStart')}><Icon name="power" size={17} />{live.outputActive ? 'Disconnect output' : 'Connect output'}</Button>
    </div>
    {!state.driverAvailable && <Notice tone="warning" title="One-time device setup">Open Settings to install the virtual device driver before connecting output.</Notice>}
    {live.training && <Notice tone="warning">Gameplay training is recording manual input. Finish that clip before changing controller setups or sequences.</Notice>}
    <Tabs value={tab} onChange={next => { if (next !== tab) transition(() => setTab(next)); }} ariaLabel="Controller workspace" items={[{ id: 'mapping', label: 'Button mapping' }, { id: 'sequences', label: 'Sequences', count: profile.recordings?.length || 0 }, { id: 'lua', label: 'Lua scripts', count: profile.luaScripts?.length || 0 }, { id: 'packs', label: 'Game packs' }, { id: 'input', label: 'Input & response' }, { id: 'accessibility', label: 'Accessibility' }]} />
    {tab === 'packs' && <GamePacks state={state} status={live} command={command} />}
    {tab === 'mapping' && <MappingEditor state={state} status={live} command={command} openSequences={() => transition(() => setTab('sequences'))} />}
    {tab === 'sequences' && <Sequences state={state} status={live} command={command} />}
    {tab === 'lua' && <LuaEditor state={state} status={live} command={command} />}
    {tab === 'input' && <InputResponse state={state} status={live} command={command} />}
    {tab === 'accessibility' && <div className="page-stack"><StickyInput key={'sticky-' + profile.id} state={state} status={live} command={command} />
      <SmoothingInput key={'smoothing-' + profile.id} state={state} status={live} command={command} />
      <FeedbackInput state={state} status={live} command={command} /><CoPilotInput state={state} status={live} command={command} /></div>}
    <details className="details-section"><summary>Manage controller setups</summary><div className="row"><Button onClick={() => nameAction('profileRename')}><Icon name="pencil" size={16} />Rename</Button>
      <Button onClick={() => transition(() => nameAction('profileDuplicate'))}><Icon name="copy" size={16} />Duplicate</Button><Button onClick={() => transition(() => perform('profileImport'))}><Icon name="upload" size={16} />Import setup</Button>
      <Button onClick={() => perform('profileExport')}><Icon name="download" size={16} />Export setup</Button><Button variant="ghost" disabled={(state.profiles || []).length < 2} onClick={() => setDeleteProfile(true)}><Icon name="trash" size={16} />Delete setup</Button></div></details>
    <Modal open={!!profileAction} onClose={() => { if (!profileBusy) setProfileAction(''); }} title={profileAction === 'profileCreate' ? 'A setup of your own' : profileAction === 'profileDuplicate' ? 'Duplicate this setup' : 'Rename your setup'}
      description="Give it a name you will recognize when you come back." footer={<><Button disabled={profileBusy} onClick={() => setProfileAction('')}>Cancel</Button><Button variant="primary" busy={profileBusy} disabled={!profileName.trim()} onClick={() => void saveName()}>Save setup</Button></>}>
      <Field label="Setup name"><input autoFocus maxLength={80} value={profileName} onChange={event => setProfileName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void saveName(); }} /></Field></Modal>
    <ConfirmDialog open={deleteProfile} onClose={() => setDeleteProfile(false)} title="Delete this controller setup?" description={'“' + profile.name + '” and its saved mappings, sequences and Lua scripts will be removed.'}
      onConfirm={() => command('profileDelete', { id: profile.id })} confirmLabel="Delete setup" />
    <DiscardDialog open={nextAction !== null} onKeep={() => setNextAction(null)} onDiscard={() => { const action = nextAction; setNextAction(null); action?.(); }} />
  </div>;
}

function StickyInput({ state, status, command }: PageProps) {
  const saved = JSON.stringify([...(state.activeProfile?.stickyControls || [])].sort());
  const [selected, setSelected] = useState<string[]>(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify([...selected].sort()) !== baseline;
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (!dirty) { setSelected(JSON.parse(saved)); setBaseline(saved); }
  }, [saved]);
  const locked = busy || status.training || status.recording?.isRecording || !!status.recording?.pending;
  return <Card title="Sticky buttons" description="Press once to hold a control, then press again to release it. These choices are saved with this controller setup.">
    <div className="stack">
      <p className="subtle">Choose buttons or triggers below. Selected triggers switch between fully pressed and released. Paddles keep their usual mappings.</p>
      <p className="subtle">Stop everything, a disconnection, or changing setups clears held controls. Release a control before pressing it again. Sequences can override sticky output; recordings capture unmodified physical input. A sticky shoot button prevents arming vision assistance.</p>
      <div className="field-grid">{(state.controls || []).filter((control: any) => !/^(small|large)/.test(control.id)).map((control: any) =>
        <Toggle key={control.id} label={control.label} checked={selected.includes(control.id)} disabled={locked} onChange={checked => {
          setSelected(value => checked ? [...value, control.id] : value.filter(id => id !== control.id)); setMessage('');
        }} />)}</div>
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
      <div className="row"><Button variant="primary" busy={busy} disabled={!dirty || locked} onClick={async () => {
        const controls = [...selected].sort(); setBusy(true); setError('');
        try { await command('stickySave', { controls }); setBaseline(JSON.stringify(controls)); setMessage('Sticky controls saved. Release the controls before pressing to latch.'); }
        catch (e) { setError(e instanceof Error ? e.message : String(e)); }
        finally { setBusy(false); }
      }}>Save sticky controls</Button><Button disabled={!dirty || locked} onClick={() => { setSelected(JSON.parse(saved)); setBaseline(saved); setError(''); }}>Discard edits</Button></div>
    </div>
  </Card>;
}

function SmoothingInput({ state, status, command }: PageProps) {
  const saved = JSON.stringify(state.activeProfile?.smoothing || { leftTimeConstantMs: 0, rightTimeConstantMs: 0 });
  const [settings, setSettings] = useState(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify(settings) !== baseline;
  useUnsavedChanges(dirty);
  useEffect(() => { if (!dirty) { setSettings(JSON.parse(saved)); setBaseline(saved); } }, [saved]);
  const locked = busy || status.training || status.recording?.isRecording || !!status.recording?.pending;
  return <Card title="Tremor smoothing" description="Soften small changes in manual stick movement. Both sticks are unfiltered by default.">
    <div className="stack"><p className="subtle">Higher values smooth more and add response delay. At the selected time constant, output covers about 63% of a sudden movement; allow about three times that value to reach 95%. Returning to the deadzone is immediate. Sequences and recordings are not smoothed.</p>
      <div className="field-grid">{(['left', 'right'] as const).map(side => <Field key={side} label={side === 'left' ? 'Left stick smoothing' : 'Right stick smoothing'}
        hint={settings[side + 'TimeConstantMs'] === 0 ? 'Off' : settings[side + 'TimeConstantMs'] + ' ms time constant'}>
        <input type="range" min={0} max={100} step={1} value={settings[side + 'TimeConstantMs']} disabled={locked}
          onChange={event => { setSettings((value: any) => ({ ...value, [side + 'TimeConstantMs']: Number(event.target.value) })); setMessage(''); }} />
      </Field>)}</div>
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
      <div className="row"><Button variant="primary" busy={busy} disabled={!dirty || locked} onClick={async () => {
        const submitted = { ...settings }; setBusy(true); setError('');
        try { await command('smoothingSave', { settings: submitted }); setBaseline(JSON.stringify(submitted)); setMessage('Stick smoothing saved.'); }
        catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
      }}>Save smoothing</Button><Button disabled={!dirty || locked} onClick={() => { setSettings(JSON.parse(saved)); setBaseline(saved); setError(''); }}>Discard smoothing edits</Button></div>
    </div>
  </Card>;
}

function FeedbackInput({ state, status, command }: PageProps) {
  const saved = JSON.stringify(state.feedback || { audioEnabled: false, hapticsEnabled: false, volume: .2, strength: .15 });
  const [settings, setSettings] = useState(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify(settings) !== baseline;
  useUnsavedChanges(dirty);
  useEffect(() => { if (!dirty) { setSettings(JSON.parse(saved)); setBaseline(saved); } }, [saved]);
  return <Card title="Nonvisual feedback" description="Optional sounds and controller pulses for profile changes, player lock and completed vision releases. Preferences apply to all controller setups.">
    <div className="stack">
      <Toggle label="Audio cues" description="Play short tones through the Windows default playback device." checked={settings.audioEnabled} disabled={busy} onChange={audioEnabled => setSettings((value: any) => ({ ...value, audioEnabled }))} />
      <Field label="Cue volume" hint={Math.round(settings.volume * 100) + '%'}><input type="range" min={0} max={100} step={1} disabled={busy} value={settings.volume * 100} onChange={event => setSettings((value: any) => ({ ...value, volume: Number(event.target.value) / 100 }))} /></Field>
      <Toggle label="Controller pulses" description="Use the selected physical gamepad's supported rumble motors. Test before relying on this cue." checked={settings.hapticsEnabled} disabled={busy} onChange={hapticsEnabled => setSettings((value: any) => ({ ...value, hapticsEnabled }))} />
      <Field label="Pulse intensity" hint={Math.round(settings.strength * 100) + '%'}><input type="range" min={0} max={50} step={1} disabled={busy} value={settings.strength * 100} onChange={event => setSettings((value: any) => ({ ...value, strength: Number(event.target.value) / 100 }))} /></Field>
      <p className="subtle">Profile changes use two ascending tones, player lock a short tone, and release a higher pair. Pulses last 40–120 ms. Stop everything, context changes and saving preferences cancel pending cues. Delivery timing depends on the audio/controller device.</p>
      {status.feedback?.error && <Notice tone="danger" announce={false}>{status.feedback.error}</Notice>}
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice>{message}</Notice>}
      <div className="row"><Button variant="primary" busy={busy} disabled={!dirty} onClick={async () => {
        const submitted = { ...settings }; setBusy(true); setError('');
        try { await command('feedbackSave', { settings: submitted }); setBaseline(JSON.stringify(submitted)); setMessage('Feedback preferences saved.'); }
        catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
      }}>Save feedback preferences</Button>
      <Button disabled={busy || dirty || (!settings.audioEnabled && !settings.hapticsEnabled)} onClick={async () => {
        setBusy(true); setError('');
        try { const result = await command('feedbackTest'); setMessage(result?.message || (result?.queued ? 'Test cue queued. Listen or check the selected controller.' : 'The test cue is disabled or rate-limited.')); }
        catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
      }}>Test feedback</Button>
      <Button disabled={!dirty || busy} onClick={() => { setSettings(JSON.parse(saved)); setBaseline(saved); setError(''); }}>Discard feedback edits</Button></div>
    </div>
  </Card>;
}

function MappingEditor({ state, status, command, openSequences }: PageProps & { openSequences: () => void }) {
  const profile = state.activeProfile || {}, [selected, setSelected] = useState('largeRight');
  const binding = (profile.bindings || []).find((item: any) => item.control === selected) || { control: selected, label: humanize(selected), script: 'passthrough', enabled: true, consume: false };
  const [draft, setDraft] = useState<any>(binding), [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [error, setError] = useState('');
  useEffect(() => { setDraft(binding); setMessage(''); setError(''); }, [selected, profile.id, JSON.stringify(binding)]);
  const saved = JSON.stringify(draft) === JSON.stringify(binding);
  useUnsavedChanges(!saved);
  const [nextControl, setNextControl] = useState<string | null>(null);
  const chooseControl = (control: string) => { if (control === selected) return; if (!saved) setNextControl(control); else setSelected(control); };
  const selectedScript = draft.script === 'recorded' ? 'recorded:' + draft.recordingId : draft.script === 'lua' ? 'lua:' + draft.luaScriptId : draft.script;
  const groups = [{ title: 'Buttons & bumpers', controls: ['a', 'b', 'x', 'y', 'leftBumper', 'rightBumper', 'leftTrigger', 'rightTrigger'] },
    { title: 'Sticks & navigation', controls: ['leftStick', 'rightStick', 'dPadUp', 'dPadDown', 'dPadLeft', 'dPadRight', 'menu', 'view'] },
    { title: 'Elite paddles', controls: ['smallLeft', 'smallRight', 'largeLeft', 'largeRight'] }];
  const scriptName = (item: any) => item.script === 'recorded' ? profile.recordings?.find((record: any) => record.id === item.recordingId)?.name || 'Saved sequence'
    : item.script === 'lua' ? profile.luaScripts?.find((script: any) => script.id === item.luaScriptId)?.name || 'Lua script' : state.scripts?.find((script: any) => script.id === item.script)?.label || humanize(item.script);
  return <div className="controller-layout mapping-layout"><div className="stack">
    <ControllerVisual state={state} status={status} selected={selected} onSelect={chooseControl} />
    <Card title="Choose a control" description="Pick it on the controller or choose it below."><div className="binding-groups">{groups.map(group => <div key={group.title}><p className="binding-group-label">{group.title}</p><div className="binding-grid">{group.controls.map(control => {
      const item = (profile.bindings || []).find((item: any) => item.control === control); if (!item) return null;
      return <button type="button" key={control} className={'binding-item' + (selected === control ? ' selected' : '')} aria-label={item.label + ': ' + (item.enabled ? scriptName(item) : 'Disabled')} aria-pressed={selected === control} onClick={() => chooseControl(control)}>
        <span className="control-glyph">{controlGlyph(control)}</span><span className="binding-copy"><strong>{item.label}</strong><small>{item.enabled ? scriptName(item) : 'Disabled'}</small></span></button>;
    })}</div></div>)}</div></Card></div>
    <Card className="mapping-editor"><div className="editor-heading"><span className="control-glyph">{controlGlyph(selected)}</span><div><p className="eyebrow">Edit this control</p><h2>{binding.label}</h2></div></div><div className="stack">
      <Field label="What should it do?" hint={draft.script === 'passthrough' ? "Keep this control's usual behavior." : draft.script === 'recorded' ? 'Replay the selected saved recording.' : draft.script === 'lua' ? 'Run this script when the control is pressed.' : 'Use the sequence timing saved in this setup.'}><select value={selectedScript} disabled={status.training} onChange={event => { const [script, id] = event.target.value.split(':'); setDraft({ ...draft, script, recordingId: script === 'recorded' ? id : null, luaScriptId: script === 'lua' ? id : null }); setMessage(''); }}>
        {(state.scripts || []).filter((item: any) => !['recorded', 'lua'].includes(item.id)).map((item: any) => <option key={item.id} value={item.id}>{item.label}</option>)}
        {!!profile.recordings?.length && <optgroup label="Your recorded sequences">{profile.recordings.map((item: any) => <option key={item.id} value={'recorded:' + item.id}>{item.name}</option>)}</optgroup>}
        {!!profile.luaScripts?.length && <optgroup label="Your Lua scripts">{profile.luaScripts.map((item: any) => <option key={item.id} value={'lua:' + item.id}>{item.name}</option>)}</optgroup>}</select></Field>
      <Toggle label="Use this mapping" checked={draft.enabled !== false} disabled={status.training} onChange={enabled => setDraft({ ...draft, enabled })} />
      {!/^(small|large)/.test(selected) && draft.script !== 'passthrough' && <Toggle label="Replace the physical press" description="Run the sequence without also passing this button press to the game." checked={!!draft.consume} disabled={status.training} onChange={consume => setDraft({ ...draft, consume })} />}
      {draft.script !== 'passthrough' && <p className="subtle">Runs with <strong>Sequences</strong> enabled and controller output connected.</p>}
      {draft.script !== 'passthrough' && <Button variant="ghost" onClick={openSequences}><Icon name="sliders" size={17} />Edit sequence timing<Icon name="arrowRight" size={16} /></Button>}
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
      <div className="editor-actions"><span className="saved-note">{saved ? 'All changes saved' : 'Unsaved changes'}</span><Button disabled={saved || busy} onClick={() => setDraft(binding)}>Reset</Button>
        <Button variant="primary" busy={busy} disabled={saved || status.training} onClick={async () => { setBusy(true); setError(''); try { await command('bindingSave', { ...draft, control: selected }); setMessage('Mapping saved.'); } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); } }}><Icon name="check" size={17} />Save mapping</Button></div>
    </div></Card>
    <DiscardDialog open={nextControl !== null} onKeep={() => setNextControl(null)} onDiscard={() => { if (nextControl) setSelected(nextControl); setNextControl(null); }} />
  </div>;
}

function ControllerVisual({ state, status, selected, onSelect }: { state: any; status: any; selected: string; onSelect: (value: string) => void }) {
  const iframe = useRef<HTMLIFrameElement>(null), [model, setModel] = useState(() => suggestedModel(state, status)), [ready, setReady] = useState(false);
  const initialTheme = useRef(currentTheme());
  useEffect(() => {
    if (!ready) return;
    const syncTheme = () => iframe.current?.contentWindow?.postMessage({ channel: 'accessrelay-controller', payload: { type: 'theme', theme: currentTheme() } }, 'https://scene.accessrelay.test');
    syncTheme(); window.addEventListener(themeChanged, syncTheme);
    return () => window.removeEventListener(themeChanged, syncTheme);
  }, [ready]);
  const deviceId = status.selectedDeviceId || state.selectedDeviceId;
  useEffect(() => { setReady(false); setModel(suggestedModel(state, status)); }, [deviceId]);
  const bindings = state.activeProfile?.bindings || [];
  useEffect(() => { const message = (event: MessageEvent) => {
    if (event.origin !== 'https://scene.accessrelay.test' || event.source !== iframe.current?.contentWindow || event.data?.channel !== 'accessrelay-controller') return;
    if (event.data.payload?.type === 'ready') setReady(true); if (event.data.payload?.type === 'select') onSelect(lower(event.data.payload.control || ''));
  }; window.addEventListener('message', message); return () => window.removeEventListener('message', message); }, [onSelect]);
  useEffect(() => { if (!ready) return; let assigned = 0;
    for (const binding of bindings) if (binding.enabled && binding.script !== 'passthrough') assigned |= Number(state.controls?.find((item: any) => item.id === binding.control)?.mask || 0);
    iframe.current?.contentWindow?.postMessage({ channel: 'accessrelay-controller', payload: { type: 'state', ...(status.input || {}), controls: Number(status.visibleControls || status.input?.buttons || 0), selected: upper(selected), assigned, connected: !!status.connected } }, 'https://scene.accessrelay.test');
  }, [ready, status, selected, state.controls, bindings]);
  return <Card className="controller-visual" title="A closer look" description="Select a control to change its mapping."
    actions={<select className="model-select" aria-label="Controller model" value={model} onChange={event => { setReady(false); setModel(event.target.value); }}><option value="elite-series-2">Elite Series 2</option><option value="xbox-one">Xbox One</option><option value="dualsense">DualSense</option><option value="dualshock-4">DualShock 4</option><option value="switch-pro">Switch Pro</option></select>}>
    {!isDesignPreview ? <iframe ref={iframe} className="controller-scene" key={model} src={'https://scene.accessrelay.test/Viewer/index.html?model=' + model + '&theme=' + initialTheme.current} title="Interactive 3D controller. Select a control to edit its mapping." onLoad={() => setReady(true)} /> : <div className="controller-design-preview">
      <svg viewBox="0 0 600 310" role="img" aria-label="Controller design illustration"><defs><linearGradient id="controller-body" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#49695f" /><stop offset="1" stopColor="#253f3d" /></linearGradient></defs>
        <ellipse cx="300" cy="272" rx="190" ry="18" fill="#b5c8b6" opacity=".45" /><path d="M160 55C122 60 100 113 86 190C74 250 99 278 128 258L192 207H408L472 258C501 278 526 250 514 190C500 113 478 60 440 55C393 43 355 55 300 55S207 43 160 55Z" fill="url(#controller-body)" stroke="#57786b" strokeWidth="3" />
        <path d="M180 58L205 40H263L275 57M325 57L337 40H395L420 58" fill="#718c7c" stroke="#49675b" strokeWidth="4" />
        <circle cx="185" cy="119" r="38" fill="#1d3533" stroke="#779283" strokeWidth="3" /><circle cx="185" cy="119" r="24" fill="#496c5b" stroke="#8aab8e" strokeWidth="2" />
        <circle cx="350" cy="189" r="34" fill="#1d3533" stroke="#779283" strokeWidth="3" /><circle cx="350" cy="189" r="22" fill="#496c5b" stroke="#8aab8e" strokeWidth="2" />
        <path d="M228 166V182H211V198H228V215H244V198H261V182H244V166Z" fill="#9cae9c" stroke="#314b40" strokeWidth="2" />
        {[['Y',415,94,'#d5c78b'],['B',449,128,'#bc8c75'],['A',415,162,'#9cd29f'],['X',381,128,'#8cafa9']].map(([letter,x,y,fill]) => <g key={String(letter)}><circle cx={Number(x)} cy={Number(y)} r="16" fill={String(fill)} stroke="#29463a" strokeWidth="3" /><text x={Number(x)} y={Number(y)+5} textAnchor="middle" fontSize="15" fontFamily="Segoe UI" fill="#244137" fontWeight="650">{letter}</text></g>)}
        <circle cx="300" cy="99" r="14" fill="#98f9c3" /><path d="M292 92L308 106M308 92L292 106" stroke="#305947" strokeWidth="3" /><rect x="266" y="132" width="17" height="10" rx="3" fill="#8da895" /><rect x="317" y="132" width="17" height="10" rx="3" fill="#8da895" />
      </svg><span className="subtle">Interactive 3D is available in the installed app.</span></div>}
    <p className="subtle visual-hint"><Icon name="info" size={15} />Drag to turn the controller. Select any control to edit it.</p>
  </Card>;
}

// Sequence and input tools.
function Sequences({ state, status, command }: PageProps) {
  const [view,setView]=useState('saved'),[editId,setEditId]=useState<string|null>(null),[deleteItem,setDeleteItem]=useState<any>(null),[duplicateItem,setDuplicateItem]=useState<any>(null);
  const [name,setName]=useState(''),[busy,setBusy]=useState(false),[error,setError]=useState('');
  const records=state.activeProfile?.recordings||[];
  const [nextView, setNextView] = useState<string | null>(null);
  return <div className="stack"><div className="row-spread"><div><h2>Small sequences. Your own rhythm.</h2><p className="subtle">Keep a movement you recorded, or tune a built-in sequence.</p></div>
    <Button onClick={()=>void command('recordImport').catch(()=>{})}><Icon name="upload" size={16}/>Import sequence</Button></div>
    <Tabs value={view} onChange={next=>{if(next!==view){if(hasUnsavedChanges())setNextView(next);else setView(next);}}} ariaLabel="Sequence type" items={[{id:'saved',label:'Your recordings',count:records.length},{id:'builtIn',label:'Built-in sequences'}]}/>
    {view==='saved'?<div className="split-grid"><Card title="Your saved sequences" description="Choose one to inspect its trajectory and edit individual movements.">
      {!records.length?<EmptyState icon="waves" title="Keep a movement you like" description="Record from your controller, stop when you are done, and give the sequence a name."/>:
      <div className="sequence-list">{records.map((item:any)=><div className="sequence-item" key={item.id}><span className="sequence-icon"><Icon name="waves"/></span>
        <div><strong>{item.name}</strong><p>{formatDuration(item.durationMs)} · {item.frameCount} samples</p></div><Button variant="ghost" onClick={()=>setEditId(item.id)}>Edit<Icon name="arrowRight" size={16}/></Button>
        <ActionMenu label={'More actions for '+item.name}>
          <ActionMenuItem onSelect={()=>{setName(item.name+' copy');setDuplicateItem(item);}}><Icon name="copy" size={16}/>Duplicate</ActionMenuItem>
          <ActionMenuItem onSelect={()=>void command('recordExport',{id:item.id}).catch(()=>{})}><Icon name="download" size={16}/>Export</ActionMenuItem>
          <ActionMenuItem onSelect={()=>setDeleteItem(item)}><Icon name="trash" size={16}/>Delete</ActionMenuItem></ActionMenu></div>)}</div>}
    </Card><RecordPanel state={state} status={status} command={command}/></div>:<TimingEditor state={state} status={status} command={command}/>}
    {editId&&<RecordingEditor id={editId} command={command} onClose={()=>setEditId(null)}/>}
    <ConfirmDialog open={!!deleteItem} onClose={()=>setDeleteItem(null)} title="Delete this sequence?" description={'“'+deleteItem?.name+'” will be removed. Its mappings will be cleared.'}
      onConfirm={()=>command('recordDelete',{id:deleteItem.id})} confirmLabel="Delete sequence"/>
    <Modal open={!!duplicateItem} onClose={()=>setDuplicateItem(null)} title="Make another version" description="The original sequence stays as it is."
      footer={<><Button onClick={()=>setDuplicateItem(null)}>Cancel</Button><Button variant="primary" busy={busy} disabled={!name.trim()} onClick={async()=>{setBusy(true);setError('');try{await command('recordDuplicate',{id:duplicateItem.id,name:name.trim()});setDuplicateItem(null);}catch(e){setError(String(e));}finally{setBusy(false);}}}>Duplicate</Button></>}>
      <Field label="New sequence name"><input value={name} maxLength={80} onChange={event=>setName(event.target.value)}/></Field>{error&&<Notice tone="danger">{error}</Notice>}</Modal>
    <DiscardDialog open={nextView!==null} onKeep={()=>setNextView(null)} onDiscard={()=>{if(nextView)setView(nextView);setNextView(null);}}/>
  </div>;
}

function RecordPanel({state,status,command}:PageProps){
  const [channels,setChannels]=useState(['rightStick']),[name,setName]=useState(''),[error,setError]=useState(''),[busy,setBusy]=useState('');
  const capture=status.recording||{},pending=capture.pending;
  const options=[{id:'rightStick',label:'Right stick'},{id:'leftStick',label:'Left stick'},{id:'buttons',label:'Buttons & bumpers'},{id:'leftTrigger',label:'Left trigger'},{id:'rightTrigger',label:'Right trigger'}];
  const run=async(action:string,args?:any)=>{setBusy(action);setError('');try{await command(action,args);if(action==='recordSave')setName('');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}};
  return <section className="capture-panel"><div className="capture-head"><div><p className="eyebrow">Make a new recording</p><h2>Capture your movement</h2></div><Icon name="radio" size={24}/></div>
    <p>Choose the controls to keep. Everything else stays out of the saved sequence.</p>
    <div className="checkbox-grid">{options.map(option=><label key={option.id} className="check-row"><input type="checkbox" disabled={capture.isRecording||!!pending} checked={channels.includes(option.id)}
      onChange={event=>setChannels(event.target.checked?[...channels,option.id]:channels.filter(id=>id!==option.id))}/>{option.label}</label>)}</div>
    <details><summary>Choose individual axes or buttons</summary><div className="checkbox-grid">{(state.recordingChannels||[]).filter((item:any)=>!['all','buttons','rightStick','leftStick','leftTrigger','rightTrigger'].includes(item.id))
      .map((option:any)=><label key={option.id} className="check-row"><input type="checkbox" disabled={capture.isRecording||!!pending} checked={channels.includes(option.id)}
        onChange={event=>setChannels(event.target.checked?[...channels,option.id]:channels.filter(id=>id!==option.id))}/>{option.label}</label>)}</div></details>
    {capture.isRecording?<div className="stack"><div><span className="capture-counter">{(Math.max(0,capture.elapsedMs||0)/1000).toFixed(2)}<span style={{fontSize:17}}> s</span></span><p>{capture.hasInput?'Recording your controller input…':'Waiting for your first selected movement…'}</p></div>
      <Button variant="primary" busy={busy==='recordStop'} onClick={()=>void run('recordStop')}><Icon name="stop" size={16}/>Stop recording</Button></div>:
      pending?<div className="stack"><Notice tone="success" title="Movement captured">{formatDuration(pending.durationMs)} · {pending.frameCount} samples. Give it a name to keep it.</Notice>
        <Field label="Sequence name"><input placeholder="A name you will remember" maxLength={80} value={name} onChange={event=>setName(event.target.value)}/></Field>
        <div className="row"><Button onClick={()=>void run('recordDiscard')}>Discard</Button><Button variant="primary" busy={busy==='recordSave'} disabled={!name.trim()} onClick={()=>void run('recordSave',{name:name.trim()})}><Icon name="save" size={16}/>Save sequence</Button></div></div>:
      <div className="stack"><Button variant="primary" disabled={!status.connected||!channels.length||status.training} busy={busy==='recordStart'} onClick={()=>void run('recordStart',{channels})}><Icon name="circle" size={16}/>Start recording</Button>
        <p>{status.connected?'The timer starts when you first move a selected control.':'Connect a controller before recording.'}</p></div>}
    {error&&<Notice tone="danger">{error}</Notice>}{capture.note&&<p>{capture.note}</p>}
  </section>;
}

function TimingEditor({state,status,command}:PageProps){
  const timings=state.activeProfile?.timings||[],[variant,setVariant]=useState(timings[0]?.variant||'rhythm');
  const item=timings.find((entry:any)=>entry.variant===variant)||timings[0];
  const [draft,setDraft]=useState<any>(item?.timing||INITIAL_TIMING),[chart,setChart]=useState<any>(null),[error,setError]=useState(''),[message,setMessage]=useState(''),[busy,setBusy]=useState(false);
  useEffect(()=>{setDraft(item?.timing||INITIAL_TIMING);setMessage('');setError('');},[state.activeProfile?.id,variant,JSON.stringify(item?.timing)]);
  useEffect(()=>{let current=true;void command('trajectory',{variant}).then(value=>{if(current)setChart(value);}).catch(()=>{});return()=>{current=false;};},[command,variant,JSON.stringify(item?.timing)]);
  const changed=JSON.stringify(draft)!==JSON.stringify(item?.timing);
  useUnsavedChanges(changed);
  const [nextVariant, setNextVariant] = useState<string | null>(null);
  return <div className="split-grid"><Card title="Shape the sequence" description="Adjust each phase, then save it to the current controller setup."><div className="stack">
    <Field label="Built-in sequence"><select value={variant} onChange={event=>{if(changed)setNextVariant(event.target.value);else setVariant(event.target.value);}}>{timings.map((entry:any)=><option key={entry.variant} value={entry.variant}>{entry.label}</option>)}</select></Field>
    <div className="timing-grid">{delayFields.map(([key,label,min])=><div className="timing-row" key={key}><label htmlFor={'timing-'+key}>{label}</label><div className="field-unit">
      <input id={'timing-'+key} type="number" min={min} max={10000} value={draft[key]??min} onChange={event=>setDraft({...draft,[key]:Number(event.target.value)})}/><span>ms</span></div></div>)}</div>
    <details><summary>Flick direction</summary><div className="form-grid">{['flickX','flickY'].map(key=><Field label={key==='flickX'?'Horizontal':'Vertical'} key={key}>
      <input type="number" min={-32768} max={32767} value={draft[key]} onChange={event=>setDraft({...draft,[key]:Number(event.target.value)})}/></Field>)}</div></details>
    {error&&<Notice tone="danger">{error}</Notice>}{message&&<Notice tone="success">{message}</Notice>}
    <div className="editor-actions"><span className="saved-note">{changed?'Unsaved changes':'All changes saved'}</span><Button disabled={!changed||busy} onClick={()=>setDraft(item.timing)}>Reset</Button>
      <Button variant="primary" busy={busy} disabled={!changed||status.training} onClick={async()=>{setBusy(true);setError('');try{await command('timingSave',{variant,timing:draft});setMessage('Sequence timing saved.');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy(false);}}}><Icon name="save" size={16}/>Save timing</Button></div>
  </div></Card><div className="stack"><Card title="How it moves" description="The saved right-stick trajectory, from beginning to end."><TrajectoryChart data={chart}/><p className="subtle" style={{marginTop:12}}>The preview reflects saved settings. It does not send controller input.</p></Card>
    <Card title="Fine-tune from shot feedback" description="Optional manual tuning for NBA 2K27."><FeedbackTuning variant={variant} command={command} disabled={status.training||changed}/></Card></div>
    <DiscardDialog open={nextVariant!==null} onKeep={()=>setNextVariant(null)} onDiscard={()=>{if(nextVariant)setVariant(nextVariant);setNextVariant(null);}}/></div>;
}

export function TrajectoryChart({data,recording}:{data?:any;recording?:any}){
  const raw=recording?.frames||data?.frames||data?.points||[],duration=Math.max(1,recording?.durationMs||data?.durationMs||raw[raw.length-1]?.timeMs||1);
  const count=Math.min(900,raw.length),width=900,height=180,points=count?Array.from({length:count},(_,i)=>raw[Math.floor(i*(raw.length-1)/Math.max(1,count-1))]):[];
  const normalized=!recording&&(data?.normalized===true||(!!data?.points&&!data?.frames));
  const path=(axis:string)=>points.map((frame:any,index:number)=>{const state=frame.state||frame;let value=Number(state[axis]??(normalized?(axis==='rightY'?state.y:state.x):0)??0);if(!normalized)value/=32767;
    return(index?'L':'M')+(20+Number(frame.timeMs||0)/duration*(width-40)).toFixed(1)+','+(height/2-value*(height/2-16)).toFixed(1);}).join(' ');
  return <div className="trajectory"><div className="chart-legend"><span><i className="legend-dot" style={{background:'var(--accent)'}}/>Vertical</span><span><i className="legend-dot" style={{background:'var(--chart-secondary)'}}/>Horizontal</span></div>
    <svg viewBox={'0 0 '+width+' '+height} role="img" aria-label="Right-stick vertical and horizontal trajectory over time">
      {[16,height/2,height-16].map(y=><line key={y} x1="20" x2={width-20} y1={y} y2={y} stroke="var(--line)" strokeWidth="1" strokeDasharray={y===height/2?undefined:'4 5'}/>)}
      {[0,.25,.5,.75,1].map(x=><line key={x} x1={20+x*(width-40)} x2={20+x*(width-40)} y1="12" y2={height-12} stroke="var(--line)"/>)}
      {count>0?<><path d={path('rightX')} fill="none" stroke="var(--chart-secondary)" strokeWidth="2.5" vectorEffect="non-scaling-stroke"/><path d={path('rightY')} fill="none" stroke="var(--accent)" strokeWidth="3" vectorEffect="non-scaling-stroke"/></>:
        <text x={width/2} y={height/2+5} fill="var(--muted)" textAnchor="middle" fontSize="18">Choose a saved sequence to see its movement</text>}
    </svg><div className="chart-labels"><span>0 ms</span><span>{formatDuration(duration)}</span></div></div>;
}

function FeedbackTuning({command,variant,id,disabled,onApplied}:{command:PageProps['command'];variant?:string;id?:string;disabled?:boolean;onApplied?:()=>Promise<void>}){
  const [timing,setTiming]=useState('none'),[tempo,setTempo]=useState('none'),[busy,setBusy]=useState(false),[message,setMessage]=useState('');
  return <div className="stack"><div className="form-grid"><Field label="Timing feedback"><select value={timing} onChange={event=>setTiming(event.target.value)}>
    {['none','early','slightlyEarly','excellent','slightlyLate','late'].map(value=><option value={value} key={value}>{value==='none'?'No adjustment':humanize(value)}</option>)}</select></Field>
    <Field label="Tempo feedback"><select value={tempo} onChange={event=>setTempo(event.target.value)}>{['none','fast','slightlyFast','excellent','slightlySlow','slow'].map(value=><option value={value} key={value}>{value==='none'?'No adjustment':humanize(value)}</option>)}</select></Field></div>
    <p className="subtle">Applies a small timing adjustment to this saved sequence. Feedback is a tuning hint, not a measured latency correction.</p>
    <Button busy={busy} disabled={disabled||(timing==='none'&&tempo==='none')} onClick={async()=>{setBusy(true);setMessage('');try{await command('nbaFeedback',{id,variant,timingFeedback:timing,tempoFeedback:tempo});await onApplied?.();setMessage('Feedback adjustment applied to the saved sequence.');}catch(e){setMessage(e instanceof Error?e.message:String(e));}finally{setBusy(false);}}}><Icon name="sliders" size={16}/>Apply feedback adjustment</Button>
    {message&&<Notice>{message}</Notice>}</div>;
}

function RecordingEditor({id,command,onClose}:{id:string;command:PageProps['command'];onClose:()=>void}){
  const [recording,setRecording]=useState<any>(null),[original,setOriginal]=useState<any>(null),[index,setIndex]=useState(0),[error,setError]=useState(''),[busy,setBusy]=useState(false),[message,setMessage]=useState('');
  const [trimStart,setTrimStart]=useState(0),[trimEnd,setTrimEnd]=useState(0),[intervalStart,setIntervalStart]=useState(0),[intervalEnd,setIntervalEnd]=useState(1),[intervalMs,setIntervalMs]=useState(100);
  const load=useCallback(async()=>{const value=await command('recordGet',{id});setRecording(value);setOriginal(value);setTrimEnd(Math.max(0,value.frames.length-1));},[id,command]);
  useEffect(()=>{void load().catch(e=>setError(e instanceof Error?e.message:String(e)));},[load]);
  const frame=recording?.frames?.[index],dirty=JSON.stringify(recording)!==JSON.stringify(original);
  useUnsavedChanges(!!recording && dirty);
  const [closeRequested,setCloseRequested]=useState(false);
  const requestClose=()=>{if(busy)return;if(dirty)setCloseRequested(true);else onClose();};
  const updateFrame=(key:string,value:number)=>setRecording({...recording,frames:recording.frames.map((item:any,i:number)=>i===index?key==='timeMs'?{...item,timeMs:value}:{...item,state:{...item.state,[key]:value}}:item)});
  const save=async()=>{setBusy(true);setError('');try{await command('recordUpdate',{recording});setOriginal(structuredClone(recording));setMessage('Sequence saved.');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy(false);}};
  return <><Modal open onClose={requestClose} wide title="Refine your sequence" description="Inspect the movement, trim the take, or adjust the saved samples."
    footer={<><span className="saved-note">{dirty?'Unsaved changes':'All changes saved'}</span><Button onClick={requestClose} disabled={busy}>Close</Button><Button variant="primary" busy={busy} disabled={!recording||!dirty} onClick={()=>void save()}><Icon name="save" size={16}/>Save sequence</Button></>}>
    {error&&<Notice tone="danger">{error}</Notice>}{message&&<Notice tone="success">{message}</Notice>}{recording?<>
      <div className="form-grid"><Field label="Sequence name"><input maxLength={80} value={recording.name} onChange={event=>setRecording({...recording,name:event.target.value})}/></Field>
        <Field label="Total duration (ms)"><input type="number" min={1} max={10000} value={recording.durationMs} onChange={event=>setRecording({...recording,durationMs:Number(event.target.value)})}/></Field></div>
      <TrajectoryChart recording={recording}/>
      <details><summary>Trim the beginning or end</summary><div className="form-grid"><Field label="First sample"><input type="number" min={0} max={recording.frames.length-1} value={trimStart} onChange={event=>setTrimStart(Number(event.target.value))}/></Field>
        <Field label="Last sample"><input type="number" min={trimStart} max={recording.frames.length-1} value={trimEnd} onChange={event=>setTrimEnd(Number(event.target.value))}/></Field></div>
        <Button style={{marginTop:14}} onClick={()=>{if(trimStart<0||trimEnd<trimStart||trimEnd>=recording.frames.length){setError('Choose a valid first and last sample.');return;}
          const start=recording.frames[trimStart].timeMs,end=trimEnd+1<recording.frames.length?recording.frames[trimEnd+1].timeMs:recording.durationMs;
          const frames=recording.frames.slice(trimStart,trimEnd+1).map((item:any)=>({...item,timeMs:item.timeMs-start}));
          setRecording({...recording,durationMs:end-start,frames});setIndex(0);setTrimStart(0);setTrimEnd(frames.length-1);setMessage('Trim is ready. Save the sequence to keep it.');}}>Trim to these samples</Button></details>
      <details><summary>Adjust an interval</summary><div className="form-grid form-grid-three">
        <Field label="Start sample (included)"><input type="number" min={0} max={recording.frames.length-1} value={intervalStart} onChange={event=>setIntervalStart(Number(event.target.value))}/></Field>
        <Field label="End boundary (excluded)"><input type="number" min={intervalStart+1} max={recording.frames.length} value={intervalEnd} onChange={event=>setIntervalEnd(Number(event.target.value))}/></Field>
        <Field label="New interval duration (ms)"><input type="number" min={1} max={10000} value={intervalMs} onChange={event=>setIntervalMs(Number(event.target.value))}/></Field></div>
        <Button style={{marginTop:14}} busy={busy} onClick={async()=>{setBusy(true);setError('');try{await command('recordRetime',{id,startFrame:intervalStart,endFrame:intervalEnd,durationMs:intervalMs,...(dirty?{recording}:{})});await load();setMessage('Interval timing saved.');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy(false);}}}>Save interval timing</Button></details>
      <details><summary>Inspect and edit individual samples</summary><div className="data-table-wrap"><table className="data-table"><thead><tr><th>Sample</th><th>Time</th><th>Right X</th><th>Right Y</th><th>Buttons</th></tr></thead>
        <tbody>{recording.frames.map((item:any,i:number)=><tr key={i} className={index===i?'selected':''}><td><button onClick={()=>setIndex(i)} aria-label={'Edit sample '+i}>{i}</button></td><td>{item.timeMs} ms</td><td>{item.state.rightX}</td><td>{item.state.rightY}</td><td>{item.state.buttons}</td></tr>)}</tbody></table></div>
        <div className="frame-fields" style={{marginTop:15}}>{['timeMs','rightX','rightY','leftX','leftY','leftTrigger','rightTrigger','buttons'].map(key=><Field key={key} label={humanize(key)}>
          <input type="number" min={key==='timeMs'||key.includes('Trigger')||key==='buttons'?0:-32768} max={key==='buttons'?65535:key.includes('Trigger')?255:key==='timeMs'?recording.durationMs:32767}
            value={key==='timeMs'?frame?.timeMs||0:frame?.state[key]||0} onChange={event=>updateFrame(key,Number(event.target.value))}/></Field>)}</div></details>
      <details><summary>NBA 2K27 feedback tuning</summary><FeedbackTuning id={id} command={command} disabled={dirty} onApplied={load}/></details>
    </>:!error&&<p className="subtle">Opening your saved sequence…</p>}
  </Modal><DiscardDialog open={closeRequested} onKeep={()=>setCloseRequested(false)} onDiscard={onClose}/></>;
}

function LuaEditor({state,status,command}:PageProps){
  const scripts=state.activeProfile?.luaScripts||[],[selected,setSelected]=useState(''),[draft,setDraft]=useState<any>(null),[original,setOriginal]=useState<any>(null);
  const [busy,setBusy]=useState(''),[error,setError]=useState(''),[message,setMessage]=useState(''),[deleting,setDeleting]=useState(false);
  useEffect(()=>{setSelected('');setDraft(null);setOriginal(null);setMessage('');},[state.activeProfile?.id]);
  const open=async(id:string)=>{setBusy('open');setError('');try{const value=await command('luaGet',{id});setSelected(id);setDraft(value);setOriginal(value);setMessage('');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}};
  const create=async()=>{setBusy('new');setError('');try{const value=await command('luaNew');setSelected(value.id);setDraft(value);setOriginal(null);setMessage('New script. Check it, then save it to this setup.');}catch(e){setError(String(e));}finally{setBusy('');}};
  const dirty=JSON.stringify(draft)!==JSON.stringify(original);
  useUnsavedChanges(!!draft && dirty);
  const [nextAction,setNextAction]=useState<(()=>void)|null>(null);
  const transition=(action:()=>void)=>{if(draft&&dirty)setNextAction(()=>action);else action();};
  return <div className="split-grid lua-layout"><Card title="Your Lua scripts" description="Controller logic saved with this setup." actions={<Button variant="ghost" busy={busy==='new'} onClick={()=>transition(()=>void create())}><Icon name="plus" size={17}/>New</Button>}>
    {scripts.length?<div className="sequence-list">{scripts.map((script:any)=><button type="button" className={'lua-script-item'+(selected===script.id?' active':'')} key={script.id} onClick={()=>transition(()=>void open(script.id))}><Icon name="code" size={19}/><span>{script.name}</span><Icon name="chevronRight" size={16}/></button>)}</div>:
      <EmptyState title="A little logic goes a long way" description="Create a Lua script when a fixed sequence is not enough. Save it here, then assign it to a control in Button mapping." icon="code" action={<Button onClick={()=>transition(()=>void create())}><Icon name="plus" size={16}/>Create a script</Button>}/>}
    <details className="details-section"><summary>Controller API quick reference</summary><div className="stack-sm subtle">
      <p>Read physical input, write controller values and wait between steps. The starter script shows the supported calls.</p><p>Scripts run with a bounded instruction budget. Check errors before assigning a script.</p></div></details>
  </Card><Card title={draft?'Shape the behavior':'Your script editor'} description="Check the Lua, save it, then choose its trigger in Button mapping.">
    {draft?<div className="stack"><Field label="Script name"><input value={draft.name||''} maxLength={80} onChange={event=>setDraft({...draft,name:event.target.value})}/></Field>
      <Field label="Lua source"><textarea className="code-editor" spellCheck={false} maxLength={state.limits?.maximumSourceLength||32768} value={draft.source||''} onChange={event=>setDraft({...draft,source:event.target.value})}
        onKeyDown={event=>{if(event.key==='Tab'){event.preventDefault();const target=event.currentTarget,start=target.selectionStart,end=target.selectionEnd;setDraft({...draft,source:draft.source.slice(0,start)+'  '+draft.source.slice(end)});requestAnimationFrame(()=>{target.selectionStart=target.selectionEnd=start+2;});}}}/></Field>
      <div className="code-meta"><span>{draft.source?.split('\n').length||0} lines</span><span>{draft.source?.length||0} characters</span><span>{dirty?'Unsaved changes':'All changes saved'}</span></div>
      {error&&<Notice tone="danger">{error}</Notice>}{message&&<Notice>{message}</Notice>}
      <div className="editor-actions"><Button variant="ghost" className="icon-button" title="Delete script" aria-label="Delete script" disabled={!original} onClick={()=>setDeleting(true)}><Icon name="trash" size={18}/></Button>
        <Button busy={busy==='validate'} onClick={async()=>{setBusy('validate');setError('');try{const result=await command('luaValidate',{source:draft.source});setMessage(result?.message||(typeof result==='string'?result:'Syntax check passed.'));}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}}}><Icon name="check" size={17}/>Check Lua</Button>
        <Button variant="primary" busy={busy==='save'} disabled={!dirty||status.training} onClick={async()=>{setBusy('save');setError('');try{await command('luaSave',{script:draft});setOriginal(structuredClone(draft));setMessage('Script saved. It is ready to assign in Button mapping.');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}}}><Icon name="save" size={17}/>Save script</Button></div>
    </div>:<EmptyState icon="code" title="Choose a script or start a new one" description="Your controller logic will appear here. Opening the editor never starts a script."/>}
  </Card><ConfirmDialog open={deleting} onClose={()=>setDeleting(false)} title="Delete this Lua script?" description={'“'+draft?.name+'” and its control assignments will be removed.'}
    onConfirm={async()=>{await command('luaDelete',{id:draft.id});setDraft(null);setOriginal(null);setSelected('');}} confirmLabel="Delete script"/>
    <DiscardDialog open={nextAction!==null} onKeep={()=>setNextAction(null)} onDiscard={()=>{const action=nextAction;setNextAction(null);action?.();}}/></div>;
}

function InputResponse({state,status,command}:PageProps){
  const [deadzone,setDeadzone]=useState<any>(state.deadzone||{leftPercent:0,rightPercent:0}),[busy,setBusy]=useState(false),[message,setMessage]=useState(''),[probe,setProbe]=useState(false),[probeStep,setProbeStep]=useState(0);
  const probePulse=useRef(0),probes=['Small left paddle','Large left paddle','Small right paddle','Large right paddle'],expected=['smallLeft','largeLeft','smallRight','largeRight'];
  useEffect(()=>{setDeadzone(state.deadzone||{leftPercent:0,rightPercent:0});},[JSON.stringify(state.deadzone)]);
  useEffect(()=>{if(!probe||status.paddlePulse===probePulse.current)return;probePulse.current=status.paddlePulse;
    if(lower(String(status.lastPaddles||''))===expected[probeStep]){if(probeStep===3){setProbe(false);setMessage('All four paddle inputs were seen.');}else setProbeStep(probeStep+1);}},[status.paddlePulse,status.lastPaddles,probe,probeStep]);
  const dirty=JSON.stringify(deadzone)!==JSON.stringify(state.deadzone);
  useUnsavedChanges(dirty);
  return <div className="stack"><div className="stat-grid"><div className="stat"><span className="stat-label">Typical input poll</span><span className="stat-value">{formatMs(status.meanPollMs)}</span></div><div className="stat"><span className="stat-label">p95 input poll</span><span className="stat-value">{formatMs(status.p95PollMs)}</span></div><div className="stat"><span className="stat-label">p99 input poll</span><span className="stat-value">{formatMs(status.p99PollMs)}</span></div></div>
    <div className="device-readout"><Card title="Physical input" description={status.connected?status.deviceName:'No physical controller connected.'}><StateReadout state={status.input||{}}/></Card>
      <Card title="Virtual output" description={status.outputStatus||'Controller output is off.'}><StateReadout state={status.output||{}}/></Card></div>
    <div className="split-grid"><Card title="A quieter center" description="Keep small stick drift from reaching the virtual controller."><div className="stack"><div className="form-grid">
      {['leftPercent','rightPercent'].map(key=><Field key={key} label={key==='leftPercent'?'Left stick deadzone':'Right stick deadzone'} hint="Set to 0% to leave the stick unfiltered."><div className="field-unit">
        <input type="number" min={0} max={50} step={.5} value={deadzone[key]} aria-label={key==='leftPercent'?'Left stick deadzone':'Right stick deadzone'} onChange={event=>setDeadzone({...deadzone,[key]:Number(event.target.value)})}/><span>%</span></div></Field>)}</div>
      <Button variant="primary" busy={busy} disabled={!dirty||status.training} onClick={async()=>{setBusy(true);setMessage('');try{await command('deadzoneSave',deadzone);setMessage('Stick deadzones saved.');}catch(e){setMessage(e instanceof Error?e.message:String(e));}finally{setBusy(false);}}}>Save deadzones</Button></div></Card>
      <Card title="Elite paddle check" description="Check the keypad paddle signals from a wired Elite controller.">{probe?<div className="stack"><Badge tone="mint">{probeStep+1} of 4</Badge><h3>Press the {probes[probeStep].toLowerCase()}</h3><Button onClick={()=>setProbe(false)}>Cancel check</Button></div>:
        <div className="stack"><p className="subtle">Select the controller's PC Profile and disconnect virtual output before checking the four paddles.</p><Button disabled={!status.supportsEliteKeypad||status.outputActive} onClick={()=>{setProbeStep(0);probePulse.current=status.paddlePulse;setProbe(true);setMessage('');}}><Icon name="gamepad" size={17}/>Check paddles</Button></div>}</Card></div>
    {message&&<Notice>{message}</Notice>}</div>;
}
function StateReadout({state}:{state:any}){
  return <div className="stack"><div className="input-pair">{['left','right'].map(side=>{const x=Number(state[side+'X']||0),y=Number(state[side+'Y']||0);return <div key={side}>
    <p className="subtle" style={{textAlign:'center'}}>{side==='left'?'Left stick':'Right stick'}</p><div className="stick-pad"><span className="stick-point" style={{left:(50+x/32768*43)+'%',top:(50-y/32768*43)+'%'}}/></div><p className="input-values">X {x} · Y {y}</p></div>;})}</div>
    <div className="form-grid"><Meter label="Left trigger" value={state.leftTrigger||0} max={255}/><Meter label="Right trigger" value={state.rightTrigger||0} max={255}/></div><p className="subtle">Buttons <code>{'0x'+Number(state.buttons||0).toString(16).toUpperCase().padStart(4,'0')}</code></p></div>;
}
