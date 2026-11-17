import { CaptureSourceControls } from '../components/CaptureSourceControls';
import { useState } from 'react';
import { getGameMeta } from '../games';
import { useGamePluginError, useGameVisionWorkspace } from '../game-plugins/registry';
import type { GameControllerHost } from '../game-plugins/api';
import type { GameplayWindowControls } from '../components/GameplayPreview';
import { Badge, Button, Card, DiscardDialog, Field, Icon, Notice, Select, SelectOption, hasUnsavedChanges } from '../ui';

export interface GameAddonDescriptor {
  gameId: string;
  pluginId: string;
  title?: string;
  coverUrl?: string | null;
}

export function GameAddonPage({
  state = {},
  status,
  command,
  controller,
  addon,
  gameDescriptors = [],
  captureWindow,
  onBack,
  openTraining,
}: {
  state?: any;
  status?: any;
  command: (action: string, args?: any) => Promise<any>;
  controller?: GameControllerHost;
  addon: GameAddonDescriptor;
  gameDescriptors?: { id: string; coverUrl?: string | null }[];
  captureWindow?: GameplayWindowControls;
  onBack: () => void;
  openTraining?: (pluginId: string) => Promise<any>;
}) {
  const [busy, setBusy] = useState(''), [error, setError] = useState('');
  const [pending, setPending] = useState<{ action: string; id: string } | null>(null), [draftEpoch, setDraftEpoch] = useState(0);
  const live = status ?? state.status ?? {}, session = live.session ?? {};
  const sources = live.sources ?? state.sources ?? [], sourceId = live.selectedSourceId ?? state.selectedSourceId ?? '';
  const plugins = state.plugins ?? [];
  const pluginId = addon.pluginId, installed = plugins.some((plugin: any) => plugin.id === pluginId);
  const Workspace = useGameVisionWorkspace(pluginId), Plugin = installed ? Workspace : undefined;
  const pluginError = useGamePluginError(pluginId);
  const meta = getGameMeta(addon.gameId || pluginId, addon.title);
  const title = meta.title;
  const coverUrl = addon.coverUrl || gameDescriptors.find(d => d.id === pluginId)?.coverUrl || null;
  const saving = (!!live.training?.hasTake && !live.training?.isFinalized)
    || (!!live.diagnosticRecording && !live.diagnosticRecording.isFinalized);
  const locked = !!session.running || !!live.stopping || saving, working = !!busy || !!live.busy || live.trainingJob?.state === 'running';

  async function run(action: string, args?: any) {
    setError(''); setBusy(action);
    try { await command(action, args); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(''); }
  }
  const choose = (action: string, id: string) => { if (hasUnsavedChanges()) setPending({ action, id }); else void run(action, { id }); };

  return <div className="page-stack game-addon-page">
    <div className="game-addon-nav">
      <Button variant="ghost" onClick={onBack}><Icon name="arrowLeft" size={17} />Back to Add-Ons</Button>
      <div className="button-group">
        {installed && openTraining && <Button disabled={locked || working} onClick={() => void openTraining(pluginId)}>
          <Icon name="book" size={17} />Model Training</Button>}
      </div>
    </div>

    <section className="game-addon-hero">
      <div className="game-addon-cover">
        {coverUrl
          ? <img src={coverUrl} alt={`${title} cover art`} />
          : <div className="game-cover-fallback" style={{ ['--game-accent' as any]: meta.accent }}><span>{title}</span></div>}
        <div className="game-addon-cover-scrim" />
      </div>
      <div className="game-addon-hero-body">
        <div className="row" style={{ gap: '10px', alignItems: 'center' }}>
          <Badge tone={installed ? 'mint' : 'neutral'}>{installed ? 'Add-on active' : 'Install required'}</Badge>
          {session.running && <Badge tone="success">Capture live</Badge>}
        </div>
        <h1>{title}</h1>
        <p className="subtle">{meta.subtitle}</p>
      </div>
    </section>

    {error && <Notice tone="danger">{error}</Notice>}
    {(state.pluginErrors ?? []).map((message: string) => <Notice key={message} tone="danger">{message}</Notice>)}

    <Card title="Capture settings"><CaptureSourceControls xbox={live.xbox ?? state.xbox} sources={sources} sourceId={sourceId}
      locked={locked} working={working} busy={busy} choose={choose} run={run}
      refresh={() => { if (!locked && !working) command('sourceRefresh').catch(e => setError(e instanceof Error ? e.message : String(e))); }} /></Card>

    {Plugin
      ? <Plugin key={pluginId + draftEpoch} state={state} status={status} command={command} controller={controller} mode="live" captureWindow={captureWindow} />
      : installed
        ? <Card>{pluginError ? <Notice tone="danger">{pluginError}</Notice> : <p>Loading {title} add-on…</p>}</Card>
        : <Card title={`${title} is not installed`}>
          <p className="subtle">Install this game add-on from Add-Ons before opening its workspace.</p>
          <Button variant="primary" onClick={onBack}><Icon name="store" size={17} />Go to Add-Ons</Button>
        </Card>}

    <DiscardDialog open={pending !== null} onKeep={() => setPending(null)} onDiscard={() => {
      const next = pending; setPending(null); setDraftEpoch(value => value + 1);
      if (next) void run(next.action, { id: next.id });
    }} />
  </div>;
}
