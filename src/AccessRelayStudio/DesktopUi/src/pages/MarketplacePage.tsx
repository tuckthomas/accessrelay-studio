import { useEffect, useRef, useState } from 'react';
import { getGameMeta } from '../games';
import { Badge, Button, Card, EmptyState, Field, Icon, Input, Notice, SectionHeader, Select, SelectOption } from '../ui';

type Entry = {
  id: string;
  name: string;
  description: string;
  author: string;
  version: string;
  kind: 'script' | 'plugin' | 'controller-profile' | 'training-plugin';
  gameId?: string;
  license: string;
  tags: string[];
  installed: boolean;
  coverUrl?: string | null;
  trainingPluginId?: string;
  nativeCode?: boolean;
};

const kinds: Record<Entry['kind'], string> = {
  script: 'Game scripts',
  'controller-profile': 'Controller scripts',
  plugin: 'Game add-ons',
  'training-plugin': 'Game vision add-ons',
};

const shortKind: Record<Entry['kind'], string> = {
  script: 'Script',
  'controller-profile': 'Controller preset',
  plugin: 'Add-on',
  'training-plugin': 'Vision Add-On',
};

export function MarketplacePage({
  state,
  installedPlugins = [],
  gameDescriptors = [],
  command,
  openGameAddon,
  openTraining,
}: {
  state: any;
  installedPlugins?: { id: string; name?: string }[];
  gameDescriptors?: { id: string; packageId?: string; coverUrl?: string | null }[];
  command: (action: string, args?: any) => Promise<any>;
  openGameAddon?: (addon: { gameId: string; pluginId: string; title: string; coverUrl?: string | null }) => Promise<any>;
  openTraining?: (pluginId: string) => Promise<any>;
}) {
  const [catalog, setCatalog] = useState(state);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('all');
  const [game, setGame] = useState('all');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const requested = useRef(false);

  useEffect(() => { if (state?.loaded) setCatalog(state); }, [state]);

  const openSource = (entry?: Entry) => {
    void command('sourceOpen', entry ? { id: entry.id, version: entry.version } : {}).catch(reason => setError(String(reason)));
  };

  async function run(action: string, args?: any) {
    setBusy(action === 'install' ? args.id : action);
    setError('');
    setMessage('');
    try {
      const result = await command(action, args);
      if (result?.entries) setCatalog(result);
      if (action === 'install') {
        const entry = result?.entries?.find((item: Entry) => item.id === args.id);
        setMessage(entry?.kind === 'training-plugin'
          ? 'Add-on installed. Open it from the game card below when ready.'
          : 'Package installed. Open Controller → Plugins to use controller scripts.');
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy('');
    }
  }

  async function launchGame(gameId: string, pluginId: string, title: string, coverUrl?: string | null) {
    if (!openGameAddon) return;
    setBusy(`open:${gameId}`);
    setError('');
    setMessage('');
    try {
      await openGameAddon({ gameId, pluginId, title, coverUrl });
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy('');
    }
  }

  async function launchTraining(entry: Entry) {
    if (!entry.trainingPluginId || !openTraining) return;
    setBusy(entry.id + ':training');
    setError('');
    setMessage('');
    try {
      await openTraining(entry.trainingPluginId);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy('');
    }
  }

  useEffect(() => {
    if (!requested.current && !state?.loaded) {
      requested.current = true;
      void run('refresh');
    }
  }, []);

  const allEntries: Entry[] = catalog?.entries ?? [];
  const entries = allEntries.filter(entry =>
    (kind === 'all' || entry.kind === kind)
    && (game === 'all' || entry.gameId === game)
    && [entry.name, entry.description, entry.author, entry.gameId ?? '', ...entry.tags]
      .join(' ')
      .toLowerCase()
      .includes(query.toLowerCase()));

  const gameGroups: { gameId: string; entries: Entry[] }[] = [];
  const groupIndex = new Map<string, number>();
  const generalEntries: Entry[] = [];

  for (const entry of entries) {
    if (entry.gameId) {
      const idx = groupIndex.get(entry.gameId);
      if (idx === undefined) {
        groupIndex.set(entry.gameId, gameGroups.length);
        gameGroups.push({ gameId: entry.gameId, entries: [entry] });
      } else {
        gameGroups[idx].entries.push(entry);
      }
    } else {
      generalEntries.push(entry);
    }
  }

  const availableGames = [...new Set<string>(allEntries.flatMap(entry => entry.gameId ? [entry.gameId] : []))];

  function resolvePluginId(groupEntries: Entry[], gameId: string): string | undefined {
    const installedEntryPlugin = groupEntries.find(e => e.installed && e.trainingPluginId)?.trainingPluginId;
    if (installedEntryPlugin) return installedEntryPlugin;
    if (groupEntries.some(e => e.installed && (e.kind === 'training-plugin' || e.kind === 'plugin'))
      && installedPlugins.some(p => p.id === gameId)) {
      return gameId;
    }
    return undefined;
  }

  return <div className="page-stack marketplace-page">
    <SectionHeader
      title="Add-Ons"
      description="Game-specific workspaces, vision add-ons, and controller scripts."
      actions={<>
        <Button disabled={!!busy} onClick={() => void run('refresh')}><Icon name="refresh" size={17} />Refresh</Button>
        <Button onClick={() => openSource()}><Icon name="code" size={17} />Community repository</Button>
      </>}
    />

    <div className="marketplace-filters">
      <Field label="Search">
        <Input type="search" placeholder="Search Add-Ons" value={query} onChange={event => setQuery(event.target.value)} />
      </Field>
      <Field label="Content type">
        <Select aria-label="Add-Ons content type" value={kind} onValueChange={setKind}>
          <SelectOption value="all">All types</SelectOption>
          {Object.entries(kinds).map(([id, label]) => <SelectOption key={id} value={id}>{label}</SelectOption>)}
        </Select>
      </Field>
      <Field label="Game">
        <Select aria-label="Add-Ons game" value={game} onValueChange={setGame}>
          <SelectOption value="all">All games</SelectOption>
          {availableGames.map(id => <SelectOption key={id} value={id}>{getGameMeta(id).title}</SelectOption>)}
        </Select>
      </Field>
    </div>

    {error && <Notice tone="danger">{error}</Notice>}
    {message && <Notice tone="success">{message}</Notice>}

    {busy === 'refresh' && !catalog?.loaded ? (
      <Card><p>Loading add-ons…</p></Card>
    ) : entries.length ? (
      <div className="page-stack">
        {gameGroups.length > 0 && (
          <section className="page-stack" aria-label="Games">
            <div className="addons-games-grid">
              {gameGroups.map(group => {
                const primaryEntry = group.entries.find(e => e.kind === 'training-plugin' || e.kind === 'plugin') || group.entries[0];
                const single = group.entries.length === 1;
                const meta = getGameMeta(group.gameId, undefined, single ? `${primaryEntry.author} · ${primaryEntry.version}` : primaryEntry?.description);
                const descriptor = gameDescriptors.find(d => d.id === group.gameId || d.packageId === primaryEntry?.id);
                const coverUrl = group.entries.find(e => e.coverUrl)?.coverUrl || descriptor?.coverUrl || null;
                const installedCount = group.entries.filter(e => e.installed).length;
                const interactivePluginId = resolvePluginId(group.entries, group.gameId);
                const trainingEntry = group.entries.find(e => e.installed && e.trainingPluginId);
                const canOpen = Boolean(interactivePluginId && openGameAddon);

                return (
                  <Card key={group.gameId} className="game-addon-card">
                    <div
                      className={'game-addon-card-cover' + (canOpen ? ' clickable' : '')}
                      onClick={canOpen ? () => void launchGame(group.gameId, interactivePluginId!, meta.title, coverUrl) : undefined}
                    >
                      {coverUrl
                        ? <img src={coverUrl} alt={`${meta.title} cover art`} />
                        : <div className="game-cover-fallback" style={{ ['--game-accent' as any]: meta.accent }}><span>{meta.title}</span></div>}
                      <div className="game-addon-card-overlay">
                        <div className="game-addon-card-badges">
                          <Badge tone={installedCount > 0 ? 'mint' : 'neutral'}>
                            {single
                              ? (installedCount > 0 ? 'Installed' : 'Available')
                              : (installedCount > 0 ? `${installedCount} of ${group.entries.length} installed` : `${group.entries.length} available`)}
                          </Badge>
                        </div>
                        <h2>{meta.title}</h2>
                        <p>{meta.subtitle}</p>
                      </div>
                    </div>

                    <div className="game-addon-packages">
                      {group.entries.map(entry => (
                        <div key={entry.id + entry.version} className="game-addon-package">
                          <div className="game-addon-package-head">
                            <div>
                              <span className="subtle game-addon-kind">{shortKind[entry.kind]}</span>
                              {(!single || entry.name !== meta.title) && <h3 title={entry.name}>{entry.name}</h3>}
                            </div>
                            <div className="game-addon-package-actions">
                              {entry.installed ? (
                                !single && <Badge tone="mint"><Icon name="check" size={14} />Installed</Badge>
                              ) : (
                                <Button
                                  variant="primary"
                                  disabled={!!busy}
                                  busy={busy === entry.id}
                                  onClick={() => void run('install', { id: entry.id, version: entry.version })}
                                >
                                  <Icon name="download" size={16} />Install
                                </Button>
                              )}
                              <Button variant="ghost" onClick={() => openSource(entry)}>
                                Source<Icon name="arrowUpRight" size={15} />
                              </Button>
                            </div>
                          </div>
                          <p className="subtle game-addon-package-desc">{entry.description}</p>
                          <p className="subtle game-addon-package-meta">{entry.author} · {entry.version} · {entry.license}</p>
                          {entry.nativeCode && (
                            <details className="game-addon-runtime">
                              <summary>Plugin runtime</summary>
                              <p>Runs .NET and interface code on this PC with your user permissions. Review its source before installing.</p>
                            </details>
                          )}
                        </div>
                      ))}
                    </div>

                    {(canOpen || (trainingEntry && openTraining)) && (
                      <div className="game-addon-card-footer">
                        {canOpen && (
                          <Button
                            variant="primary"
                            disabled={!!busy}
                            busy={busy === `open:${group.gameId}`}
                            onClick={() => void launchGame(group.gameId, interactivePluginId!, meta.title, coverUrl)}
                          >
                            <Icon name="play" size={17} />Open {meta.title}
                          </Button>
                        )}
                        {trainingEntry && openTraining && (
                          <Button
                            disabled={!!busy}
                            busy={busy === trainingEntry.id + ':training'}
                            onClick={() => void launchTraining(trainingEntry)}
                          >
                            <Icon name="camera" size={17} />Open training
                          </Button>
                        )}
                      </div>
                    )}
                  </Card>
                );
              })}
            </div>
          </section>
        )}

        {generalEntries.length > 0 && (
          <section className="page-stack" aria-label="Scripts and general add-ons">
            {gameGroups.length > 0 && <h2>Scripts &amp; general add-ons</h2>}
            <div className="marketplace-grid">
              {generalEntries.map(entry => (
                <Card key={entry.id + entry.version} className="marketplace-item">
                  <div className="row">
                    <Icon name={entry.kind === 'script' ? 'code' : entry.kind === 'training-plugin' ? 'camera' : 'gamepad'} size={24} />
                    <span className="subtle">{kinds[entry.kind]}</span>
                  </div>
                  <h2 title={entry.name}>{entry.name}</h2>
                  <p className="marketplace-item-description" title={entry.description}>{entry.description}</p>
                  <div className="marketplace-item-meta">
                    <p className="subtle" title={`${entry.author} · ${entry.version}`}>{entry.author} · {entry.version}</p>
                    <p className="subtle" title={entry.license}>{entry.license}</p>
                    {entry.nativeCode && (
                      <details>
                        <summary>Plugin runtime</summary>
                        <p>Runs .NET and interface code on this PC with your user permissions. Review its source before installing.</p>
                      </details>
                    )}
                  </div>
                  <div className="marketplace-item-actions">
                    {entry.installed && entry.trainingPluginId && openTraining ? (
                      <Button variant="primary" disabled={!!busy} busy={busy === entry.id + ':training'} onClick={() => void launchTraining(entry)}>
                        <Icon name="camera" size={17} />Open training
                      </Button>
                    ) : (
                      <Button
                        variant="primary"
                        disabled={!!busy || entry.installed}
                        busy={busy === entry.id}
                        onClick={() => void run('install', { id: entry.id, version: entry.version })}
                      >
                        <Icon name={entry.installed ? 'check' : 'download'} size={17} />{entry.installed ? 'Installed' : 'Install'}
                      </Button>
                    )}
                    <Button variant="ghost" onClick={() => openSource(entry)}>
                      View source<Icon name="arrowUpRight" size={16} />
                    </Button>
                  </div>
                </Card>
              ))}
            </div>
          </section>
        )}
      </div>
    ) : (
      <EmptyState
        icon="store"
        title={query || kind !== 'all' || game !== 'all' ? 'No matching add-ons' : 'No add-ons available'}
        description={query || kind !== 'all' || game !== 'all' ? 'Change the search or filters.' : 'Use the community repository to contribute a package.'}
      />
    )}
  </div>;
}
