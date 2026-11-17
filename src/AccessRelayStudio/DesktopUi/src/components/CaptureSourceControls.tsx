import { Button, Field, Icon, Notice, Select, SelectOption } from '../ui';

export function CaptureSourceControls({ xbox = {}, sources, sourceId, locked, working, busy, choose, run, refresh }: {
  xbox?: any; sources: { id: string; title: string }[]; sourceId: string; locked: boolean; working: boolean; busy: string;
  choose: (action: string, id: string) => void; run: (action: string, args?: any) => Promise<void>; refresh: () => void;
}) {
  const stats = xbox.statistics;
  const value = (n: number | undefined | null, unit: string) => typeof n === 'number' ? n.toFixed(1) + unit : '—';
  return <>
    <div className="row remote-play-source">
      <Field label="Capture source"><Select aria-label="Capture source" value={xbox.direct ? 'xbox' : 'window'} disabled={working || locked}
        onValueChange={id => choose('captureMode', id)}>
        <SelectOption value="window">Application window</SelectOption><SelectOption value="xbox">Direct Xbox Remote Play</SelectOption>
      </Select></Field>
      {!xbox.direct && <><Field label="Gameplay window" hint="A browser window captures its active tab.">
        <Select aria-label="Gameplay window" value={sourceId} disabled={locked || working} onFocus={refresh}
          onValueChange={id => choose('sourceSelect', id)}>
          <SelectOption value="">Choose a window</SelectOption>{sources.map(s => <SelectOption key={s.id} value={s.id}>{s.title}</SelectOption>)}
        </Select></Field>
        <Button aria-label="Refresh capture windows" disabled={locked || working} onClick={() => void run('sourceRefresh')}><Icon name="refresh" size={18} /></Button></>}
      {xbox.direct && <>
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
      </>}
    </div>
    {xbox.direct && xbox.signInCode && <Notice>Enter <strong>{xbox.signInCode}</strong> at {xbox.verificationPage} in your browser.</Notice>}
    {xbox.direct && xbox.message && <p role="status">{xbox.message}</p>}
    {xbox.direct && xbox.connected && stats && <p>{stats.width} × {stats.height} · {value(stats.decodedFps, ' FPS')} · {stats.rttSource === 'ICMP to selected LAN peer' ? 'LAN ping' : 'ICE RTT'} {value(stats.rttMs, ' ms')} · Jitter {value(stats.jitterMs, ' ms')} · Decode {value(stats.decodeMs, ' ms')} · Packet loss {value(stats.packetLossPercent, '%')}</p>}
  </>;
}
