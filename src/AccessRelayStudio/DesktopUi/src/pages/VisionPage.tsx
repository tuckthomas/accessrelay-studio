import { useState } from 'react';
import { gameVisionWorkspace } from '../game-plugins/registry';
import { GameplayPreview, type GameplayWindowControls } from '../components/GameplayPreview';
import { Select, SelectOption, Button, Card, DiscardDialog, Field, Icon, Notice, SectionHeader, hasUnsavedChanges, type PageProps } from '../ui';

export type VisionMode = 'live' | 'training';
export type VisionWorkspaceProps = PageProps & { mode: VisionMode; captureWindow?: GameplayWindowControls };

export function VisionPage({ state = {}, status, command, mode = 'live', captureWindow }: PageProps & { mode?: VisionMode; captureWindow?: GameplayWindowControls }) {
  const [busy, setBusy] = useState(''), [error, setError] = useState('');
  const [pending, setPending] = useState<string | null>(null);
  const live = status ?? state.status ?? {}, session = live.session ?? {};
  const sources = live.sources ?? state.sources ?? [], sourceId = live.selectedSourceId ?? state.selectedSourceId ?? '';
  const pluginId = state.selectedPluginId ?? '', Plugin = gameVisionWorkspace(pluginId);
  const plugins = (state.plugins ?? []).filter((plugin: any) => gameVisionWorkspace(plugin.id));
  const saving = !!live.training?.hasTake && !live.training?.isFinalized;
  const locked = !!session.running || !!live.stopping || saving, working = !!busy || !!live.busy;
  async function run(action: string, args?: any) {
    setError(''); setBusy(action);
    try { await command(action, args); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(''); }
  }
  const choosePlugin = (id: string) => { if (hasUnsavedChanges()) setPending(id); else void run('pluginSelect', { id }); };
  return <div className="page-stack remote-play-page">
    <SectionHeader title={mode === 'training' ? 'AI Model Training' : 'Live Gameplay'} actions={<Field label="Game plugin"><Select aria-label="Game plugin" disabled={locked || working}
      value={pluginId} onValueChange={selectionValue => choosePlugin(selectionValue)}>
      <SelectOption value="">None</SelectOption>{plugins.map((plugin: any) => <SelectOption key={plugin.id} value={plugin.id}>{plugin.name}</SelectOption>)}
    </Select></Field>} />
    {error && <Notice tone="danger">{error}</Notice>}
    {Plugin ? <Plugin key={pluginId + mode} state={state} status={status} command={command} mode={mode} captureWindow={captureWindow} /> : mode === 'training'
      ? <Card title="Training data"><p className="subtle">Choose a game plugin to record and review its training data.</p></Card> : <>
      <Card><div className="row remote-play-source"><Field label="Capture window" hint="A browser window captures its active tab.">
        <Select aria-label="Capture window" value={sourceId} disabled={locked || working}
          onFocus={() => { if (!locked && !working) command('sourceRefresh').catch(e => setError(e instanceof Error ? e.message : String(e))); }}
          onValueChange={selectionValue => void run('sourceSelect', { id: selectionValue })}>
          <SelectOption value="">Choose a window</SelectOption>{sources.map((source: any) => <SelectOption key={source.id} value={source.id}>{source.title}</SelectOption>)}
        </Select></Field><Button aria-label="Refresh capture windows" disabled={locked || working} onClick={() => void run('sourceRefresh')}><Icon name="refresh" size={18} /></Button>
      </div></Card>
      <GameplayPreview status={live} window={captureWindow} onError={setError} actions={session.running || live.stopping
        ? <Button disabled={!!live.stopping} onClick={() => void run('stop')}><Icon name="stop" size={17} />Stop capture</Button>
        : <Button variant="primary" disabled={!sourceId || working} busy={busy === 'start'} onClick={() => void run('start', { loadModel: false })}><Icon name="play" size={17} />Start capture</Button>} />
    </>}
    <DiscardDialog open={pending !== null} onKeep={() => setPending(null)} onDiscard={() => { const id = pending; setPending(null); if (id !== null) void run('pluginSelect', { id }); }} />
  </div>;
}
