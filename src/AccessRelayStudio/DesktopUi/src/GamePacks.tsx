import { useEffect, useState } from 'react';
import { Button, Card, DiscardDialog, EmptyState, Field, Notice, useUnsavedChanges, type PageProps } from './ui';

export function GamePacks({ state, status, command }: PageProps) {
  const packs: any[] = state.gamePacks || [];
  const [key, setKey] = useState(packs[0]?.key || ''), [next, setNext] = useState<string | null>(null);
  const pack = packs.find(item => item.key === key), manifest = pack?.manifest;
  const current = state.activeProfile?.gamePack;
  const matches = !!manifest && current?.id === manifest.id && current?.version === manifest.version && current?.manifestSha256 === pack.manifestSha256;
  const saved = JSON.stringify(matches ? current.tuning : Object.fromEntries((manifest?.tuning || []).map((control: any) => [control.id, control.default])));
  const [values, setValues] = useState<Record<string, number>>(JSON.parse(saved)), [baseline, setBaseline] = useState(saved);
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState('');
  const dirty = JSON.stringify(values) !== baseline;
  useUnsavedChanges(dirty);
  useEffect(() => { setValues(JSON.parse(saved)); setBaseline(saved); setError(''); setMessage(''); }, [key, state.activeProfile?.id]);
  useEffect(() => { if (!dirty) { setValues(JSON.parse(saved)); setBaseline(saved); } }, [saved]);
  useEffect(() => { if (!packs.some(item => item.key === key)) setKey(packs[0]?.key || ''); }, [JSON.stringify(packs.map(item => item.key))]);
  const locked = !!busy || status.training || status.recording?.isRecording || !!status.recording?.pending;
  const execute = async (action: string, args?: any) => {
    setBusy(action); setError(''); setMessage('');
    try { const result = await command(action, args); setMessage(result?.message || 'Game pack updated.'); if (action === 'packTune') setBaseline(JSON.stringify(values)); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(''); }
  };
  return <div className="page-stack"><Card title="Game packs" description="Optional game-specific Lua mappings and tuning. Create a separate controller setup so existing mappings remain available.">
    <div className="stack"><p className="subtle">Import packs only from sources you trust. Packs may contain Lua scripts and ONNX models; they cannot supply native plugins. Resource hashes detect changed bytes and do not identify a trustworthy publisher. Importing activates nothing.</p>
      <Button disabled={locked} onClick={() => void execute('packImport')}>Import pack.json</Button>
      {(state.gamePackErrors || []).map((issue: string, index: number) => <Notice tone="warning" key={index}>{issue}</Notice>)}
      {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
      {!packs.length ? <EmptyState title="No game packs installed" description="Import a pack manifest from a folder containing its declared resources." /> : <>
        <Field label="Installed game pack"><select value={key} disabled={locked} onChange={event => { if (dirty) setNext(event.target.value); else setKey(event.target.value); }}>{packs.map(item => <option key={item.key} value={item.key}>{item.manifest.name} · {item.manifest.version}</option>)}</select></Field>
        {manifest && <><p>{manifest.description}</p><p className="subtle">License: {manifest.license}. {manifest.assets.length} declared resources. {matches ? 'This controller setup is linked to this pack and version.' : 'This pack will create a new setup with sequences off.'}</p>
          {!!manifest.presets?.length && <Field label="Pack tuning preset"><select value="" disabled={locked} onChange={event => { const preset = manifest.presets[Number(event.target.value)]; if (preset) setValues({ ...Object.fromEntries(manifest.tuning.map((control: any) => [control.id, control.default])), ...preset.values }); }}><option value="">Choose a preset</option>{manifest.presets.map((preset: any, index: number) => <option key={index} value={index}>{preset.name}</option>)}</select></Field>}
          <div className="form-grid">{manifest.tuning.map((control: any) => <Field key={control.id} label={control.label} hint={control.kind === 'range' ? String(values[control.id] ?? control.default) : undefined}>
            {control.kind === 'choice' ? <select disabled={locked} value={values[control.id] ?? control.default} onChange={event => setValues({ ...values, [control.id]: Number(event.target.value) })}>{control.choices.map((choice: any) => <option key={choice.value} value={choice.value}>{choice.label}</option>)}</select>
              : <input type="range" min={control.minimum} max={control.maximum} step={control.step} disabled={locked} value={values[control.id] ?? control.default} onChange={event => setValues({ ...values, [control.id]: Number(event.target.value) })} />}
          </Field>)}</div>
          <div className="row"><Button variant="primary" disabled={locked} onClick={() => void execute('packCreateSetup', { key, values })}>Create setup from pack</Button>
            <Button disabled={locked || !matches || !dirty} onClick={() => void execute('packTune', { key, values })}>Save pack tuning</Button>
            <Button disabled={locked || !dirty} onClick={() => { setValues(JSON.parse(saved)); setBaseline(saved); }}>Discard pack edits</Button></div>
          <p className="subtle">Pack tuning updates imported Lua scripts. Edited script sources need a fresh setup. Saving pauses sequences and disarms assistance; enable sequences explicitly when ready. Models retain Remote Play's compatibility, accuracy and calibration requirements.</p>
          {(pack.models || []).map((path: string) => <Button key={path} disabled={locked} onClick={() => void execute('packModelImport', { key, path })}>Import model {path.split('/').at(-1)} into Remote Play</Button>)}
          <details><summary>Review pack resources and integrity</summary><p className="subtle">Manifest SHA-256: {pack.manifestSha256}</p><ul>{manifest.assets.map((asset: any) => <li key={asset.path}>{asset.path} · {asset.kind} · {asset.bytes} bytes</li>)}</ul></details>
        </>}
      </>}
    </div></Card><DiscardDialog open={next !== null} onKeep={() => setNext(null)} onDiscard={() => { setKey(next!); setNext(null); }} /></div>;
}
