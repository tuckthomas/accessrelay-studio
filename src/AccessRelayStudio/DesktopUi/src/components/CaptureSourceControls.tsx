import { Button, Field, Icon, Notice, Select, SelectOption } from '../ui';

export function CaptureSourceControls({ xbox = {}, sources, sourceId, locked, working, busy, choose, run, refresh }: {
  xbox?: any; sources: { id: string; title: string }[]; sourceId: string; locked: boolean; working: boolean; busy: string;
  choose: (action: string, id: string) => void; run: (action: string, args?: any) => Promise<void>; refresh: () => void;
}) {
  const stats = xbox.statistics;
  const value = (n: number | undefined | null, unit: string) => typeof n === 'number' ? n.toFixed(1) + unit : '—';
  return <div className="stack-sm">
    <div className="row remote-play-source">
      <Field label="Capture source"><Select aria-label="Capture source" value={xbox.direct ? 'xbox' : sourceId} disabled={working || locked}
        onFocus={refresh} onValueChange={id => choose(id === 'xbox' ? 'captureMode' : 'sourceSelect', id)}>
        <SelectOption value="">Choose a source</SelectOption>
        <SelectOption value="xbox">Direct Xbox Remote Play</SelectOption>
        {sources.map(source => <SelectOption key={source.id} value={source.id}>{source.title}</SelectOption>)}
      </Select></Field>
      <Button aria-label="Refresh capture windows" title="Refresh capture windows" disabled={locked || working} onClick={() => void run('sourceRefresh')}><Icon name="refresh" size={18} /></Button>
    </div>
    {xbox.direct && <div className="row remote-play-source">
      {xbox.signedIn ? <>
        <Field label="Xbox console"><Select aria-label="Xbox console" value={xbox.consoleId ?? ''} disabled={working || locked || xbox.connected}
          onValueChange={id => choose('xboxConsole', id)}>
          <SelectOption value="">Choose a console</SelectOption>{(xbox.consoles ?? []).map((s: any) =>
            <SelectOption key={s.id} value={s.id}>{s.name} · {s.powerState}</SelectOption>)}
        </Select></Field>
        <Button disabled={working || locked || xbox.connected} onClick={() => void run('xboxRefresh')}>Refresh consoles</Button>
        <Button active={!!xbox.connected} disabled={locked || working || !xbox.consoleId}
          onClick={() => void run(xbox.connected ? 'xboxDisconnect' : 'xboxConnect')}>{xbox.connected ? 'Disconnect Xbox' : 'Connect Xbox'}</Button>
        <Button disabled={working || locked} onClick={() => void run('xboxSignOut')}>Sign out</Button>
      </> : <Button disabled={working || locked} onClick={() => void run('xboxSignIn')}>Sign in to Xbox</Button>}
      {(xbox.signInCode || busy === 'xboxConnect') && <Button onClick={() => void run('xboxCancel')}>Cancel</Button>}
    </div>}
    {xbox.direct && xbox.connected && <div className="row">
      <Button onClick={() => void run('xboxShowPlayer')} disabled={working}>Show Xbox player</Button>
      <Button active={!!xbox.controllerConnected} disabled={working}
        onClick={() => void run(xbox.controllerConnected ? 'xboxControllerDisconnect' : 'connectOutput')}>
        {xbox.controllerConnected ? 'Disconnect controller' : 'Connect controller'}
      </Button>
      <span role="status">Xbox controller output is {xbox.controllerConnected ? 'on' : 'off'}.</span>
    </div>}
    {xbox.direct && xbox.signInCode && <Notice>Enter <strong>{xbox.signInCode}</strong> at {xbox.verificationPage} in your browser.</Notice>}
    {xbox.direct && xbox.message && <p role="status">{xbox.message}</p>}
    {xbox.direct && xbox.connected && stats && <p>{stats.width} × {stats.height} · {value(stats.decodedFps, ' FPS')} · {stats.rttSource === 'ICMP to selected LAN peer' ? 'LAN ping' : 'ICE RTT'} {value(stats.rttMs, ' ms')} · Jitter {value(stats.jitterMs, ' ms')} · Decode {value(stats.decodeMs, ' ms')} · Packet loss {value(stats.packetLossPercent, '%')}{stats.colorConversion && ` · ${stats.colorConversion} color conversion`}</p>}
  </div>;
}
