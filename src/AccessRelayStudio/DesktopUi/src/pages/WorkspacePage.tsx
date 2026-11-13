import { useRef, useState } from 'react';
import { Button, Card, Icon, Meter, Notice, SectionHeader } from '../ui';
import type { SystemStatus } from '../api';

type Feature = 'controller' | 'vision' | 'voice';
type Pending = 'start' | 'stop' | null;
export function WorkspacePage({ state, status, command, navigate }: {
  state: any; status: any; command: (method: string, args?: any) => Promise<any>; navigate: (page: any) => void;
}) {
  const controller = status.controller || {}, voice = status.voice || {}, vision = status.vision || {};
  const profile = state.controller?.activeProfile;
  const [pending, setPending] = useState<Record<Feature, Pending>>({ controller: null, vision: null, voice: null });
  const operations = useRef({ controller: 0, vision: 0, voice: 0 });
  const [monitorBusy, setMonitorBusy] = useState(false);
  const monitorEnabled = state.settings?.resourceMonitoringEnabled !== false;
  const recording = vision.training?.isRecording || vision.recording?.isRecording;
  const visionRunning = vision.session?.running || vision.running || recording;
  const commandListening = voice.commands?.recognition?.running || voice.commands?.output?.enabled;
  const active = {
    controller: !!(controller.outputActive || controller.isStarting || pending.controller === 'start'),
    vision: !!(visionRunning || pending.vision === 'start'),
    voice: !!(voice.isRunning || voice.isStarting || commandListening || pending.voice === 'start'),
  };
  const sourceId = vision.selectedSourceId ?? state.vision?.selectedSourceId;
  const savingClip = vision.training?.hasTake && !vision.training?.isRecording && !vision.training?.isFinalized;
  const voiceModel = state.voice?.models?.find((model: any) => model.id === state.voice?.selectedModelId);
  const microphoneReady = state.voice?.devices?.microphones?.some((device: any) => device.id === state.voice?.microphoneId);
  const unavailable = {
    controller: controller.training ? 'Finish the training clip before connecting output.'
      : !controller.connected ? 'Connect a gamepad in Controller.' : !state.controller?.driverAvailable ? 'Install the output driver in Settings.' : '',
    vision: vision.stopping || savingClip ? 'Waiting for capture and clip saving to finish.' : vision.busy ? 'Live Gameplay setup is busy.'
      : !sourceId ? 'Choose a gameplay window in Live Gameplay.' : '',
    voice: voice.busy && !voice.isStarting ? 'Finish the current Voice operation.' : !microphoneReady ? 'Choose a microphone in Voice.'
      : voiceModel?.isAvailable === false ? 'Choose an installed voice model in Voice.'
      : voiceModel?.usesReference && !state.voice?.selectedProfileId ? 'Choose a reference voice in Voice.'
      : state.voice?.destination === 'virtual' && state.voice?.virtualMicIssue ? state.voice.virtualMicIssue
      : state.voice?.destination === 'headphones' && !state.voice?.devices?.outputs?.some((device: any) => device.id === state.voice?.outputId) ? 'Choose headphones in Voice.' : '',
  };

  async function toggle(feature: Feature) {
    const start = !active[feature], operation = ++operations.current[feature];
    setPending(value => ({ ...value, [feature]: start ? 'start' : 'stop' }));
    try {
      const method = feature === 'controller' ? 'controller.' + (start ? 'outputStart' : 'outputStop')
        : feature + '.' + (start ? 'start' : 'stop');
      await command(method, feature === 'vision' && start ? { loadModel: !!state.vision?.selectedModelId } : undefined);
    } catch { /* The shared command handler presents the native error. */ }
    finally {
      if (operations.current[feature] === operation) setPending(value => ({ ...value, [feature]: null }));
    }
  }
  async function toggleMonitor() {
    setMonitorBusy(true);
    try { await command('app.resourceMonitoringSet', { enabled: !monitorEnabled }); }
    catch { /* Keep the saved state if changing the native preference failed. */ }
    finally { setMonitorBusy(false); }
  }

  const rows: { id: Feature; label: string; icon: string; detail: string }[] = [
    { id: 'controller', label: 'Controller output', icon: 'gamepad', detail: controller.outputActive
      ? controller.deviceName || 'Virtual controller connected' : active.controller ? 'Connecting output…' : unavailable.controller || 'Output is off' },
    { id: 'vision', label: 'Live Gameplay', icon: 'camera', detail: recording ? 'Recording a training clip'
      : visionRunning ? 'Capture is running' : active.vision ? 'Starting capture…' : unavailable.vision || 'Capture is off' },
    { id: 'voice', label: 'Voice', icon: 'mic', detail: voice.isRunning ? (voice.destination === 'headphones' ? 'Headphone preview' : 'Virtual microphone')
      : commandListening ? 'Voice commands are listening' : active.voice ? 'Starting voice…' : unavailable.voice || 'Voice processing is off' },
  ];
  return <div className="page-stack">
    <SectionHeader eyebrow="A space for your setup" title="Your workspace" description="Your controller, your voice, your way to play." />
    <div className="workspace-grid">
      <section className="workspace-hero">
        <div className="hero-kicker"><Icon name="gamepad" size={20} />Controller workspace</div>
        <div><h2 className="hero-title">{controller.connected ? 'Your controller. More possibilities.' : 'Start with your controller.'}</h2>
          <p className="hero-text">{controller.connected ? 'Make a few thoughtful adjustments, or build a setup that does a little more for you.' : 'Connect a gamepad, choose a setup, and make the controls work the way you want.'}</p></div>
        <div className="hero-actions"><Button variant="primary" onClick={() => navigate('controller')}><Icon name="gamepad" size={18} />{controller.connected ? 'Open controller' : 'Set up controller'}<Icon name="arrowRight" size={16} /></Button></div>
      </section>
      <Card title="Controls"><div className="session-list">{rows.map(row => <div className="session-item" key={row.id}>
        <span className="session-item-icon"><Icon name={row.icon} /></span><div className="session-item-copy"><strong>{row.label}</strong><p id={'dashboard-' + row.id + '-detail'}>{row.detail}</p></div>
        <Button className="session-power" variant={active[row.id] ? 'primary' : 'secondary'} aria-label={row.id === 'vision' ? 'Live Gameplay capture' : row.id === 'voice' ? 'Voice processing' : row.label}
          aria-pressed={active[row.id]} aria-describedby={'dashboard-' + row.id + '-detail'}
          disabled={pending[row.id] === 'stop' || (!active[row.id] && !!unavailable[row.id])} onClick={() => void toggle(row.id)}>
          <Icon name={pending[row.id] ? 'loader' : 'power'} size={16} className={pending[row.id] ? 'spin' : ''} />
          {pending[row.id] === 'stop' ? 'Stopping…' : pending[row.id] === 'start' || (row.id === 'voice' && voice.isStarting) || (row.id === 'controller' && controller.isStarting) ? 'Cancel' : active[row.id] ? 'On' : 'Off'}
        </Button>
      </div>)}</div></Card>
    </div>
    <h2>Pick up where you need to</h2>
    <div className="workspace-tools">
      <Button variant="unstyled" type="button" className="tool-card" onClick={() => navigate('controller')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="sliders" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Make the controls yours</h3><p>Map a button, refine a sequence, or record a movement you want to keep.</p></div>
        <span className="tool-card-link">{profile?.name || 'Open controller profiles'}</span>
      </Button>
      <Button variant="unstyled" type="button" className="tool-card" onClick={() => navigate('vision')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="camera" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Bring gameplay into view</h3><p>Capture your game and configure live assistance.</p></div>
        <span className="tool-card-link">Open Live Gameplay</span>
      </Button>
      <Button variant="unstyled" type="button" className="tool-card" onClick={() => navigate('voice')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="audio" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Find your sound</h3><p>Shape your microphone, try a saved voice, or create a voice from a clip.</p></div>
        <span className="tool-card-link">Open your voice workspace</span>
      </Button>
    </div>
    <ResourceUse metrics={status.system || state.system || {}} enabled={monitorEnabled} busy={monitorBusy} onToggle={() => void toggleMonitor()} />
  </div>;
}

const valid = (value: any): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const percent = (value: any) => valid(value) ? value.toFixed(1) + '%' : '—';
const bytes = (value: any) => valid(value) ? value >= 1024 ** 3 ? (value / 1024 ** 3).toFixed(2) + ' GiB' : (value / 1024 ** 2).toFixed(0) + ' MiB' : '—';
const ratio = (used: any, total: any) => valid(used) && valid(total) && total > 0 ? Math.min(100, used / total * 100) : null;

function UsageChart({ values, label }: { values: (number | null | undefined)[]; label: string }) {
  const readings = values.filter(valid);
  const segments: string[] = []; let segment = '';
  values.forEach((value, index) => {
    if (!valid(value)) { if (segment) segments.push(segment); segment = ''; return; }
    segment += (segment ? ' L' : 'M') + (index / 59 * 300).toFixed(1) + ',' + (46 - Math.min(100, value) / 100 * 44).toFixed(1);
  });
  if (segment) segments.push(segment);
  return <><svg className="resource-chart" viewBox="0 0 300 48" preserveAspectRatio="none" aria-hidden="true">
    <path d="M0,46 H300 M0,24 H300 M0,2 H300" className="resource-chart-grid" />
    {segments.map((path, index) => <path d={path} key={index} className="resource-chart-line" />)}</svg>
    <span className="subtle" aria-label={label + ' recent average and peak'}>Average {readings.length ? percent(readings.reduce((sum, value) => sum + value, 0) / readings.length) : '—'} · Peak {readings.length ? percent(Math.max(...readings)) : '—'}</span></>;
}

function ResourceUse({ metrics, enabled, busy, onToggle }: { metrics: SystemStatus; enabled: boolean; busy: boolean; onToggle: () => void }) {
  const history = metrics.history || [];
  const ramPercent = ratio(metrics.memoryUsedBytes, metrics.memoryTotalBytes), vramPercent = ratio(metrics.vramUsedBytes, metrics.vramTotalBytes);
  return <Card className="workspace-resources" title="Resource use" actions={<Button variant={enabled ? 'primary' : 'secondary'}
    aria-label="Resource monitoring" aria-pressed={enabled} busy={busy} onClick={onToggle}><Icon name="power" size={16} />{enabled ? 'On' : 'Off'}</Button>}>
    {!enabled ? <p className="subtle">Monitoring is off. Resource polling and GPU counters are stopped.</p> : <>
      <div className="resource-status"><span>Whole PC</span><span className="subtle">{metrics.sampledAtUtc
        ? 'Updated ' + new Date(metrics.sampledAtUtc).toLocaleTimeString() + ' · 1-second sampling · ' + history.length + '/60 recent samples'
        : metrics.error ? 'Counters unavailable' : 'Waiting for the first sample…'}</span></div>
      {metrics.error && <Notice tone="warning">{metrics.error}</Notice>}
      <div className="resource-grid">
        <div className="resource-tile"><span className="stat-label">CPU</span><strong>{percent(metrics.cpuPercent)}</strong>
          <span className="subtle">{metrics.logicalProcessors ? metrics.logicalProcessors + ' logical processors' : 'Total processor usage'}</span>
          <UsageChart label="CPU" values={history.map(sample => sample.cpuPercent)} /></div>
        <div className="resource-tile"><span className="stat-label">System RAM</span><strong>{bytes(metrics.memoryUsedBytes)}</strong>
          <span className="subtle">of {bytes(metrics.memoryTotalBytes)} · {percent(ramPercent)}</span>
          {ramPercent !== null && <Meter value={ramPercent} label="System RAM used" />}
          <span className="subtle">Available {valid(metrics.memoryTotalBytes) && valid(metrics.memoryUsedBytes) ? bytes(Math.max(0, metrics.memoryTotalBytes - metrics.memoryUsedBytes)) : '—'}</span></div>
        <div className="resource-tile"><span className="stat-label">GPU</span><strong>{percent(metrics.gpuPercent)}</strong>
          <span className="subtle">{metrics.gpuName || 'GPU information unavailable'}</span>
          <UsageChart label="GPU" values={history.map(sample => sample.gpuPercent)} /></div>
        <div className="resource-tile"><span className="stat-label">GPU Memory</span><strong>{bytes(metrics.vramUsedBytes)}</strong>
          <span className="subtle">of {bytes(metrics.vramTotalBytes)} · {percent(vramPercent)}</span>
          {vramPercent !== null && <Meter value={vramPercent} label="Video memory used" />}
          <span className="subtle">{valid(metrics.vramTotalBytes) ? 'Selected GPU' : 'Dedicated capacity not reported'}</span></div>
      </div>
      <div className="resource-host"><div className="resource-status"><strong>Studio host process</strong></div>
        <dl className="resource-host-values"><div><dt>CPU · share of PC</dt><dd>{percent(metrics.appCpuPercent)}</dd></div>
          <div><dt>RAM in use</dt><dd>{bytes(metrics.appMemoryBytes)}</dd></div><div><dt>App memory allocated</dt><dd>{bytes(metrics.appPrivateBytes)}</dd></div>
          <div><dt>Threads</dt><dd>{metrics.appThreadCount ?? '—'}</dd></div></dl></div>
    </>}
  </Card>;
}
