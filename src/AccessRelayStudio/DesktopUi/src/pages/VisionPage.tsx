import { useState } from 'react';
import { gameVisionWorkspace } from '../game-plugins/registry';
import { GameplayPreview, type GameplayWindowControls } from '../components/GameplayPreview';
import { Select, SelectOption, Button, Card, DiscardDialog, Field, Icon, Notice, SectionHeader, hasUnsavedChanges, type PageProps } from '../ui';

export type VisionMode = 'live' | 'training';
export type VisionWorkspaceProps = PageProps & { mode: VisionMode; captureWindow?: GameplayWindowControls };

export function VisionPage({ state = {}, status, command, mode = 'live', captureWindow, openMarketplace }: PageProps & { mode?: VisionMode; captureWindow?: GameplayWindowControls; openMarketplace?: () => void }) {
  const [busy, setBusy] = useState(''), [error, setError] = useState('');
  const [pending, setPending] = useState<{ action: string; id: string } | null>(null), [draftEpoch, setDraftEpoch] = useState(0);
  const live = status ?? state.status ?? {}, session = live.session ?? {};
  const sources = live.sources ?? state.sources ?? [], sourceId = live.selectedSourceId ?? state.selectedSourceId ?? '';
  const plugins = (state.plugins ?? []).filter((plugin: any) => gameVisionWorkspace(plugin.id));
  const pluginId = state.selectedPluginId ?? '', Plugin = plugins.some((plugin: any) => plugin.id === pluginId) ? gameVisionWorkspace(pluginId) : undefined;
  const saving = !!live.training?.hasTake && !live.training?.isFinalized;
  const locked = !!session.running || !!live.stopping || saving, working = !!busy || !!live.busy || live.trainingJob?.state === 'running';
  async function run(action: string, args?: any) {
    setError(''); setBusy(action);
    try { await command(action, args); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(''); }
  }
  const choose = (action: string, id: string) => { if (hasUnsavedChanges()) setPending({ action, id }); else void run(action, { id }); };
  return <div className="page-stack remote-play-page">
    <SectionHeader title={mode === 'training' ? 'AI Model Training' : 'Live Gameplay'} actions={<Field label={mode === 'training' ? 'Training plugin' : 'Game plugin'}><Select aria-label={mode === 'training' ? 'Training plugin' : 'Game plugin'} disabled={locked || working}
      value={pluginId} onValueChange={selectionValue => choose('pluginSelect', selectionValue)}>
      <SelectOption value="">None</SelectOption>{plugins.map((plugin: any) => <SelectOption key={plugin.id} value={plugin.id}>{plugin.name}</SelectOption>)}
    </Select></Field>} />
    {error && <Notice tone="danger">{error}</Notice>}
    <Card title="Capture settings"><div className="row remote-play-source"><Field label="Gameplay window" hint="A browser window captures its active tab.">
        <Select aria-label="Gameplay window" value={sourceId} disabled={locked || working}
          onFocus={() => { if (!locked && !working) command('sourceRefresh').catch(e => setError(e instanceof Error ? e.message : String(e))); }}
          onValueChange={selectionValue => choose('sourceSelect', selectionValue)}>
          <SelectOption value="">Choose a window</SelectOption>{sources.map((source: any) => <SelectOption key={source.id} value={source.id}>{source.title}</SelectOption>)}
        </Select></Field><Button aria-label="Refresh capture windows" disabled={locked || working} onClick={() => void run('sourceRefresh')}><Icon name="refresh" size={18} /></Button>
      </div>{mode === 'training' && <div className="form-grid training-folder-settings">{[['dataset', 'Training data folder', state.trainingSetup?.datasetRoot], ['workspace', 'Model output folder', state.trainingSetup?.workspaceRoot]].map(([kind, label, path]) => <Field key={kind} label={label}><Button disabled={locked || working} onClick={() => void run('trainingConfigure', { kind })}>{path ? 'Change…' : kind === 'python' ? 'Choose interpreter…' : 'Choose folder…'}</Button>{path && <span className="subtle training-folder-path" title={path}>{path}</span>}</Field>)}</div>}{mode === 'training' && <details className="training-folder-settings"><summary>Training environment</summary><Field label="Python interpreter"><Button disabled={locked || working} onClick={() => void run('trainingConfigure', { kind: 'python' })}>{state.trainingSetup?.python ? 'Change interpreter…' : 'Choose interpreter…'}</Button>{state.trainingSetup?.python && <span className="subtle training-folder-path" title={state.trainingSetup.python}>{state.trainingSetup.python}</span>}</Field></details>}</Card>
    {Plugin ? <Plugin key={pluginId + mode + draftEpoch} state={state} status={status} command={command} mode={mode} captureWindow={captureWindow} /> : mode === 'training'
      ? <Card title={plugins.length ? 'Choose a training plugin' : 'No training plugins installed'}><p className="subtle">{plugins.length ? 'Select an installed plugin above.' : 'Install a model training plugin from Marketplace.'}</p>
        {openMarketplace && <Button onClick={openMarketplace}><Icon name="store" size={17} />Browse Marketplace</Button>}</Card> : <>
      <GameplayPreview status={live} window={captureWindow} onError={setError} actions={session.running || live.stopping
        ? <Button disabled={!!live.stopping} onClick={() => void run('stop')}><Icon name="stop" size={17} />Stop capture</Button>
        : <Button variant="primary" disabled={!sourceId || working} busy={busy === 'start'} onClick={() => void run('start', { loadModel: false })}><Icon name="play" size={17} />Start capture</Button>} />
    </>}
    <DiscardDialog open={pending !== null} onKeep={() => setPending(null)} onDiscard={() => { const next = pending; setPending(null); setDraftEpoch(value => value + 1); if (next) void run(next.action, { id: next.id }); }} />
  </div>;
}
