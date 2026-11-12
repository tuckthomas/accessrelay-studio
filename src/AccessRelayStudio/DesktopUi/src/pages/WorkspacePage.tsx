import { Badge, Button, Card, Icon, SectionHeader } from '../ui';

export function WorkspacePage({ state, status, command, navigate }: { state: any; status: any; command: (action: string, args?: any) => Promise<any>; navigate: (page: any) => void }) {
  const controller = status.controller || {}, voice = status.voice || {}, vision = status.vision || {};
  const profile = state.controller?.activeProfile;
  const visionRunning = vision.session?.running || vision.running;
  const recording = vision.training?.isRecording || vision.recording?.isRecording;
  return <div className="page-stack">
    <SectionHeader eyebrow="A space for your setup" title="Your workspace" description="Your controller, your voice, your way to play." />
    <div className="workspace-grid">
      <section className="workspace-hero">
        <div className="hero-kicker"><Icon name="gamepad" size={20} />Controller workspace</div>
        <div><h2 className="hero-title">{controller.connected ? 'Your controller. More possibilities.' : 'Start with your controller.'}</h2>
          <p className="hero-text">{controller.connected ? 'Make a few thoughtful adjustments, or build a setup that does a little more for you.' : 'Connect a gamepad, choose a setup, and make the controls work the way you want.'}</p></div>
        <div className="hero-actions"><Button variant="primary" onClick={() => navigate('controller')}><Icon name="gamepad" size={18} />{controller.connected ? 'Open controller' : 'Set up controller'}<Icon name="arrowRight" size={16} /></Button>
          {controller.connected && <Button onClick={() => { void command(controller.outputActive ? 'outputStop' : 'outputStart').catch(() => {}); }}>
            <Icon name="power" size={16} />{controller.outputActive ? 'Disconnect output' : 'Connect output'}</Button>}
        </div>
      </section>
      <Card title="Right now" description="A quick look at what is running.">
        <div className="session-list">
          <div className="session-item"><span className="session-item-icon"><Icon name="gamepad" /></span><div><strong>Controller</strong><p>{controller.connected ? controller.deviceName || 'Connected gamepad' : 'Waiting for a gamepad'}</p></div>
            <Badge tone={controller.outputActive ? 'success' : 'neutral'}>{controller.outputActive ? 'Output on' : 'Off'}</Badge></div>
          <div className="session-item"><span className="session-item-icon"><Icon name="camera" /></span><div><strong>Remote Play</strong><p>{recording ? 'Saving a training clip' : visionRunning ? 'Live view is open' : 'Capture is stopped'}</p></div>
            <Badge tone={recording ? 'warning' : visionRunning ? 'success' : 'neutral'}>{recording ? 'Recording' : visionRunning ? 'Viewing' : 'Off'}</Badge></div>
          <div className="session-item"><span className="session-item-icon"><Icon name="mic" /></span><div><strong>Voice</strong><p>{voice.isRunning ? (voice.destination === 'headphones' ? 'Headphone preview' : 'Virtual microphone') : 'Microphone processing is off'}</p></div>
            <Badge tone={voice.isRunning ? 'success' : 'neutral'}>{voice.isRunning ? 'Live' : 'Off'}</Badge></div>
        </div>
      </Card>
    </div>
    <div className="row-spread"><h2>Pick up where you need to</h2><span className="subtle">Everything has its own space.</span></div>
    <div className="workspace-tools">
      <button type="button" className="tool-card" onClick={() => navigate('controller')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="sliders" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Make the controls yours</h3><p>Map a button, refine a sequence, or record a movement you want to keep.</p></div>
        <span className="tool-card-link">{profile?.name ? profile.name + ' setup' : 'Open your controller setup'}</span>
      </button>
      <button type="button" className="tool-card" onClick={() => navigate('vision')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="camera" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Bring gameplay into view</h3><p>Set up Remote Play capture and save the evidence for your shot profiles.</p></div>
        <span className="tool-card-link">Open Remote Play</span>
      </button>
      <button type="button" className="tool-card" onClick={() => navigate('voice')}>
        <div className="tool-card-top"><span className="tool-card-icon"><Icon name="audio" size={24} /></span><Icon name="arrowUpRight" size={20} /></div>
        <div><h3>Find your sound</h3><p>Shape your microphone, try a saved voice, or create a voice from a clip.</p></div>
        <span className="tool-card-link">Open your voice workspace</span>
      </button>
    </div>
    <div className="workspace-note"><Icon name="shield" size={21} /><div><strong>Your PC is the workspace.</strong> Setups, recordings and processing stay local. Outputs start when you choose.</div></div>
    <ResourceUse metrics={status.system || state.system || {}} />
  </div>;
}

function ResourceUse({ metrics }: { metrics: any }) {
  const percent = (value: any) => typeof value === 'number' && Number.isFinite(value) ? value.toFixed(1) + '%' : '—';
  const bytes = (value: any) => typeof value === 'number' && Number.isFinite(value) ? (value / 1024 ** 3).toFixed(1) + ' GB' : '—';
  return <details className="workspace-resources"><summary><span className="row"><Icon name="activity" size={18} /><strong>Resource use</strong></span><span className="subtle">CPU {percent(metrics.cpuPercent)} · GPU {percent(metrics.gpuPercent)}</span></summary>
    <div className="resource-grid">
      <div><span className="stat-label">CPU</span><strong>{percent(metrics.cpuPercent)}</strong></div>
      <div><span className="stat-label">Memory</span><strong>{bytes(metrics.memoryUsedBytes)}</strong><span className="subtle">of {bytes(metrics.memoryTotalBytes)}</span></div>
      <div><span className="stat-label">GPU</span><strong>{percent(metrics.gpuPercent)}</strong><span className="subtle">{metrics.gpuName || 'GPU not available'}</span></div>
      <div><span className="stat-label">Video memory</span><strong>{bytes(metrics.vramUsedBytes)}</strong><span className="subtle">of {bytes(metrics.vramTotalBytes)}</span></div>
    </div><p className="subtle">Resource sampling runs while this workspace is visible.</p>
  </details>;
}
