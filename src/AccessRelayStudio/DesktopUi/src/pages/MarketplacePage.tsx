import { useEffect, useRef, useState } from 'react';
import { Button, Card, EmptyState, Field, Icon, Input, Notice, SectionHeader, Select, SelectOption } from '../ui';

type Entry = { id: string; name: string; description: string; author: string; version: string; kind: 'script' | 'plugin' | 'controller-profile' | 'training-plugin'; gameId?: string; license: string; tags: string[]; installed: boolean; trainingPluginId?: string };
const kinds = { script: 'Game scripts', 'controller-profile': 'Controller profiles', plugin: 'Game plugins', 'training-plugin': 'Model training plugins' };
export function MarketplacePage({ state, command, openTraining }: { state: any; command: (action: string, args?: any) => Promise<any>; openTraining?: (pluginId: string) => Promise<any> }) {
  const [catalog, setCatalog] = useState(state), [query, setQuery] = useState(''), [kind, setKind] = useState('all'), [game, setGame] = useState('all');
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState('');
  const requested = useRef(false);
  useEffect(() => { if (state?.loaded) setCatalog(state); }, [state]);
  const openSource = (entry?: Entry) => { void command('sourceOpen', entry ? { id: entry.id, version: entry.version } : {}).catch(reason => setError(String(reason))); };
  async function run(action: string, args?: any) {
    setBusy(action === 'install' ? args.id : action); setError(''); setMessage('');
    try {
      const result = await command(action, args);
      if (result?.entries) setCatalog(result);
      if (action === 'install') {
        const entry = result?.entries?.find((item: Entry) => item.id === args.id);
        setMessage(entry?.kind === 'training-plugin' ? 'Training plugin installed. Select it in Model Training or choose Open training.'
          : 'Package installed. Open Controller → Plugins to create a controller profile.');
      }
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(''); }
  }
  async function train(entry: Entry) {
    if (!entry.trainingPluginId || !openTraining) return;
    setBusy(entry.id + ':training'); setError(''); setMessage('');
    try { await openTraining(entry.trainingPluginId); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(''); }
  }
  useEffect(() => { if (!requested.current && !state?.loaded) { requested.current = true; void run('refresh'); } }, []);
  const entries: Entry[] = (catalog?.entries ?? []).filter((entry: Entry) => (kind === 'all' || entry.kind === kind)
    && (game === 'all' || entry.gameId === game)
    && [entry.name, entry.description, entry.author, ...entry.tags].join(' ').toLowerCase().includes(query.toLowerCase()));
  return <div className="page-stack marketplace-page">
    <SectionHeader title="Marketplace" actions={<><Button disabled={!!busy} onClick={() => void run('refresh')}><Icon name="refresh" size={17} />Refresh</Button>
      <Button onClick={() => openSource()}><Icon name="code" size={17} />Community repository</Button></>} />
    <div className="marketplace-filters"><Field label="Search"><Input type="search" placeholder="Search Marketplace" value={query} onChange={event => setQuery(event.target.value)} /></Field>
      <Field label="Content type"><Select aria-label="Marketplace content type" value={kind} onValueChange={setKind}><SelectOption value="all">All types</SelectOption>
        {Object.entries(kinds).map(([id, label]) => <SelectOption key={id} value={id}>{label}</SelectOption>)}</Select></Field>
      <Field label="Game"><Select aria-label="Marketplace game" value={game} onValueChange={setGame}><SelectOption value="all">All games</SelectOption>
        {[...new Set<string>((catalog?.entries ?? []).flatMap((entry: Entry) => entry.gameId ? [entry.gameId] : []))].map(id => <SelectOption key={id} value={id}>{id.toUpperCase().replace(/^NBA2K/, 'NBA 2K')}</SelectOption>)}
      </Select></Field></div>
    {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
    {busy === 'refresh' && !catalog?.loaded ? <Card><p>Loading marketplace…</p></Card> : entries.length ? <div className="marketplace-grid">{entries.map(entry => <Card key={entry.id + entry.version} className="marketplace-item">
      <div className="row"><Icon name={entry.kind === 'script' ? 'code' : entry.kind === 'training-plugin' ? 'camera' : 'gamepad'} size={24} /><span className="subtle">{kinds[entry.kind]}</span></div>
      <h2>{entry.name}</h2><p>{entry.description}</p><p className="subtle">{entry.author} · {entry.version}</p><p className="subtle">{entry.license}</p>
      <div className="marketplace-item-actions">
        {entry.installed && entry.trainingPluginId && openTraining && <Button variant="primary" disabled={!!busy} busy={busy === entry.id + ':training'} onClick={() => void train(entry)}><Icon name="camera" size={17} />Open training</Button>}
        <Button variant={entry.trainingPluginId ? 'secondary' : 'primary'} disabled={!!busy || entry.installed} busy={busy === entry.id} onClick={() => void run('install', { id: entry.id, version: entry.version })}>
          <Icon name={entry.installed ? 'check' : 'download'} size={17} />{entry.installed ? 'Installed' : 'Install'}</Button>
        <Button variant="ghost" onClick={() => openSource(entry)}>View source<Icon name="arrowUpRight" size={16} /></Button></div>
    </Card>)}</div> : <EmptyState icon="store" title={query || kind !== 'all' || game !== 'all' ? 'No matching packages' : 'No packages available'}
      description={query || kind !== 'all' || game !== 'all' ? 'Change the search or filters.' : 'Use the community repository to contribute a package.'} />}
  </div>;
}
