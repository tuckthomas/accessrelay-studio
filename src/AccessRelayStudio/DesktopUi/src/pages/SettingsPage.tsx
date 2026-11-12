import { useState } from 'react';
import { Badge, Button, Card, Icon, Notice, SectionHeader, type PageProps } from '../ui';

export function SettingsPage({ state, command }: PageProps) {
  const [result, setResult] = useState(''); const [busy, setBusy] = useState('');
  const run = async (action: string) => { setBusy(action); setResult(''); try { const value = await command(action); setResult(typeof value === 'string' ? value : value?.message || 'Done.'); } catch {} finally { setBusy(''); } };
  const driver = state.driverAvailable ?? state.controller?.driverAvailable;
  return <div className="page-stack">
    <SectionHeader eyebrow="Keep things in order" title="Studio settings" description="Manage the local pieces that keep your workspace running." />
    <div className="split-grid">
      <Card title="Virtual devices" description="Connect your controller and microphone to other Windows applications." actions={<Badge tone={driver ? 'success' : 'warning'}>{driver ? 'Driver ready' : 'Setup needed'}</Badge>}>
        <div className="stack"><p className="subtle">{driver ? 'The virtual device driver is installed. Controller and voice output can start from their workspaces.' : 'Install the bundled driver once to enable virtual controller and microphone output. Windows will show its administrator approval prompt.'}</p>
          <div className="row">{!driver && <Button variant="primary" busy={busy === 'installDriver'} onClick={() => void run('installDriver')}><Icon name="download" size={17} />Install device driver</Button>}
            <Button busy={busy === 'probeOutput'} onClick={() => void run('probeOutput')}><Icon name="refresh" size={17} />Check controller output</Button></div>
          {result && <Notice>{result}</Notice>}
        </div>
      </Card>
      <Card title="Your saved work" description="Open the folders containing your local setups and voices.">
        <div className="stack"><Button onClick={() => void run('openProfilesFolder')}><Icon name="folder" size={18} />Controller setup folder<Icon name="arrowUpRight" size={16} /></Button>
          <Button onClick={() => void run('openVoiceProfilesFolder')}><Icon name="folder" size={18} />Voice profile folder<Icon name="arrowUpRight" size={16} /></Button>
          <p className="subtle">Training clips are saved in the folder you choose when recording.</p></div>
      </Card>
    </div>
    <Card title="About this Studio" description="A local workspace for controller input, gameplay capture and voice.">
      <div className="detail-list"><div className="detail-line"><span>Installed build</span><strong>{state.version || 'Local build'}</strong></div>
        <div className="detail-line"><span>Platform</span><span>{state.platform || 'Windows x64'}</span></div>
        <div className="detail-line"><span>Interface</span><span>Local desktop interface</span></div></div>
      <details className="details-section"><summary>Storage and application details</summary><div className="detail-list">
        <div className="detail-line"><span>Controller setups</span><code>{state.profileDirectory || state.controller?.profileDirectory || 'Not available'}</code></div>
        <div className="detail-line"><span>Application folder</span><code>{state.appDirectory || 'Not available'}</code></div>
      </div></details>
    </Card>
    <Notice title="Experimental vision">Remote Play capture and evidence collection are available. Automatic assistance still requires a compatible model and validated timing evidence.</Notice>
  </div>;
}
