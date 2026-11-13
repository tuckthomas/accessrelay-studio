import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, KeyboardEvent, PointerEvent } from 'react';
import { VoiceCommands } from '../VoiceCommands';
import { ActionMenu, ActionMenuItem, Badge, Button, Card, ConfirmDialog, DiscardDialog, EmptyState, Field, Icon, Meter, Modal, Notice, SectionHeader, Tabs, Toggle, hasUnsavedChanges, useUnsavedChanges, type PageProps } from '../ui';

type VoiceSettings = { preset: string; pitchSemitones: number; gainDb: number; muted: boolean };
const defaultSettings: VoiceSettings = { preset: 'clean', pitchSemitones: 0, gainDb: 0, muted: false };
const presets = [{ id: 'clean', label: 'Natural', detail: 'Uncolored and clear' }, { id: 'warm', label: 'Warm', detail: 'Soft and rounded' },
  { id: 'bright', label: 'Bright', detail: 'Crisp and present' }, { id: 'deep', label: 'Deep', detail: 'A fuller tone' }, { id: 'radio', label: 'Radio', detail: 'Focused and compact' }];
const seconds = (ms: number) => (ms / 1000).toFixed(3);
const level = (peak: number = 0) => peak > 0 ? Math.min(100, Math.max(0, (20 * Math.log10(peak) + 60) / 60 * 100)) : 0;

export function VoicePage({ state = {}, status: liveStatus, command }: PageProps) {
  const status = liveStatus || state.status || {};
  const [tab, setTab] = useState('live');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [route, setRoute] = useState({ microphoneId: state.microphoneId || '', outputId: state.outputId || '', destination: state.destination || 'virtual' });
  const [settings, setSettings] = useState<VoiceSettings>({ ...defaultSettings, ...state.settings });
  const [editorOpen, setEditorOpen] = useState(false);
  const [editorName, setEditorName] = useState('');
  const [effectsOpen, setEffectsOpen] = useState(false);
  const [effectsName, setEffectsName] = useState('');
  const [replaceEffects, setReplaceEffects] = useState(false);
  const [effectsBaseline, setEffectsBaseline] = useState({ name: '', replace: false });
  const [nameAction, setNameAction] = useState<{ action: string; id: string; name: string; originalName: string } | null>(null);
  const [clipBaseline, setClipBaseline] = useState<{ id: string; startMs: number; endMs: number; name: string } | null>(null);
  const [nextAction, setNextAction] = useState<(() => void) | null>(null);
  const [deleteProfile, setDeleteProfile] = useState<any>(null);
  const [trim, setTrim] = useState({ startMs: 0, endMs: 0 });
  const [playhead, setPlayhead] = useState(0);
  const profiles: any[] = state.profiles || [];
  const models: any[] = state.models || [];
  const selected = profiles.find(profile => profile.id === state.selectedProfileId);
  const model = models.find(item => item.id === state.selectedModelId);
  const neural = model?.usesReference === true;
  const clip = state.clip;
  const isRunning = status.isRunning === true;
  const blocked = !!busy || status.busy === true;
  const routeLocked = blocked || isRunning;
  const clipDuration = Math.max(0, Number(clip?.durationMs) || 0);
  const selectionMs = trim.endMs - trim.startMs;
  const clipCanSave = selectionMs >= 1000 && selectionMs <= 30_000 && !!editorName.trim();
  const microphones: any[] = state.devices?.microphones || [];
  const outputs: any[] = state.devices?.outputs || [];
  const tone = (value: any) => JSON.stringify([value?.preset || 'clean', value?.pitchSemitones || 0, value?.gainDb || 0]);
  const effectsDirty = tone(settings) !== tone(state.settings);
  const clipDirty = !!clip && !!clipBaseline && clipBaseline.id === clip.id && (trim.startMs !== clipBaseline.startMs || trim.endMs !== clipBaseline.endMs || editorName !== clipBaseline.name);
  const effectsNameDirty = effectsOpen && (effectsName !== effectsBaseline.name || replaceEffects !== effectsBaseline.replace);
  const profileNameDirty = !!nameAction && nameAction.name !== nameAction.originalName;
  useUnsavedChanges(effectsDirty || clipDirty || effectsNameDirty || profileNameDirty);

  useEffect(() => { setRoute({ microphoneId: state.microphoneId || '', outputId: state.outputId || '', destination: state.destination || 'virtual' }); },
    [state.microphoneId, state.outputId, state.destination]);
  useEffect(() => { setSettings({ ...defaultSettings, ...state.settings }); },
    [state.settings?.preset, state.settings?.pitchSemitones, state.settings?.gainDb]);
  useEffect(() => {
    if (!clip) { setClipBaseline(null); setEditorName(''); setTrim({ startMs: 0, endMs: 0 }); return; }
    setTrim({ startMs: clip.startMs || 0, endMs: clip.endMs || 0 });
    setPlayhead(clip.startMs || 0);
    const name = clip.name || 'My reference voice';
    setEditorName(name);
    setClipBaseline({ id: clip.id, startMs: clip.startMs || 0, endMs: clip.endMs || 0, name });
    // Native audition/cleanup can update the preparation trim. It must not overwrite
    // editable fields or silently declare a reference profile saved.
  }, [clip?.id]);

  type DraftScope = 'effects' | 'clip' | 'effectsName' | 'profileName';
  function transition(action: () => void, scopes: DraftScope[]) {
    const dirty = { effects: effectsDirty, clip: clipDirty, effectsName: effectsNameDirty, profileName: profileNameDirty };
    if (!scopes.some(scope => dirty[scope])) { action(); return; }
    setNextAction(() => () => {
      if (scopes.includes('effects')) setSettings({ ...defaultSettings, ...state.settings });
      if (scopes.includes('clip') && clipBaseline) {
        setTrim({ startMs: clipBaseline.startMs, endMs: clipBaseline.endMs }); setEditorName(clipBaseline.name); setPlayhead(clipBaseline.startMs);
      }
      if (scopes.includes('effectsName')) { setEffectsName(effectsBaseline.name); setReplaceEffects(effectsBaseline.replace); }
      if (scopes.includes('profileName')) setNameAction(value => value ? { ...value, name: value.originalName } : value);
      action();
    });
  }
  const changeTab = (next: string) => { if (next !== tab) { if (tab === 'commands' && hasUnsavedChanges()) setNextAction(() => () => setTab(next)); else transition(() => setTab(next), ['effects', 'clip']); } };
  const closeEditor = () => { if (!blocked) transition(() => { setEditorOpen(false); void command('clipPause').catch(() => {}); }, ['clip']); };
  const closeEffects = () => { if (!blocked) transition(() => setEffectsOpen(false), ['effectsName']); };
  const closeName = () => { if (!blocked) transition(() => setNameAction(null), ['profileName']); };
  const closeDelete = useCallback(() => setDeleteProfile(null), []);

  async function execute(key: string, action: () => Promise<any>) {
    setBusy(key); setError('');
    try { return await action(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return undefined; }
    finally { setBusy(''); }
  }
  const run = (action: string, args?: any) => execute(action, () => command(action, args));
  const saveTrim = () => command('clipTrim', trim);
  function importReference() { setEditorOpen(true); if (!clip) void run('clipBrowse'); }
  function openEffects(update = false) {
    const name = update && selected?.kind === 'effects' ? selected.name : (presets.find(preset => preset.id === settings.preset)?.label || 'My') + ' voice';
    const replace = update && selected?.kind === 'effects';
    setEffectsName(name); setReplaceEffects(replace); setEffectsBaseline({ name, replace }); setEffectsOpen(true);
  }

  return <div className="page-stack voice-page">
    <style>{voiceCss}</style>
    <SectionHeader eyebrow="YOUR VOICE" title="Make yourself heard." description="A voice you choose. A route you control. Everything stays on your PC."
      actions={<Button onClick={() => void run('refreshDevices')} disabled={blocked}><Icon name="refresh" size={17} />Refresh devices</Button>} />
    <Tabs ariaLabel="Voice workspace" value={tab} onChange={changeTab} items={[{ id: 'live', label: 'Live voice' }, { id: 'library', label: 'Your voices', count: profiles.length }, { id: 'commands', label: 'Voice commands' }]} />
    {tab === 'commands' && <VoiceCommands state={state} status={status} command={command} />}
    {error && <Notice tone="danger">{error}</Notice>}
    {status.busy && <Notice title={status.message}>{status.operation === 'start' && neural ? 'Large reference models can take a moment to load.' : 'You can cancel this operation at any time.'}
      <Button variant="ghost" onClick={() => { void command('cancel').catch(e => setError(String(e))); }}>Cancel</Button></Notice>}

    {tab === 'live' ? <>
      <div className="voice-workbench">
        <Card className="voice-route-card">
          <div className="voice-card-title"><span className="voice-step-number">1</span><div><h2>Your microphone</h2><p className="muted">The voice coming in.</p></div></div>
          <Field label="Microphone"><select aria-label="Voice input microphone" value={route.microphoneId} disabled={routeLocked}
            onChange={event => setRoute({ ...route, microphoneId: event.target.value })}>
            {!microphones.length && <option value="">No microphone found</option>}
            {microphones.map(device => <option key={device.id} value={device.id}>{device.name}{device.isDefault ? ' · Default' : ''}</option>)}
          </select></Field>
          <div className="voice-path" aria-hidden="true"><Icon name="mic" /><span /><Icon name="arrowRight" /></div>
          <div className="voice-card-title"><span className="voice-step-number">2</span><div><h2>The voice you want</h2><p className="muted">Natural effects or a reference voice.</p></div></div>
          <Field label="Saved voice"><select aria-label="Saved voice profile" value={state.selectedProfileId || ''} disabled={blocked}
            onChange={event => { const id = event.target.value; transition(() => { void run(id ? 'profileSelect' : 'profileClear', id ? { id } : {}); }, ['effects']); }}>
            <option value="">Custom built-in effects</option>{profiles.map(profile => <option key={profile.id} value={profile.id}>{profile.name}{profile.kind === 'reference' ? ' · Reference' : ''}</option>)}
          </select></Field>
          <Button variant="ghost" onClick={() => changeTab('library')}><Icon name="audio" size={17} />Browse your voice library</Button>
          {neural && <Field label="Reference model" hint={model.description}><select aria-label="Reference voice model" value={state.selectedModelId || ''} disabled={routeLocked}
            onChange={event => { const id = event.target.value; transition(() => { void run('modelSelect', { id }); }, ['effects']); }}>{models.filter(item => item.usesReference).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></Field>}
        </Card>

        <Card className="voice-output-card">
          <div className="voice-output-heading"><span className="voice-large-icon"><Icon name={isRunning ? 'audio' : 'waves'} size={34} /></span>
            {isRunning && <Badge tone="success">Voice is live</Badge>}</div>
          <h2>{selected?.name || 'Your natural voice, refined.'}</h2>
          <p className="muted">{neural ? 'A local reference model shapes your speech using the voice you saved.' : 'Use a little warmth, clarity or character. You decide how much.'}</p>
          <div className="voice-meter-pair"><Meter label="Microphone input" value={level(status.inputPeak)} /><Meter label="Processed output" value={level(status.outputPeak)} /></div>
          <div className="voice-destination-title"><span className="voice-step-number">3</span><h3>Where should it go?</h3></div>
          <div className="voice-destinations" role="group" aria-label="Voice destination">
            <button type="button" className={'voice-destination ' + (route.destination === 'headphones' ? 'selected' : '')} disabled={routeLocked}
              onClick={() => setRoute({ ...route, destination: 'headphones' })}><Icon name="headphones" /><span><strong>Headphones</strong><small>Hear a preview first</small></span>{route.destination === 'headphones' && <Icon name="check" size={17} />}</button>
            <button type="button" className={'voice-destination ' + (route.destination === 'virtual' ? 'selected' : '')} disabled={routeLocked}
              onClick={() => setRoute({ ...route, destination: 'virtual' })}><Icon name="mic" /><span><strong>Virtual microphone</strong><small>Use in other apps</small></span>{route.destination === 'virtual' && <Icon name="check" size={17} />}</button>
          </div>
          {route.destination === 'headphones' ? <>
            <Field label="Playback device"><select aria-label="Voice preview playback device" value={route.outputId} disabled={routeLocked}
              onChange={event => setRoute({ ...route, outputId: event.target.value })}>{!outputs.length && <option value="">No playback device found</option>}
              {outputs.map(device => <option key={device.id} value={device.id}>{device.name}{device.isDefault ? ' · Default' : ''}</option>)}</select></Field>
            {neural && <p className="field-hint">Reference preview records {state.referencePreviewSeconds || 3} seconds of speech, then plays the converted result. Built-in effects use live playback.</p>}
          </> : <p className="field-hint">After starting, select <strong>AccessRelay Virtual Microphone</strong> in Xbox, Discord or another Windows app.</p>}
          {route.destination === 'virtual' && state.virtualMicIssue && <Notice tone="warning">{state.virtualMicIssue} Check Settings for virtual-device setup.</Notice>}
          <div className="voice-start-row">
            {isRunning || status.isStarting ? <Button variant="danger" onClick={() => void command('stop').catch(e => setError(String(e)))}><Icon name="stop" size={18} />{status.isStarting ? 'Cancel start' : 'Stop voice'}</Button>
              : <Button variant="primary" busy={busy === 'start'} disabled={blocked || !route.microphoneId || (route.destination === 'headphones' && !route.outputId)} onClick={() => void execute('start', async () => {
                await command('routeSave', route); await command('start');
              })}><Icon name={route.destination === 'headphones' ? 'play' : 'mic'} size={18} />{route.destination === 'headphones' ? 'Try this voice' : 'Start virtual microphone'}</Button>}
            <Button variant={state.settings?.muted ? 'secondary' : 'ghost'} disabled={blocked} onClick={() => void run('muteSet', { muted: !state.settings?.muted })}>
              <Icon name="volume" size={18} />{state.settings?.muted ? 'Unmute' : 'Mute'}</Button>
          </div>
          <p className="voice-session-message" aria-live="polite">{status.message || 'Voice processing is off.'}{isRunning && status.bufferedMilliseconds > 0 ? ` · ${status.bufferedMilliseconds} ms audio queued` : ''}</p>
        </Card>
      </div>
      <Card title="Make it your own" description={neural ? 'Reference models control the voice character. Adjust the output level here.' : 'Choose a starting tone, then make small adjustments.'}
        actions={<Button onClick={() => openEffects(selected?.kind === 'effects')} disabled={blocked || neural}><Icon name="save" size={17} />Save voice settings</Button>}>
        {!neural && <div className="voice-presets" role="group" aria-label="Built-in voice tone">{presets.map(preset => <button type="button" key={preset.id}
          aria-pressed={settings.preset === preset.id} disabled={blocked} className={'voice-preset ' + (settings.preset === preset.id ? 'selected' : '')}
          onClick={() => setSettings({ ...settings, preset: preset.id })}><strong>{preset.label}</strong><span>{preset.detail}</span></button>)}</div>}
        <div className="voice-tuning">
          {!neural && <Field label={<span>Pitch <strong>{settings.pitchSemitones > 0 ? '+' : ''}{settings.pitchSemitones.toFixed(1)} semitones</strong></span>}>
            <input aria-label="Voice pitch in semitones" type="range" min="-8" max="8" step="0.1" value={settings.pitchSemitones} disabled={blocked}
              onChange={event => setSettings({ ...settings, pitchSemitones: Number(event.target.value) })} /></Field>}
          <Field label={<span>Output gain <strong>{settings.gainDb > 0 ? '+' : ''}{settings.gainDb.toFixed(1)} dB</strong></span>}>
            <input aria-label="Voice output gain in decibels" type="range" min="-18" max="12" step="0.5" value={settings.gainDb} disabled={blocked}
              onChange={event => setSettings({ ...settings, gainDb: Number(event.target.value) })} /></Field>
          <div className="stack"><span className="saved-note">{effectsDirty ? 'Unapplied adjustments' : 'Adjustments applied'}</span><Button onClick={() => void run('settingsSave', { settings: { ...settings, muted: state.settings?.muted === true } })} disabled={blocked}>Apply adjustments</Button></div>
        </div>
      </Card>
    </> : tab === 'library' ? <>
      <Card className="voice-library-intro"><div><span className="eyebrow">VOICE LIBRARY</span><h2>A collection that sounds like you.</h2>
        <p className="muted">Save a favorite effects mix, or create a reference voice from a short speech clip.</p></div>
        <div className="voice-library-actions"><Button variant="primary" onClick={importReference} disabled={blocked}><Icon name="plus" size={17} />Create reference voice</Button>
          <Button onClick={() => openEffects(false)} disabled={blocked}><Icon name="sliders" size={17} />Save an effects voice</Button></div></Card>
      {!models.some(item => item.usesReference) && <Notice title="Built-in effects are ready">No reference-voice model is installed in this build. You can prepare and save reference clips now; an installed compatible model is needed to use them.</Notice>}
      {profiles.length ? <div className="voice-library-grid">{profiles.map(profile => <Card key={profile.id} className="voice-library-card">
        <div className="voice-library-card-top"><span className={'voice-profile-avatar ' + (profile.kind === 'reference' ? 'reference' : '')}><Icon name={profile.kind === 'reference' ? 'audio' : 'sliders'} size={25} /></span>
          {profile.id === state.selectedProfileId && <Badge tone="mint">Selected</Badge>}</div>
        <h3>{profile.name}</h3><p className="muted">{profile.kind === 'reference' ? `${seconds(profile.durationMs)} seconds of reference speech` : `${presets.find(preset => preset.id === profile.effects?.preset)?.label || profile.effects?.preset || 'Custom'} built-in effects`}</p>
        <div className="voice-library-card-actions"><Button onClick={() => transition(() => { void execute('profileSelect', async () => { await command('profileSelect', { id: profile.id }); setTab('live'); }); }, ['effects'])} disabled={blocked}>Use this voice<Icon name="arrowRight" size={16} /></Button>
          <ActionMenu label={'More actions for ' + profile.name}>
            <ActionMenuItem disabled={routeLocked} onSelect={() => setNameAction({ action: 'profileRename', id: profile.id, name: profile.name, originalName: profile.name })}><Icon name="pencil" size={16} />Rename</ActionMenuItem>
            <ActionMenuItem disabled={routeLocked} onSelect={() => setNameAction({ action: 'profileDuplicate', id: profile.id, name: profile.name + ' copy', originalName: profile.name + ' copy' })}><Icon name="copy" size={16} />Duplicate</ActionMenuItem>
            {profile.kind === 'effects' && <ActionMenuItem disabled={blocked} onSelect={() => transition(() => { void execute('profileSelect', async () => { await command('profileSelect', { id: profile.id }); setTab('live'); }); }, ['effects'])}><Icon name="sliders" size={16} />Adjust effects</ActionMenuItem>}
            {profile.kind === 'reference' && <ActionMenuItem disabled={blocked} onSelect={() => transition(() => { setEditorOpen(true); void run('clipOpenProfile', { id: profile.id }); }, ['clip'])}><Icon name="waves" size={16} />Trim a new copy</ActionMenuItem>}
            <ActionMenuItem disabled={routeLocked} onSelect={() => setDeleteProfile(profile)}><Icon name="trash" size={16} />Delete</ActionMenuItem>
          </ActionMenu></div>
      </Card>)}</div> : <Card><EmptyState icon="audio" title="Your first voice starts here" description="Built-in effects work immediately. Save a mix you like, or choose an audio or video clip to make a reference voice."
        action={<Button variant="primary" onClick={importReference} disabled={blocked}>Create reference voice</Button>} /></Card>}
    </> : null}

    <Modal open={effectsOpen} onClose={closeEffects} title="Save your voice settings" description="Give this mix a name so you can come back to it."
      footer={<><Button onClick={closeEffects} disabled={blocked}>Cancel</Button><Button variant="primary" busy={busy === 'profileSaveEffects'} disabled={blocked || !effectsName.trim()} onClick={() => void execute('profileSaveEffects', async () => {
        await command('profileSaveEffects', { name: effectsName, settings: { ...settings, muted: state.settings?.muted === true }, ...(replaceEffects && selected?.kind === 'effects' ? { id: selected.id } : {}) }); setEffectsOpen(false);
      })}>{replaceEffects ? 'Save changes' : 'Save voice'}</Button></>}>
      <Field label="Voice name"><input aria-label="Effects voice name" value={effectsName} maxLength={80} onChange={event => setEffectsName(event.target.value)} /></Field>
      {selected?.kind === 'effects' && <Toggle label={'Update “' + selected.name + '”'} description="Turn off to save a separate voice." checked={replaceEffects} onChange={setReplaceEffects} />}
    </Modal>
    <Modal open={!!nameAction} onClose={closeName} title={nameAction?.action === 'profileDuplicate' ? 'Duplicate voice' : 'Rename voice'}
      footer={<><Button onClick={closeName} disabled={blocked}>Cancel</Button><Button variant="primary" disabled={blocked || !nameAction?.name.trim()} onClick={() => void execute(nameAction!.action, async () => {
        await command(nameAction!.action, { id: nameAction!.id, name: nameAction!.name }); setNameAction(null);
      })}>Save</Button></>}><Field label="Voice name"><input aria-label="Voice profile name" value={nameAction?.name || ''} maxLength={80}
        onChange={event => setNameAction(action => action ? { ...action, name: event.target.value } : null)} /></Field></Modal>
    <ConfirmDialog open={!!deleteProfile} title="Delete this voice?" description={`“${deleteProfile?.name || ''}” will be removed from the local voice library. The original imported file is kept.`}
      onClose={closeDelete} onConfirm={async () => { await command('profileDelete', { id: deleteProfile.id }); }} />

    <Modal wide open={editorOpen} onClose={closeEditor} title="Create a reference voice" description="Choose the part of a clip that sounds most like the voice you want."
      footer={<><Button onClick={closeEditor} disabled={blocked}>Close</Button><Button variant="primary" busy={busy === 'clipSave'} disabled={blocked || !clipCanSave || !clip}
        onClick={() => void execute('clipSave', async () => { await saveTrim(); await command('clipSave', { name: editorName });
          setClipBaseline({ id: clip.id, ...trim, name: editorName }); setEditorOpen(false); setTab('library'); })}><Icon name="save" size={17} />Save reference voice</Button></>}>
      {error && <Notice tone="danger">{error}</Notice>}
      {status.busy && <Notice title={status.message}><Button variant="ghost" onClick={() => { void command('cancel').catch(() => {}); }}>Cancel operation</Button></Notice>}
      {!clip ? <EmptyState icon="waves" title="Start with a speech clip" description="Choose a local audio file or a downloaded video. Clear speech from one person works best."
        action={<Button variant="primary" busy={busy === 'clipBrowse'} disabled={blocked} onClick={() => void run('clipBrowse')}><Icon name="folder" size={17} />Choose audio or video</Button>} /> : <div className="voice-editor-content">
        <div className="voice-editor-file"><span><Icon name="audio" /><strong>{clip.name}</strong><span className="muted">{(clipDuration / 1000).toFixed(1)} s total</span></span>
          <div className="voice-library-actions"><Button variant="ghost" disabled={blocked} onClick={() => transition(() => { void run('clipBrowse'); }, ['clip'])}>Choose another clip</Button>
            <Button variant="ghost" disabled={blocked} onClick={() => transition(() => { void run('clipClear'); }, ['clip'])}>Clear editor</Button></div></div>
        <VoiceWaveform bins={clip.waveform || []} durationMs={clipDuration} startMs={trim.startMs} endMs={trim.endMs} positionMs={status.clipPositionMs || trim.startMs}
          disabled={blocked} onChange={setTrim} />
        <div className="voice-trim-fields"><Field label="Start (seconds)"><input aria-label="Reference clip start in seconds" type="number" min={0} max={clipDuration / 1000} step="0.001" value={trim.startMs / 1000}
          disabled={blocked} onChange={event => setTrim({ ...trim, startMs: Math.max(0, Math.round(Number(event.target.value) * 1000)) })} /></Field>
          <Field label="End (seconds)"><input aria-label="Reference clip end in seconds" type="number" min={0} max={clipDuration / 1000} step="0.001" value={trim.endMs / 1000}
            disabled={blocked} onChange={event => setTrim({ ...trim, endMs: Math.min(clipDuration, Math.round(Number(event.target.value) * 1000)) })} /></Field>
          <div className="voice-selection-length"><strong>{(selectionMs / 1000).toFixed(3)} s</strong><span className="muted">selected · 1–30 s to save</span></div></div>
        <Field label="Audition position" hint={status.clipPlaying ? 'Pause to choose another starting point.' : 'Choose a starting point within the selection.'}>
          <input aria-label="Reference audition position" type="range" min={trim.startMs} max={Math.max(trim.startMs, trim.endMs)} step={10}
            disabled={blocked || status.clipPlaying} value={status.clipPlaying ? Math.max(trim.startMs, Math.min(trim.endMs, status.clipPositionMs || trim.startMs)) : Math.max(trim.startMs, Math.min(trim.endMs, playhead))}
            onChange={event => setPlayhead(Number(event.target.value))} />
        </Field>
        <div className="voice-editor-transport"><Button disabled={blocked || selectionMs < 1000 || selectionMs > 300_000} onClick={() => void execute('clipPlay', async () => {
          if (status.clipPlaying) { setPlayhead(status.clipPositionMs || trim.startMs); await command('clipPause'); }
          else { await saveTrim(); await command('clipPlay', { positionMs: Math.max(trim.startMs, Math.min(trim.endMs, playhead)) }); }
        })}><Icon name={status.clipPlaying ? 'pause' : 'play'} size={17} />{status.clipPlaying ? 'Pause' : 'Play selection'}</Button>
          <span className="field-hint">Audition uses the Windows default playback device.</span></div>
        {status.clipError && <Notice tone="danger">{status.clipError}</Notice>}
        <details className="voice-cleanup"><summary><Icon name="sliders" size={18} />Speech cleanup <span className="muted">Optional</span></summary>
          <p className="muted">Separate speech from background music locally. Audition the result before saving.</p>
          <Button disabled={blocked || !state.musicRemovalAvailable || selectionMs < 1000 || selectionMs > 300_000} onClick={() => void execute('clipClean', async () => { await saveTrim(); await command('clipClean'); })}>
            <Icon name="waves" size={17} />Remove background music</Button>
          {!state.musicRemovalAvailable && <p className="field-hint">The dialogue-separation model is not installed in this build.</p>}
          {clip.hasCleaned && <Toggle label="Use cleaned speech" description="Applies to audition and the saved reference clip." checked={clip.useCleaned === true}
            disabled={blocked} onChange={enabled => void run('clipUseCleaned', { enabled })} />}</details>
        <Field label="Voice name" hint="Your trimmed reference is saved locally. The original audio or video stays unchanged."><input aria-label="Reference voice name" value={editorName} maxLength={80} disabled={blocked}
          onChange={event => setEditorName(event.target.value)} /></Field>
        {(selectionMs < 1000 || selectionMs > 30_000) && <Notice tone="warning">Select between 1 and 30 seconds to save a reference voice. Longer selections, up to five minutes, can be auditioned or cleaned first.</Notice>}
      </div>}
    </Modal>
    <DiscardDialog open={nextAction !== null} onKeep={() => setNextAction(null)} onDiscard={() => { const action = nextAction; setNextAction(null); action?.(); }} />
  </div>;
}

function VoiceWaveform({ bins, durationMs, startMs, endMs, positionMs, disabled, onChange }: {
  bins: { minimum: number; maximum: number }[]; durationMs: number; startMs: number; endMs: number; positionMs: number;
  disabled: boolean; onChange: (range: { startMs: number; endMs: number }) => void;
}) {
  const surface = useRef<HTMLDivElement>(null);
  const selectionDrag = useRef<{ pointer: number; x: number; start: number; end: number; span: number } | null>(null);
  const [zoom, setZoom] = useState(false);
  const [viewOffset, setViewOffset] = useState<number | null>(null);
  const span = zoom ? Math.min(durationMs, Math.max(8000, (endMs - startMs) * 1.5 + 2000)) : durationMs;
  const viewStart = zoom ? Math.max(0, Math.min(durationMs - span, viewOffset ?? (startMs + endMs - span) / 2)) : 0;
  const left = (value: number) => Math.max(0, Math.min(100, (value - viewStart) / Math.max(1, span) * 100));
  const bars = useMemo(() => {
    const start = Math.floor(viewStart / Math.max(1, durationMs) * bins.length);
    const finish = Math.min(bins.length, Math.ceil((viewStart + span) / Math.max(1, durationMs) * bins.length));
    const count = Math.min(800, Math.max(1, finish - start));
    return Array.from({ length: count }, (_, index) => {
      let minimum = 0, maximum = 0;
      const first = start + Math.floor(index / count * (finish - start));
      const last = Math.max(first + 1, start + Math.ceil((index + 1) / count * (finish - start)));
      for (let cursor = first; cursor < last && cursor < bins.length; cursor++) {
        minimum = Math.min(minimum, bins[cursor]?.minimum || 0); maximum = Math.max(maximum, bins[cursor]?.maximum || 0);
      }
      return { x: index / count * 1000, top: 70 - maximum * 58, bottom: 70 - minimum * 58 };
    });
  }, [bins, durationMs, viewStart, span]);
  function move(which: 'start' | 'end', value: number) {
    value = Math.round(Math.max(0, Math.min(durationMs, value)));
    onChange(which === 'start' ? { startMs: Math.min(value, endMs - 1000), endMs } : { startMs, endMs: Math.max(startMs + 1000, value) });
  }
  function key(event: KeyboardEvent<HTMLButtonElement>, which: 'start' | 'end') {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault(); const value = which === 'start' ? startMs : endMs;
    move(which, event.key === 'Home' ? 0 : event.key === 'End' ? durationMs : value + (event.key === 'ArrowRight' ? 1 : -1) * (event.shiftKey ? 1000 : 100));
  }
  function drag(event: PointerEvent<HTMLButtonElement>, which: 'start' | 'end') {
    if (!event.currentTarget.hasPointerCapture(event.pointerId) || disabled) return;
    const rect = surface.current?.getBoundingClientRect(); if (!rect) return;
    move(which, viewStart + (event.clientX - rect.left) / rect.width * span);
  }
  return <div className="voice-wave-editor"><div className="voice-wave-toolbar"><span className="muted">Drag an edge to trim, or drag the selection to move it.</span><Button variant="ghost" onClick={() => { setZoom(!zoom); setViewOffset(null); }} disabled={disabled}>{zoom ? 'Show full clip' : 'Zoom to selection'}</Button></div>
    <div className="voice-wave-surface" ref={surface}>
      <svg viewBox="0 0 1000 140" preserveAspectRatio="none" role="img" aria-label="Reference audio waveform"><line x1="0" x2="1000" y1="70" y2="70" stroke="var(--line)" />
        {bars.map((bar, index) => <line key={index} x1={bar.x} x2={bar.x} y1={bar.top} y2={Math.max(bar.top + 1, bar.bottom)} stroke="var(--accent)" strokeWidth="1.5" />)}</svg>
      <div className="voice-wave-shade" style={{ left: 0, width: left(startMs) + '%' }} /><div className="voice-wave-shade" style={{ left: left(endMs) + '%', right: 0 }} />
      <div className="voice-wave-selection" style={{ left: left(startMs) + '%', width: Math.max(0, left(endMs) - left(startMs)) + '%', pointerEvents: disabled ? 'none' : 'auto', cursor: 'grab', touchAction: 'none' }}
        onPointerDown={event => { if (disabled) return; event.currentTarget.setPointerCapture(event.pointerId); selectionDrag.current = { pointer: event.pointerId, x: event.clientX, start: startMs, end: endMs, span }; event.preventDefault(); }}
        onPointerMove={event => {
          const drag = selectionDrag.current, bounds = surface.current?.getBoundingClientRect();
          if (!drag || drag.pointer !== event.pointerId || !bounds || disabled) return;
          const delta = Math.round((event.clientX - drag.x) / bounds.width * drag.span);
          const start = Math.max(0, Math.min(durationMs - (drag.end - drag.start), drag.start + delta));
          onChange({ startMs: start, endMs: start + drag.end - drag.start });
        }} onPointerUp={event => { selectionDrag.current = null; if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId); }}
        onPointerCancel={() => { selectionDrag.current = null; }} />
      {(['start', 'end'] as const).map(which => <button key={which} type="button" className={'voice-wave-handle voice-wave-' + which} role="slider"
        aria-label={'Reference selection ' + which} aria-valuemin={0} aria-valuemax={durationMs / 1000} aria-valuenow={(which === 'start' ? startMs : endMs) / 1000}
        aria-valuetext={seconds(which === 'start' ? startMs : endMs) + ' seconds'} disabled={disabled} style={{ left: left(which === 'start' ? startMs : endMs) + '%' } as CSSProperties}
        onKeyDown={event => key(event, which)} onPointerDown={event => { if (!disabled) { event.currentTarget.setPointerCapture(event.pointerId); event.preventDefault(); } }}
        onPointerMove={event => drag(event, which)} onPointerUp={event => { if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId); }}><span>{which === 'start' ? 'Start' : 'End'}</span></button>)}
      {positionMs >= viewStart && positionMs <= viewStart + span && <div className="voice-wave-playhead" style={{ left: left(positionMs) + '%' }} />}
    </div><div className="voice-wave-ruler"><span>{seconds(viewStart)} s</span><span>{seconds(viewStart + span)} s</span></div>
    {zoom && durationMs > span && <Field label="Move waveform view"><input type="range" aria-label="Waveform view position" min={0} max={durationMs - span}
      step={100} value={viewStart} disabled={disabled} onChange={event => setViewOffset(Number(event.target.value))} /></Field>}
  </div>;
}

const voiceCss = `
.voice-page .muted{color:var(--muted)}.voice-workbench{display:grid;grid-template-columns:minmax(280px,.8fr) minmax(400px,1.2fr);gap:22px}.voice-route-card,.voice-output-card{display:flex;flex-direction:column;gap:20px}.voice-card-title{display:flex;align-items:center;gap:12px}.voice-card-title h2,.voice-card-title p{margin:0}.voice-card-title p{margin-top:5px}.voice-step-number{display:grid;place-items:center;width:30px;height:30px;border:1px solid var(--line);border-radius:50%;font-size:14px;font-weight:700;color:var(--accent);flex:none}.voice-path{display:flex;align-items:center;gap:12px;color:var(--muted);padding:0 10px}.voice-path span{height:1px;flex:1;background:var(--line)}.voice-output-card{background:var(--surface);}.voice-output-heading{display:flex;justify-content:space-between;align-items:center}.voice-large-icon{width:68px;height:68px;border-radius:22px;background:var(--icon-bg);display:grid;place-items:center;color:var(--accent)}.voice-output-card>h2{font-size:27px;letter-spacing:-.6px;margin:0}.voice-output-card>p{margin:0;line-height:1.6}.voice-meter-pair{display:grid;grid-template-columns:1fr 1fr;gap:24px;padding:8px 0}.voice-destination-title{display:flex;align-items:center;gap:12px}.voice-destination-title h3{margin:0;font-size:16px}.voice-destinations{display:grid;grid-template-columns:1fr 1fr;gap:12px}.voice-destination{border:1px solid var(--line);border-radius:14px;display:flex;align-items:center;gap:12px;padding:16px;text-align:left;background:var(--surface);color:var(--ink);font:inherit;cursor:pointer}.voice-destination.selected{border-color:var(--accent);background:var(--selected);box-shadow:0 0 0 1px var(--accent)}.voice-destination span{flex:1;display:grid;gap:5px}.voice-destination strong{font-size:14px}.voice-destination small{font-size:14px;color:var(--muted)}.voice-start-row{display:flex;gap:10px;align-items:center}.voice-session-message{font-size:14px;color:var(--muted);min-height:22px}.voice-presets{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin-bottom:22px}.voice-preset{padding:17px 14px;border:1px solid var(--line);border-radius:12px;background:var(--surface);font:inherit;color:var(--ink);text-align:left;cursor:pointer;display:grid;gap:7px}.voice-preset.selected{border-color:var(--accent);background:var(--selected)}.voice-preset span{font-size:14px;color:var(--muted)}.voice-tuning{display:flex;align-items:end;gap:25px}.voice-tuning .field{flex:1}.voice-tuning .field label span{display:flex;justify-content:space-between;gap:10px}.voice-tuning .field label strong{font-weight:500;color:var(--accent);white-space:nowrap}.voice-library-intro{display:flex;align-items:center;justify-content:space-between;gap:25px;padding:30px}.voice-library-intro h2{margin-top:12px;font-size:24px;letter-spacing:-.4px}.voice-library-intro p{line-height:1.6;max-width:530px}.voice-library-actions{display:flex;flex-direction:column;gap:10px;flex:none}.voice-library-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:18px}.voice-library-card{display:flex;flex-direction:column;gap:17px}.voice-library-card h3,.voice-library-card p{margin:0}.voice-library-card-top{display:flex;align-items:center;justify-content:space-between}.voice-profile-avatar{display:grid;place-items:center;width:55px;height:55px;border-radius:18px;background:var(--icon-bg);color:var(--accent)}.voice-profile-avatar.reference{background:var(--icon-bg);color:var(--navy)}.voice-library-card-actions{display:flex;justify-content:space-between;align-items:center;margin-top:auto}.voice-page button:disabled{cursor:not-allowed}.voice-editor-content{display:grid;gap:22px}.voice-editor-file{display:flex;align-items:center;justify-content:space-between;gap:15px}.voice-editor-file>span{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.voice-trim-fields{display:grid;grid-template-columns:1fr 1fr auto;gap:20px;align-items:end}.voice-selection-length{display:grid;gap:7px;padding-bottom:9px;white-space:nowrap}.voice-selection-length strong{font-size:22px;color:var(--accent)}.voice-editor-transport{display:flex;align-items:center;gap:15px}.voice-cleanup{border:1px solid var(--line);border-radius:12px;padding:15px}.voice-cleanup summary{display:flex;align-items:center;gap:9px;font-weight:600;cursor:pointer}.voice-cleanup summary .muted{margin-left:auto;font-weight:400}.voice-cleanup[open]>p{margin:15px 0}.voice-wave-toolbar{display:flex;align-items:center;justify-content:space-between;margin-bottom:25px}.voice-wave-surface{position:relative;background:var(--surface-inset);border-radius:8px;border:1px solid var(--line);height:150px;margin:0 10px}.voice-wave-surface svg{width:100%;height:100%;display:block}.voice-wave-shade{position:absolute;top:0;bottom:0;background:var(--wave-shade);pointer-events:none}.voice-wave-selection{position:absolute;top:0;bottom:0;background:#98f9c31c;border-top:2px solid var(--accent);border-bottom:2px solid var(--accent);pointer-events:none}.voice-wave-handle{position:absolute;top:0;bottom:0;transform:translateX(-50%);border:0;width:15px;background:transparent;cursor:ew-resize;touch-action:none;z-index:2}.voice-wave-handle:before{content:'';position:absolute;inset:0 6px;background:var(--accent)}.voice-wave-handle span{position:absolute;top:-25px;font-size:14px;font-weight:600;color:var(--accent);white-space:nowrap}.voice-wave-start span{left:5px}.voice-wave-end span{right:5px}.voice-wave-playhead{position:absolute;top:0;bottom:0;border-left:1px solid var(--navy);pointer-events:none;z-index:1}.voice-wave-ruler{display:flex;justify-content:space-between;font-size:14px;color:var(--muted);margin-top:9px}.voice-page input[type=range]{width:100%;accent-color:var(--accent)}@media(max-width:1200px){.voice-workbench{grid-template-columns:1fr}.voice-route-card{display:grid;grid-template-columns:1fr 1fr;align-items:center}.voice-route-card .voice-path{display:none}.voice-library-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.voice-presets{grid-template-columns:repeat(3,1fr)}}@media(max-width:850px){.voice-destinations,.voice-meter-pair,.voice-library-grid,.voice-route-card{grid-template-columns:1fr}.voice-library-intro,.voice-tuning{display:flex;align-items:stretch;flex-direction:column}.voice-presets{grid-template-columns:repeat(2,1fr)}.voice-trim-fields{grid-template-columns:1fr 1fr}.voice-selection-length{grid-column:1/-1}.voice-editor-file{align-items:flex-start;flex-direction:column}.voice-tuning .field{width:100%}}
`;
