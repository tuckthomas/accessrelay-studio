import { useEffect, useRef, useState } from 'react';
import { Button, Card, EmptyState, Field, Icon, Input, Notice, SectionHeader, Select, SelectOption } from '../ui';

type Entry = { id: string; name: string; description: string; author: string; version: string; kind: 'script' | 'plugin'; license: string; tags: string[]; installed: boolean };
export function MarketplacePage({ state, command }: { state: any; command: (action: string, args?: any) => Promise<any> }) {
  const [catalog, setCatalog] = useState(state), [query, setQuery] = useState(''), [kind, setKind] = useState('all');
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState('');
  const requested = useRef(false);
  useEffect(() => { if (state?.loaded) setCatalog(state); }, [state]);
  const openSource = (entry?: Entry) => { void command('sourceOpen', entry ? { id: entry.id, version: entry.version } : {}).catch(reason => setError(String(reason))); };
  async function run(action: string, args?: any) {
    setBusy(action === 'install' ? args.id : action); setError(''); setMessage('');
    try {
      const result = await command(action, args);
      if (result?.entries) setCatalog(result);
      if (action === 'install') setMessage('Installed. Open Controller → Plugins to create a profile or configure it.');
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(''); }
  }
  useEffect(() => { if (!requested.current && !state?.loaded) { requested.current = true; void run('refresh'); } }, []);
  const entries: Entry[] = (catalog?.entries ?? []).filter((entry: Entry) => (kind === 'all' || entry.kind === kind)
    && [entry.name, entry.description, entry.author, ...entry.tags].join(' ').toLowerCase().includes(query.toLowerCase()));
  return <div className="page-stack marketplace-page">
    <SectionHeader title="Marketplace" actions={<><Button disabled={!!busy} onClick={() => void run('refresh')}><Icon name="refresh" size={17} />Refresh</Button>
      <Button onClick={() => openSource()}><Icon name="code" size={17} />Community repository</Button></>} />
    <div className="marketplace-filters"><Field label="Search"><Input type="search" placeholder="Search scripts and plugins" value={query} onChange={event => setQuery(event.target.value)} /></Field>
      <Field label="Content"><Select aria-label="Marketplace content type" value={kind} onValueChange={setKind}><SelectOption value="all">Scripts and plugins</SelectOption>
        <SelectOption value="script">Scripts</SelectOption><SelectOption value="plugin">Game plugins</SelectOption></Select></Field></div>
    {error && <Notice tone="danger">{error}</Notice>}{message && <Notice tone="success">{message}</Notice>}
    {busy === 'refresh' && !catalog?.loaded ? <Card><p>Loading marketplace…</p></Card> : entries.length ? <div className="marketplace-grid">{entries.map(entry => <Card key={entry.id + entry.version} className="marketplace-item">
      <div className="row"><Icon name={entry.kind === 'script' ? 'code' : 'gamepad'} size={24} /><span className="subtle">{entry.kind === 'script' ? 'Script' : 'Game plugin'}</span></div>
      <h2>{entry.name}</h2><p>{entry.description}</p><p className="subtle">{entry.author} · {entry.version}</p><p className="subtle">{entry.license}</p>
      <div className="marketplace-item-actions"><Button variant="primary" disabled={!!busy || entry.installed} busy={busy === entry.id} onClick={() => void run('install', { id: entry.id, version: entry.version })}>
        <Icon name={entry.installed ? 'check' : 'download'} size={17} />{entry.installed ? 'Installed' : 'Install'}</Button><Button variant="ghost" onClick={() => openSource(entry)}>View source<Icon name="arrowUpRight" size={16} /></Button></div>
    </Card>)}</div> : <EmptyState icon="store" title={query || kind !== 'all' ? 'No matching packages' : 'No packages available'}
      description={query || kind !== 'all' ? 'Change the search or content filter.' : 'Use the community repository to contribute a script or game plugin.'} />}
  </div>;
}
