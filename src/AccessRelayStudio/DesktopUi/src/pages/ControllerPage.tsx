import { useCallback, useEffect, useRef, useState } from 'react';
import { isDesignPreview } from '../api';
import { CoPilotInput } from '../CoPilotInput';
import { ProfilePicker } from '../ui';
import { GamePacks } from '../GamePacks';
import { currentTheme, themeChanged } from '../theme';
import { Select, SelectOption, Input, Slider, Checkbox, Textarea, Badge, Button, Card, ConfirmDialog, DiscardDialog, EmptyState, Field, Icon, Meter, Modal, Notice, SectionHeader, Tabs, Toggle, formatDuration, formatMs, hasUnsavedChanges, humanize, useUnsavedChanges, type PageProps } from '../ui';

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

export function ControllerPage({ state, status, command }: PageProps) {
  const [tab, setTab] = useState('mapping'), [editScriptId,setEditScriptId]=useState<string|undefined>(), [profileAction, setProfileAction] = useState(''), [profileName, setProfileName] = useState('');
  const [profileBusy, setProfileBusy] = useState(false), [deleteProfile, setDeleteProfile] = useState(false), [error, setError] = useState(''), [busy, setBusy] = useState(''), [originalProfileName, setOriginalProfileName] = useState('');
  useUnsavedChanges(!!profileAction && profileName !== originalProfileName);
  const [nextAction, setNextAction] = useState<(() => void) | null>(null);
  const transition = (action: () => void) => { if (hasUnsavedChanges()) setNextAction(() => action); else action(); };
  const profile = state.activeProfile || {}, live = status || state.status || {}, devices = live.devices || state.devices || [];
  const connectingOutput = !live.outputActive && (!!live.isStarting || busy === 'scriptsSet');
  const act = async (action: string, args?: any) => { setBusy(action); setError(''); try { return await command(action, args); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); throw e; } finally { setBusy(''); } };
  const perform = (action: string, args?: any) => { void act(action, args).catch(() => {}); };
  const nameAction = (action: string) => { const value = action === 'profileCreate' ? '' : profile.name + (action === 'profileDuplicate' ? ' copy' : ''); setProfileName(value); setOriginalProfileName(value); setProfileAction(action); };
  const saveName = async () => { if (!profileName.trim()) return; setProfileBusy(true);
    try { await act(profileAction, { name: profileName.trim() }); setProfileAction(''); } catch {} finally { setProfileBusy(false); } };
  return <div className="page-stack">
    <SectionHeader title="Controller" actions={<ProfilePicker label="Controller profile" value={profile.id || ''} options={state.profiles || []}
      disabled={!!busy || !!live.training || !!live.recording?.isRecording} onSelect={id => transition(() => perform('profileSelect', { id }))}
      onCreate={() => transition(() => nameAction('profileCreate'))} />} />
    {error && <Notice tone="danger">{error}</Notice>}
    <div className="controller-topline"><div className="controller-device"><span className="device-icon"><Icon name="gamepad" size={25} /></span>
      <Select value={live.selectedDeviceId ?? state.selectedDeviceId ?? ''} disabled={!!live.coPilot?.enabled} aria-label="Input controller" onValueChange={selectionValue => perform('deviceSelect', { id: selectionValue })}>
        <SelectOption value="">Choose a controller</SelectOption>{live.coPilot?.enabled && !devices.some((device: any) => device.id === live.selectedDeviceId) && <SelectOption value={live.selectedDeviceId}>Primary controller unavailable</SelectOption>}{devices.map((device: any) => <SelectOption key={device.id} value={device.id}>{device.name}</SelectOption>)}</Select>
      <Button variant="ghost" className="icon-button" busy={busy === 'deviceRefresh'} title="Refresh controllers" aria-label="Refresh controllers" onClick={() => perform('deviceRefresh')}><Icon name="refresh" size={18} /></Button></div>
      <Badge tone={live.connected ? 'success' : 'neutral'}>{live.connected ? 'Input connected' : 'No input'}</Badge><div className="split-divider" />
      {live.outputActive && <Toggle label="Run scripts" checked={!!live.scriptsEnabled} disabled={!!busy || !live.connected || !!live.training || !!live.recording?.isRecording} onChange={enabled => perform('scriptsSet', { enabled })} />}
      <Button variant={live.outputActive || connectingOutput ? 'secondary' : 'primary'} aria-busy={connectingOutput || undefined} disabled={!live.outputActive && !connectingOutput && (!!busy || !live.connected || !!live.training || !!live.recording?.isRecording)}
        onClick={() => live.outputActive || connectingOutput ? perform('outputStop') : perform('scriptsSet', { enabled: true })}><Icon name="power" size={17} />{live.outputActive ? 'Stop controller' : connectingOutput ? 'Cancel connection' : 'Start controller'}</Button>
    </div>
    {!state.driverAvailable && <Notice tone="warning" title="One-time device setup">Open Settings to install the virtual device driver before connecting output.</Notice>}
    {live.training && <Notice tone="warning">Gameplay training is recording manual input. Finish that clip before changing controller profiles or scripts.</Notice>}
    <Tabs value={tab} onChange={next => { if (next !== tab) transition(() => setTab(next)); }} ariaLabel="Controller workspace" items={[{ id: 'mapping', label: 'Button mapping' }, { id: 'lua', label: 'Scripts', count: profile.luaScripts?.length || 0 }, { id: 'packs', label: 'Plugins' }, { id: 'input', label: 'Input & response' }, { id: 'accessibility', label: 'Accessibility' }]} />
    {tab === 'packs' && <GamePacks state={state} status={live} command={command} />}
    {tab === 'mapping' && <MappingEditor state={state} status={live} command={command} openScript={id=>transition(()=>{setEditScriptId(id);setTab('lua');})} />}
    {tab === 'lua' && <LuaEditor state={state} status={live} command={command} initialId={editScriptId}/>}
    {tab === 'input' && <InputResponse state={state} status={live} command={command} />}
    {tab === 'accessibility' && <div className="page-stack"><StickyInput key={'sticky-' + profile.id} state={state} status={live} command={command} />
      <SmoothingInput key={'smoothing-' + profile.id} state={state} status={live} command={command} />
      <FeedbackInput state={state} status={live} command={command} /><CoPilotInput state={state} status={live} command={command} /></div>}
    <details className="details-section"><summary>Manage controller profiles</summary><div className="row"><Button onClick={() => nameAction('profileRename')}><Icon name="pencil" size={16} />Rename</Button>
      <Button onClick={() => transition(() => nameAction('profileDuplicate'))}><Icon name="copy" size={16} />Duplicate</Button><Button onClick={() => transition(() => perform('profileImport'))}><Icon name="upload" size={16} />Import profile</Button>
      <Button onClick={() => perform('profileExport')}><Icon name="download" size={16} />Export profile</Button><Button variant="ghost" disabled={(state.profiles || []).length < 2} onClick={() => setDeleteProfile(true)}><Icon name="trash" size={16} />Delete profile</Button></div></details>
    <Modal open={!!profileAction} onClose={() => { if (!profileBusy) setProfileAction(''); }} title={profileAction === 'profileCreate' ? 'Create controller profile' : profileAction === 'profileDuplicate' ? 'Duplicate controller profile' : 'Rename controller profile'}
      description="Give it a name you will recognize when you come back." footer={<><Button disabled={profileBusy} onClick={() => setProfileAction('')}>Cancel</Button><Button variant="primary" busy={profileBusy} disabled={!profileName.trim()} onClick={() => void saveName()}>Save profile</Button></>}>
      <Field label="Profile name"><Input autoFocus maxLength={80} value={profileName} onChange={event => setProfileName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void saveName(); }} /></Field></Modal>
    <ConfirmDialog open={deleteProfile} onClose={() => setDeleteProfile(false)} title="Delete this controller profile?" description={'“' + profile.name + '” and its saved mappings and Lua scripts will be removed.'}
      onConfirm={() => command('profileDelete', { id: profile.id })} confirmLabel="Delete profile" />
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
  return <Card title="Sticky buttons" description="Press once to hold a control, then press again to release it. These choices are saved with this controller profile.">
    <div className="stack">
      <p className="subtle">Choose buttons or triggers below. Selected triggers switch between fully pressed and released. Paddles keep their usual mappings.</p>
      <p className="subtle">Stop everything, a disconnection, or changing profiles clears held controls. Release a control before pressing it again. Sequences can override sticky output; recordings capture unmodified physical input. A sticky shoot button prevents arming vision assistance.</p>
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
    <div className="stack"><p className="subtle">Higher values smooth more and add response delay. At the selected time constant, output covers about 63% of a sudden movement; allow about three times that value to reach 95%. Returning to the deadzone is immediate. Script output is not smoothed.</p>
      <div className="field-grid">{(['left', 'right'] as const).map(side => <Field key={side} label={side === 'left' ? 'Left stick smoothing' : 'Right stick smoothing'}
        hint={settings[side + 'TimeConstantMs'] === 0 ? 'Off' : settings[side + 'TimeConstantMs'] + ' ms time constant'}>
        <Slider type="range" min={0} max={100} step={1} value={settings[side + 'TimeConstantMs']} disabled={locked}
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
  return <Card title="Nonvisual feedback" description="Optional sounds and controller pulses for profile changes, player lock and completed vision releases. Preferences apply to all controller profiles.">
    <div className="stack">
      <Toggle label="Audio cues" description="Play short tones through the Windows default playback device." checked={settings.audioEnabled} disabled={busy} onChange={audioEnabled => setSettings((value: any) => ({ ...value, audioEnabled }))} />
      <Field label="Cue volume" hint={Math.round(settings.volume * 100) + '%'}><Slider type="range" min={0} max={100} step={1} disabled={busy} value={settings.volume * 100} onChange={event => setSettings((value: any) => ({ ...value, volume: Number(event.target.value) / 100 }))} /></Field>
      <Toggle label="Controller pulses" description="Use the selected physical gamepad's supported rumble motors. Test before relying on this cue." checked={settings.hapticsEnabled} disabled={busy} onChange={hapticsEnabled => setSettings((value: any) => ({ ...value, hapticsEnabled }))} />
      <Field label="Pulse intensity" hint={Math.round(settings.strength * 100) + '%'}><Slider type="range" min={0} max={50} step={1} disabled={busy} value={settings.strength * 100} onChange={event => setSettings((value: any) => ({ ...value, strength: Number(event.target.value) / 100 }))} /></Field>
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

function MappingEditor({ state, status, command, openScript }: PageProps & { openScript: (id:string) => void }) {
  const profile = state.activeProfile || {}, [selected, setSelected] = useState('largeRight');
  const binding = (profile.bindings || []).find((item: any) => item.control === selected) || { control: selected, label: humanize(selected), script: 'passthrough', enabled: true, consume: false };
  const [draft, setDraft] = useState<any>(binding), [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [error, setError] = useState('');
  useEffect(() => { setDraft(binding); setMessage(''); setError(''); }, [selected, profile.id, JSON.stringify(binding)]);
  const saved = JSON.stringify(draft) === JSON.stringify(binding);
  useUnsavedChanges(!saved);
  const [nextControl, setNextControl] = useState<string | null>(null);
  const chooseControl = (control: string) => { if (control === selected) return; if (!saved) setNextControl(control); else setSelected(control); };
  const selectedScript = draft.script === 'lua' ? 'lua:' + draft.luaScriptId : draft.script;
  const groups = [{ title: 'Buttons & bumpers', controls: ['a', 'b', 'x', 'y', 'leftBumper', 'rightBumper', 'leftTrigger', 'rightTrigger'] },
    { title: 'Sticks & navigation', controls: ['leftStick', 'rightStick', 'dPadUp', 'dPadDown', 'dPadLeft', 'dPadRight', 'menu', 'view'] },
    { title: 'Elite paddles', controls: ['smallLeft', 'smallRight', 'largeLeft', 'largeRight'] }];
  const scriptName = (item: any) => item.script === 'lua' ? profile.luaScripts?.find((script: any) => script.id === item.luaScriptId)?.name || 'Lua script' : 'Normal input';
  return <div className="controller-layout mapping-layout"><div className="stack">
    <ControllerVisual state={state} status={status} selected={selected} onSelect={chooseControl} />
    <Card title="Choose a control" description="Pick it on the controller or choose it below."><div className="binding-groups">{groups.map(group => <div key={group.title}><p className="binding-group-label">{group.title}</p><div className="binding-grid">{group.controls.map(control => {
      const item = (profile.bindings || []).find((item: any) => item.control === control); if (!item) return null;
      return <Button variant="unstyled" type="button" key={control} className={'binding-item' + (selected === control ? ' selected' : '')} aria-label={item.label + ': ' + (item.enabled ? scriptName(item) : 'Disabled')} aria-pressed={selected === control} onClick={() => chooseControl(control)}>
        <span className="control-glyph">{controlGlyph(control)}</span><span className="binding-copy"><strong>{item.label}</strong><small>{item.enabled ? scriptName(item) : 'Disabled'}</small></span></Button>;
    })}</div></div>)}</div></Card></div>
    <Card className="mapping-editor"><div className="editor-heading"><span className="control-glyph">{controlGlyph(selected)}</span><div><p className="eyebrow">Edit this control</p><h2>{binding.label}</h2></div></div><div className="stack">
      <Field label="What should it do?" hint={draft.script === 'passthrough' ? "Keep this control's usual behavior." : 'Run this script when the control is pressed.'}><Select value={selectedScript} disabled={status.training} onValueChange={selectionValue => { const [script, id] = selectionValue.split(':'); setDraft({ ...draft, script, recordingId: null, luaScriptId: script === 'lua' ? id : null }); setMessage(''); }}>
        {(state.scripts || []).filter((item: any) => item.id === 'passthrough').map((item: any) => <SelectOption key={item.id} value={item.id}>{item.label}</SelectOption>)}
        {!!profile.luaScripts?.length && <optgroup label="Your Lua scripts">{profile.luaScripts.map((item: any) => <SelectOption key={item.id} value={'lua:' + item.id}>{item.name}</SelectOption>)}</optgroup>}</Select></Field>
      <Toggle label="Use this mapping" checked={draft.enabled !== false} disabled={status.training} onChange={enabled => setDraft({ ...draft, enabled })} />
      {!/^(small|large)/.test(selected) && draft.script !== 'passthrough' && <Toggle label="Replace the physical press" description="Run the script without also passing this button press to the game." checked={!!draft.consume} disabled={status.training} onChange={consume => setDraft({ ...draft, consume })} />}
      {draft.script !== 'passthrough' && <p className="subtle">{!status.outputActive ? 'Start controller to use this mapping.' : !status.scriptsEnabled ? 'Scripts are paused. Turn on Run scripts to use this mapping.' : 'Runs when you press this control.'}</p>}
      {draft.script !== 'passthrough' && <Button variant="ghost" onClick={()=>openScript(draft.luaScriptId)}><Icon name="code" size={17} />Edit script<Icon name="arrowRight" size={16} /></Button>}
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
    actions={<Select className="model-select" aria-label="Controller model" value={model} onValueChange={selectionValue => { setReady(false); setModel(selectionValue); }}><SelectOption value="elite-series-2">Elite Series 2</SelectOption><SelectOption value="xbox-one">Xbox One</SelectOption><SelectOption value="dualsense">DualSense</SelectOption><SelectOption value="dualshock-4">DualShock 4</SelectOption><SelectOption value="switch-pro">Switch Pro</SelectOption></Select>}>
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

function RecordPanel({state,status,command,onSaved}:PageProps & {onSaved:(script:any)=>void}){
  const [channels,setChannels]=useState(['rightStick']),[name,setName]=useState(''),[error,setError]=useState(''),[busy,setBusy]=useState('');
  const capture=status.recording||{},pending=capture.pending;
  const options=[{id:'rightStick',label:'Right stick'},{id:'leftStick',label:'Left stick'},{id:'buttons',label:'Buttons & bumpers'},{id:'leftTrigger',label:'Left trigger'},{id:'rightTrigger',label:'Right trigger'}];
  const run=async(action:string,args?:any)=>{setBusy(action);setError('');try{const result=await command(action,args);if(action==='recordSave'){setName('');onSaved(result);}}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}};
  return <section className="capture-panel"><div className="capture-head"><div><h2>Record controller input</h2></div><Icon name="radio" size={24}/></div>
    <p>Choose the controls to capture. Saving creates an editable Lua script.</p>
    <div className="checkbox-grid">{options.map(option=><label key={option.id} className="check-row"><Checkbox type="checkbox" disabled={capture.isRecording||!!pending} checked={channels.includes(option.id)}
      onChange={event=>setChannels(event.target.checked?[...channels,option.id]:channels.filter(id=>id!==option.id))}/>{option.label}</label>)}</div>
    <details><summary>Choose individual axes or buttons</summary><div className="checkbox-grid">{(state.recordingChannels||[]).filter((item:any)=>!['all','buttons','rightStick','leftStick','leftTrigger','rightTrigger'].includes(item.id))
      .map((option:any)=><label key={option.id} className="check-row"><Checkbox type="checkbox" disabled={capture.isRecording||!!pending} checked={channels.includes(option.id)}
        onChange={event=>setChannels(event.target.checked?[...channels,option.id]:channels.filter(id=>id!==option.id))}/>{option.label}</label>)}</div></details>
    {capture.isRecording?<div className="stack"><div><span className="capture-counter">{(Math.max(0,capture.elapsedMs||0)/1000).toFixed(2)}<span style={{fontSize:17}}> s</span></span><p>{capture.hasInput?'Recording your controller input…':'Waiting for your first selected movement…'}</p></div>
      <Button variant="primary" busy={busy==='recordStop'} onClick={()=>void run('recordStop')}><Icon name="stop" size={16}/>Stop recording</Button></div>:
      pending?<div className="stack"><Notice tone="success" title="Movement captured">{formatDuration(pending.durationMs)} · {pending.frameCount} samples. Give the Lua script a name to save it.</Notice>
        <Field label="Script name"><Input placeholder="A name you will remember" maxLength={80} value={name} onChange={event=>setName(event.target.value)}/></Field>
        <div className="row"><Button onClick={()=>void run('recordDiscard')}>Discard</Button><Button variant="primary" busy={busy==='recordSave'} disabled={!name.trim()} onClick={()=>void run('recordSave',{name:name.trim()})}><Icon name="save" size={16}/>Save as Lua script</Button></div></div>:
      <div className="stack"><Button variant="primary" disabled={!status.connected||!channels.length||status.training} busy={busy==='recordStart'} onClick={()=>void run('recordStart',{channels})}><Icon name="circle" size={16}/>Start recording</Button>
        <p>{status.connected?'The timer starts when you first move a selected control.':'Connect a controller before recording.'}</p></div>}
    {error&&<Notice tone="danger">{error}</Notice>}{capture.note&&<p>{capture.note}</p>}
  </section>;
}

function LuaEditor({state,status,command,initialId}:PageProps & {initialId?:string}){
  const scripts=state.activeProfile?.luaScripts||[],[selected,setSelected]=useState(''),[draft,setDraft]=useState<any>(null),[original,setOriginal]=useState<any>(null);
  const [busy,setBusy]=useState(''),[error,setError]=useState(''),[message,setMessage]=useState(''),[deleting,setDeleting]=useState(false),[recordOpen,setRecordOpen]=useState(false),[discardRecording,setDiscardRecording]=useState(false);
  useEffect(()=>{setSelected('');setDraft(null);setOriginal(null);setMessage('');if(initialId)void open(initialId);},[state.activeProfile?.id,initialId]);
  const open=async(id:string)=>{setBusy('open');setError('');try{const value=await command('luaGet',{id});setSelected(id);setDraft(value);setOriginal(value);setMessage('');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}};
  const create=async()=>{setBusy('new');setError('');try{const value=await command('luaNew');setSelected(value.id);setDraft(value);setOriginal(null);setMessage('New script. Check it, then save it to this profile.');}catch(e){setError(String(e));}finally{setBusy('');}};
  const dirty=JSON.stringify(draft)!==JSON.stringify(original);
  useUnsavedChanges(!!draft && dirty);
  const [nextAction,setNextAction]=useState<(()=>void)|null>(null);
  const transition=(action:()=>void)=>{if(draft&&dirty)setNextAction(()=>action);else action();};
  const recordingBusy=status.recording?.isRecording||!!status.recording?.pending;
  return <div className="split-grid lua-layout"><Card title="Your Lua scripts" description="Write a script or create one by recording controller input." actions={<div className="row">
    <Button variant="ghost" onClick={()=>transition(()=>setRecordOpen(true))}><Icon name="radio" size={17}/>Record input</Button>
    <Button variant="ghost" onClick={()=>transition(()=>void command('luaImport').then(value=>{if(value){setSelected(value.id);setDraft(value);setOriginal(value);}}).catch(e=>setError(String(e))))}><Icon name="upload" size={17}/>Import</Button>
    <Button variant="ghost" busy={busy==='new'} onClick={()=>transition(()=>void create())}><Icon name="plus" size={17}/>New</Button></div>}>
    {scripts.length?<div className="sequence-list">{scripts.map((script:any)=><Button variant="unstyled" type="button" className={'lua-script-item'+(selected===script.id?' active':'')} key={script.id} onClick={()=>transition(()=>void open(script.id))}><Icon name="code" size={19}/><span>{script.name}</span><Icon name="chevronRight" size={16}/></Button>)}</div>:
      <EmptyState title="No scripts yet" description="Write Lua or record your controller input, then assign the saved script in Button mapping." icon="code" action={<Button onClick={()=>transition(()=>void create())}><Icon name="plus" size={16}/>Create a script</Button>}/>}
    {error&&!draft&&<Notice tone="danger">{error}</Notice>}
    <details className="details-section"><summary>Controller API quick reference</summary><div className="stack-sm subtle">
      <p>Read physical input, write buttons, sticks or triggers, and wait between steps. Recorded input is saved inside output.replay as timestamped raw values.</p><p>Scripts run with a bounded instruction budget. Check errors before assigning a script.</p></div></details>
  </Card><Card title={draft?'Shape the behavior':'Your script editor'} description="Check the Lua, save it, then choose its trigger in Button mapping.">
    {draft?<div className="stack"><Field label="Script name"><Input value={draft.name||''} maxLength={80} onChange={event=>setDraft({...draft,name:event.target.value})}/></Field>
      <Field label="Lua source"><Textarea className="code-editor" spellCheck={false} maxLength={state.limits?.maximumSourceLength||524288} value={draft.source||''} onChange={event=>setDraft({...draft,source:event.target.value})}
        onKeyDown={event=>{if(event.key==='Tab'){event.preventDefault();const target=event.currentTarget,start=target.selectionStart,end=target.selectionEnd;setDraft({...draft,source:draft.source.slice(0,start)+'  '+draft.source.slice(end)});requestAnimationFrame(()=>{target.selectionStart=target.selectionEnd=start+2;});}}}/></Field>
      <div className="code-meta"><span>{draft.source?.split('\n').length||0} lines</span><span>{draft.source?.length||0} characters</span><span>{dirty?'Unsaved changes':'All changes saved'}</span></div>
      {error&&<Notice tone="danger">{error}</Notice>}{message&&<Notice>{message}</Notice>}
      <div className="editor-actions"><Button variant="ghost" className="icon-button" title="Delete script" aria-label="Delete script" disabled={!original} onClick={()=>setDeleting(true)}><Icon name="trash" size={18}/></Button>
        <Button disabled={!original||dirty} onClick={()=>void command('luaExport',{id:draft.id}).catch(e=>setError(String(e)))}><Icon name="download" size={17}/>Export</Button>
        <Button busy={busy==='validate'} onClick={async()=>{setBusy('validate');setError('');try{const result=await command('luaValidate',{source:draft.source});setMessage(result?.message||(typeof result==='string'?result:'Syntax check passed.'));}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}}}><Icon name="check" size={17}/>Check Lua</Button>
        <Button variant="primary" busy={busy==='save'} disabled={!dirty||status.training} onClick={async()=>{setBusy('save');setError('');try{await command('luaSave',{script:draft});setOriginal(structuredClone(draft));setMessage('Script saved. It is ready to assign in Button mapping.');}catch(e){setError(e instanceof Error?e.message:String(e));}finally{setBusy('');}}}><Icon name="save" size={17}/>Save script</Button></div>
    </div>:<EmptyState icon="code" title="Choose a script or start a new one" description="Your controller logic will appear here. Opening the editor never starts a script."/>}
  </Card><ConfirmDialog open={deleting} onClose={()=>setDeleting(false)} title="Delete this Lua script?" description={'“'+draft?.name+'” and its control assignments will be removed.'}
    onConfirm={async()=>{await command('luaDelete',{id:draft.id});setDraft(null);setOriginal(null);setSelected('');}} confirmLabel="Delete script"/>
    <Modal open={recordOpen} title="Record controller input" wide onClose={()=>{if(recordingBusy)setDiscardRecording(true);else setRecordOpen(false);}}>
      <RecordPanel state={state} status={status} command={command} onSaved={script=>{setRecordOpen(false);setSelected(script.id);setDraft(script);setOriginal(script);setMessage('Recording saved as a Lua script.');}}/>
    </Modal>
    <ConfirmDialog open={discardRecording} onClose={()=>setDiscardRecording(false)} title="Discard this recording?" description="The unsaved controller input will be discarded."
      confirmLabel="Discard recording" onConfirm={async()=>{if(status.recording?.isRecording)await command('recordStop');await command('recordDiscard');setDiscardRecording(false);setRecordOpen(false);}}/>
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
        <Input type="number" min={0} max={50} step={.5} value={deadzone[key]} aria-label={key==='leftPercent'?'Left stick deadzone':'Right stick deadzone'} onChange={event=>setDeadzone({...deadzone,[key]:Number(event.target.value)})}/><span>%</span></div></Field>)}</div>
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
