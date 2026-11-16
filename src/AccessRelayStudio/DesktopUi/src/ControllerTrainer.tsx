import { useEffect, useState } from 'react';
import { Button, Card, DiscardDialog, Field, Input, Notice, Select, SelectOption, Slider, Tabs, Textarea,
  hasUnsavedChanges, useUnsavedChanges, type PageProps } from './ui';

type Shot = { id: string; label: string; instructions: string };
type Feedback = { id: string; label: string; adjustmentMs: number };
type Definition = { shots: Shot[]; timingFeedback: Feedback[]; tempoFeedback: Feedback[] };
type Props = PageProps & { pack: any };

export function ControllerTrainer(props: Props) {
  const definition: Definition = props.pack.manifest.controllerTrainer;
  const context = props.status.trainerRecording;
  const [shotId, setShotId] = useState(context?.key === props.pack.key ? context.shotId : definition.shots[0].id);
  const [next, setNext] = useState<string | null>(null);
  const shot = definition.shots.find(item => item.id === shotId) || definition.shots[0];
  return <div className="page-stack">
    <Tabs ariaLabel="Shot types" value={shot.id} items={definition.shots.map(item => ({ id: item.id, label: item.label }))}
      onChange={id => { if (id !== shot.id) { if (hasUnsavedChanges()) setNext(id); else setShotId(id); } }} />
    <ShotTrainer key={props.state.activeProfile.id + ':' + props.pack.key + ':' + shot.id} {...props} definition={definition} shot={shot} />
    <DiscardDialog open={next !== null} onKeep={() => setNext(null)} onDiscard={() => { setShotId(next!); setNext(null); }} />
  </div>;
}

function ShotTrainer({ state, status, command, pack, definition, shot }: Props & { definition: Definition; shot: Shot }) {
  const script = state.activeProfile.luaScripts?.find((item: any) => item.training?.packId === pack.manifest.id && item.training.shotId === shot.id);
  const training = script?.training;
  const assignment = state.activeProfile.bindings?.find((item: any) => item.luaScriptId === script?.id && item.enabled);
  const [input, setInput] = useState(training?.shotInput || 'rightStick');
  const [baselineTiming, setBaselineTiming] = useState(''), [baselineTempo, setBaselineTempo] = useState('');
  const [timing, setTiming] = useState(''), [tempo, setTempo] = useState('');
  const [hold, setHold] = useState(String(training?.holdMs ?? '')), [stroke, setStroke] = useState(String(training?.tempoMs ?? ''));
  const [control, setControl] = useState(assignment?.control || '');
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState(''), [source, setSource] = useState('');
  const saved = JSON.stringify([training?.holdMs, training?.tempoMs]);
  useEffect(() => { setHold(String(training?.holdMs ?? '')); setStroke(String(training?.tempoMs ?? '')); setSource(''); }, [saved]);
  useEffect(() => { setControl(assignment?.control || ''); }, [assignment?.control]);
  const dirty = !!training && (hold !== String(training.holdMs) || stroke !== String(training.tempoMs ?? ''));
  useUnsavedChanges(dirty || !!baselineTiming || !!baselineTempo || !!timing || !!tempo || control !== (assignment?.control || ''));
  const recording = status.recording || {};
  const context = status.trainerRecording;
  const ownsRecording = context?.key === pack.key && context?.profileId === state.activeProfile.id && context?.shotId === shot.id;
  const taking = ownsRecording && recording.isRecording;
  const pending = ownsRecording && !!recording.pending;
  const locked = !!busy || !!status.training || !!recording.isRecording || !!recording.pending;
  const editable = !!training && training.sourceMatches && !locked;
  const selectedInput = ownsRecording ? context.shotInput : input;
  const hasTempo = training?.tempoMs != null;
  const act = async (action: string, args?: any, success?: string) => {
    setBusy(action); setError(''); setMessage('');
    try { const result = await command(action, args); if (success) setMessage(success); return { result }; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return null; }
    finally { setBusy(''); }
  };
  const validDuration = (value: string, min: number) => value.trim() !== '' && Number.isInteger(Number(value)) && Number(value) >= min && Number(value) <= 10000;
  const durationValid = training && validDuration(hold, training.minimumHoldMs) && (!hasTempo || validDuration(stroke, training.minimumTempoMs));
  const timingDelta = definition.timingFeedback.find(item => item.id === timing)?.adjustmentMs || 0;
  const tempoDelta = definition.tempoFeedback.find(item => item.id === tempo)?.adjustmentMs || 0;
  const feedbackReady = !!timing && (!hasTempo || !!tempo);
  const labelFor = (list: Feedback[], value: string) => list.find(item => item.id === value)?.label || value;
  return <div className="page-stack">
    {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
    <Card title={shot.label} description={shot.instructions}>
      <div className="stack">
        <h3>1. Record your baseline</h3>
        <p className="subtle">Record one complete shot, then release the shot button or return the right stick to center. Only the selected shot input is recorded and replayed. Move and dribble manually; other controls stay live during playback. Idle time before and after the shot is removed.</p>
        <Field label="Shot input used in the attempt"><Select value={selectedInput} disabled={locked} onValueChange={setInput}>
          <SelectOption value="rightStick">Right stick · down, then up or release</SelectOption>
          {['x', 'a', 'b', 'y'].map(value => <SelectOption key={value} value={value}>{value.toUpperCase()} button</SelectOption>)}
        </Select></Field>
        <div className="row"><Button active={taking} busy={busy === 'trainerStart' || busy === 'recordStop'}
          disabled={!!busy || (taking ? false : locked || !status.connected)} onClick={() => {
            void act(taking ? 'recordStop' : 'trainerStart', taking ? undefined : { key: pack.key, shotId: shot.id, shotInput: input });
          }}>{taking ? 'Stop baseline recording' : training ? 'Record a new baseline' : 'Record baseline'}</Button>
          {(taking || pending) && <Button disabled={!!busy} onClick={async () => {
            if (await act('recordDiscard', undefined, 'Attempt discarded. Your saved baseline is kept.')) { setBaselineTiming(''); setBaselineTempo(''); }
          }}>Discard attempt</Button>}
        </div>
        {taking && <Notice>Recording {Math.round(recording.elapsedMs || 0)} ms · {recording.hasInput ? 'Physical input captured.' : 'Waiting for physical input.'}</Notice>}
        {recording.note && ownsRecording && <Notice tone="warning">{recording.note}</Notice>}
        {(recording.isRecording || recording.pending) && !ownsRecording && <Notice tone="warning">Finish the recording in its shot tab or in Scripts before starting another baseline.</Notice>}
        {!status.connected && <p className="subtle">Connect a physical controller to record a baseline.</p>}
        {training && <Notice>Shot playback: {training.durationMs} ms. Original baseline retained: {training.baselineDurationMs} ms, {training.baselineFrames} physical samples. Original feedback: {labelFor(definition.timingFeedback, training.baselineTimingFeedback)} timing; {labelFor(definition.tempoFeedback, training.baselineTempoFeedback)} tempo. Recording a replacement keeps this script until conversion succeeds.</Notice>}
        {pending && <div className="stack">
          <h3>2. Enter the baseline feedback and convert</h3>
          <p className="subtle">Captured {recording.pending.durationMs} ms and {recording.pending.frameCount} samples. Select what the game reported. Conversion keeps the recorded timing as your baseline.</p>
          <div className="form-grid">
            <FeedbackSelect label="Baseline Shot Timing" choices={definition.timingFeedback} value={baselineTiming} disabled={!!busy} onChange={setBaselineTiming} />
            <FeedbackSelect label="Baseline Shot Tempo" choices={definition.tempoFeedback} value={selectedInput === 'rightStick' ? baselineTempo : 'none'} disabled={!!busy || selectedInput !== 'rightStick'} onChange={setBaselineTempo} />
          </div>
          <Button variant="primary" busy={busy === 'trainerConvert'} disabled={!!busy || !baselineTiming || (selectedInput === 'rightStick' && !baselineTempo)} onClick={async () => {
            const converted = await act('trainerConvert', { key: pack.key, shotId: shot.id, timingFeedback: baselineTiming, tempoFeedback: selectedInput === 'rightStick' ? baselineTempo : 'none' }, 'Baseline saved as Lua. Choose its controller assignment below.');
            if (converted) {
              setBaselineTiming(''); setBaselineTempo(''); setTiming(''); setTempo(''); setSource('');
              if (converted.result?.training) { setHold(String(converted.result.training.holdMs)); setStroke(String(converted.result.training.tempoMs ?? '')); }
            }
          }}>Convert baseline to Lua</Button>
        </div>}
      </div>
    </Card>
    {training && <>
      {!training.sourceMatches && <Notice tone="warning">This Lua script has custom edits. Record a new baseline to use trainer adjustments.</Notice>}
      <Card title="3. Assign this shot" description="Tap the assigned input once to play the complete shot. You can release the paddle or button immediately.">
        <div className="stack"><Field label="Controller key, input or paddle"><Select value={control} disabled={!editable} onValueChange={setControl}>
          <SelectOption value="">Choose an input</SelectOption>
          {(state.controls || []).map((item: any) => {
            const mapped = state.activeProfile.bindings?.find((binding: any) => binding.control === item.id);
            const occupied = mapped?.script !== 'passthrough' && mapped?.script != null && mapped?.luaScriptId !== script.id;
            const unavailable = /^(small|large)/.test(item.id) && !status.supportsEliteKeypad;
            return <SelectOption key={item.id} value={item.id} disabled={occupied || unavailable}>{item.label}{occupied ? ' · already assigned' : unavailable ? ' · unavailable' : ''}</SelectOption>;
          })}
        </Select></Field>
          <Button busy={busy === 'trainerAssign'} disabled={!editable || !control || control === assignment?.control} onClick={() => void act('trainerAssign', { key: pack.key, id: script.id, control }, 'Shot assigned. Tap once to play the complete shot; Run scripts keeps its current state.')}>Assign shot</Button>
          {assignment && <Notice>Assigned to {assignment.label || assignment.control}. Tap once; the shot completes after you release the input. {status.outputActive && status.scriptsEnabled ? 'Scripts are on.' : 'Start controller and enable Run scripts when ready to practice.'}</Notice>}
        </div>
      </Card>
      {assignment && <Card title="4. Practice and adjust" description="Attempt this shot using its assigned input, then enter the latest in-game feedback.">
        <div className="stack"><div className="form-grid">
          <FeedbackSelect label="Shot Timing" choices={definition.timingFeedback} value={timing} disabled={!editable || dirty} onChange={setTiming} />
          <FeedbackSelect label="Shot Tempo" choices={definition.tempoFeedback} value={hasTempo ? tempo : 'none'} disabled={!editable || dirty || !hasTempo} onChange={setTempo} />
        </div>
          {!hasTempo && <p className="subtle">This attempt has no measured down-to-up stick stroke. Tempo does not change a button hold or a stick release. To tune tempo, record a down-to-up stroke with intermediate movement samples.</p>}
          <p className="subtle">{feedbackReady ? `Next adjustment: hold ${timingDelta >= 0 ? '+' : ''}${timingDelta} ms${hasTempo ? `; stick stroke ${tempoDelta >= 0 ? '+' : ''}${tempoDelta} ms` : ''}.` : 'Choose the latest feedback to preview the adjustment.'} Each application adjusts only this shot and keeps Run scripts in its current state. These are practice corrections; game outcomes can vary.</p>
          <Button busy={busy === 'trainerTune'} disabled={!editable || dirty || !feedbackReady} onClick={async () => {
            if (await act('trainerTune', { key: pack.key, id: script.id, feedback: true, timingFeedback: timing, tempoFeedback: hasTempo ? tempo : 'none' }, 'Feedback applied to this shot’s Lua. Run scripts keeps its current state.')) { setTiming(''); setTempo(''); setSource(''); }
          }}>Apply shot feedback</Button>
          <h3>Manual durations</h3>
          <Duration label="Hold before release (ms)" value={hold} min={training.minimumHoldMs} disabled={!editable} onChange={setHold} />
          {hasTempo && <Duration label="Stick stroke duration (ms)" value={stroke} min={training.minimumTempoMs} disabled={!editable} onChange={setStroke} />}
          <p className="subtle">Drag a slider or type whole milliseconds. Hold controls how long the recorded stick or button stays down before release. Stick stroke controls the captured down-to-up travel. Only this shot input is replayed. Other recorded shot phases retain their lengths; later samples move with your edited phase.</p>
          <div className="row"><Button disabled={!editable || !dirty || !durationValid} busy={busy === 'trainerTune'} onClick={async () => {
            if (await act('trainerTune', { key: pack.key, id: script.id, holdMs: Number(hold), tempoMs: hasTempo ? Number(stroke) : null }, 'Manual durations saved to Lua. Run scripts keeps its current state.')) { setTiming(''); setTempo(''); setSource(''); }
          }}>Save manual durations</Button><Button disabled={!dirty || !!busy} onClick={() => { setHold(String(training.holdMs)); setStroke(String(training.tempoMs ?? '')); }}>Discard duration edits</Button></div>
          {dirty && !durationValid && <Notice tone="warning">Enter whole milliseconds between the shown minimum and 10000. The complete script must remain within the recording limit.</Notice>}
        </div>
      </Card>}
      <Card title="Generated Lua"><div className="stack"><div className="row">
        <Button disabled={!!busy} onClick={async () => { const response = await act('luaGet', { id: script.id }); if (response) setSource(response.result.source); }}>Review Lua script</Button>
        <Button disabled={!!busy} onClick={() => void act('luaExport', { id: script.id })}>Export Lua script</Button>
      </div>{source && <Field label="Generated Lua source"><Textarea readOnly rows={12} value={source} spellCheck={false} /></Field>}</div></Card>
    </>}
  </div>;
}

function FeedbackSelect({ label, choices, value, disabled, onChange }: { label: string; choices: Feedback[]; value: string; disabled: boolean; onChange: (value: string) => void }) {
  return <Field label={label}><Select value={value} disabled={disabled} onValueChange={onChange}>
    <SelectOption value="">Choose the game's feedback</SelectOption>{choices.map(choice => <SelectOption key={choice.id} value={choice.id}>{choice.label}</SelectOption>)}
  </Select></Field>;
}

function Duration({ label, value, min, disabled, onChange }: { label: string; value: string; min: number; disabled: boolean; onChange: (value: string) => void }) {
  return <div className="form-grid"><Field label={label} hint={`Minimum ${min} ms; maximum 10000 ms`}>
    <Input type="number" min={min} max={10000} step={1} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />
  </Field><Field label={label + ' slider'}><Slider min={min} max={10000} step={1} value={Math.max(min, Math.min(10000, Number(value) || min))} disabled={disabled} onChange={event => onChange(event.target.value)} /></Field></div>;
}
