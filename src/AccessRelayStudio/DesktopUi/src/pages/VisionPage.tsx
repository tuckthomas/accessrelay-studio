import { useState } from 'react';
import { gameVisionWorkspace } from '../game-plugins/registry';
import { Select, SelectOption, Button, Card, DiscardDialog, Field, Icon, Notice, SectionHeader, hasUnsavedChanges, type PageProps } from '../ui';

export function VisionPage({ state = {}, status, command }: PageProps) {
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
    <SectionHeader title="Game Vision" description="Capture gameplay for training and live AI assistance." actions={<Field label="Game plugin"><Select aria-label="Game plugin" disabled={locked || working}
      value={pluginId} onValueChange={selectionValue => choosePlugin(selectionValue)}>
      <SelectOption value="">None</SelectOption>{plugins.map((plugin: any) => <SelectOption key={plugin.id} value={plugin.id}>{plugin.name}</SelectOption>)}
    </Select></Field>} />
    {error && <Notice tone="danger">{error}</Notice>}
    {Plugin ? <Plugin key={pluginId} state={state} status={status} command={command} /> : <>
      <Card><div className="row remote-play-source"><Field label="Capture window" hint="A browser window captures its active tab.">
        <Select aria-label="Capture window" value={sourceId} disabled={locked || working}
          onFocus={() => { if (!locked && !working) command('sourceRefresh').catch(e => setError(e instanceof Error ? e.message : String(e))); }}
          onValueChange={selectionValue => void run('sourceSelect', { id: selectionValue })}>
          <SelectOption value="">Choose a window</SelectOption>{sources.map((source: any) => <SelectOption key={source.id} value={source.id}>{source.title}</SelectOption>)}
        </Select></Field><Button aria-label="Refresh capture windows" disabled={locked || working} onClick={() => void run('sourceRefresh')}><Icon name="refresh" size={18} /></Button>
      </div></Card>
      <Card className="remote-play-preview" title="Gameplay preview" actions={session.running || live.stopping
        ? <Button disabled={!!live.stopping} onClick={() => void run('stop')}><Icon name="stop" size={17} />Stop preview</Button>
        : <Button variant="primary" disabled={!sourceId || working} busy={busy === 'start'} onClick={() => void run('start', { loadModel: false })}><Icon name="play" size={17} />Start preview</Button>}>
        <div className="remote-play-stage">{live.preview?.url
          ? <img src={live.preview.url} alt={session.running ? 'Live gameplay capture' : 'Last captured gameplay preview'} />
          : <p className="subtle">Select a window and start preview.</p>}</div>
        <div className="row-spread remote-play-preview-details"><span>{live.preview?.sourceWidth ? `${live.preview.sourceWidth} × ${live.preview.sourceHeight}` : ''}</span>
          <span>{session.running ? 'Viewing only · frames are not saved' : ''}</span></div>
        {session.captureFailed && <Notice tone="danger">{session.message || 'The selected window could not be captured.'}</Notice>}
      </Card>
    </>}
    <DiscardDialog open={pending !== null} onKeep={() => setPending(null)} onDiscard={() => { const id = pending; setPending(null); if (id !== null) void run('pluginSelect', { id }); }} />
  </div>;
}
