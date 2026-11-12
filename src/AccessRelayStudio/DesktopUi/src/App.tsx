import { useCallback, useEffect, useState } from 'react';
import { desktop, isDesignPreview } from './api';
import type { StudioState, StudioStatus } from './api';
import { Badge, Button, DiscardDialog, Icon, Modal, Notice, hasUnsavedChanges, subscribeUnsavedChanges } from './ui';
import { WorkspacePage } from './pages/WorkspacePage';
import { ControllerPage } from './pages/ControllerPage';
import { SettingsPage } from './pages/SettingsPage';
import { VoicePage } from './pages/VoicePage';
import { VisionPage } from './pages/VisionPage';
import { AnnouncementContext, LiveAnnouncements, useStatusAnnouncements } from './announcements';
import { SwitchNavigation } from './SwitchNavigation';

type Page = 'workspace' | 'controller' | 'vision' | 'voice' | 'settings';
const navigation: { id: Page; label: string; icon: string }[] = [
  { id: 'workspace', label: 'Workspace', icon: 'dashboard' },
  { id: 'controller', label: 'Controller', icon: 'gamepad' },
  { id: 'vision', label: 'Remote Play', icon: 'camera' },
  { id: 'voice', label: 'Voice', icon: 'audio' }
];
export function App() {
  const [page, setPage] = useState<Page>('workspace');
  const [pendingPage, setPendingPage] = useState<Page | null>(null);
  const [state, setState] = useState<StudioState | null>(null);
  const [status, setStatus] = useState<StudioStatus>({});
  const [error, setError] = useState('');
  const [bootError, setBootError] = useState('');
  const [help, setHelp] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [closeRequested, setCloseRequested] = useState(false), [closing, setClosing] = useState(false);
  const navigate = useCallback((next: Page) => {
    if (next === page) return;
    if (hasUnsavedChanges()) setPendingPage(next); else setPage(next);
  }, [page]);
  const bootstrap = useCallback(async () => {
    setBootError('');
    try { const result = await desktop.command('app.bootstrap'); setState(result); }
    catch (e) { setBootError(e instanceof Error ? e.message : String(e)); }
  }, []);
  useEffect(() => {
    const unsubscribe = desktop.subscribe(message => {
      if (message.type === 'snapshot') setState(message.state);
      if (message.type === 'status') setStatus(message.status);
      if (message.type === 'closeRequested') { setPendingPage(null); setCloseRequested(true); }
    });
    void bootstrap();
    return unsubscribe;
  }, [bootstrap]);
  useEffect(() => subscribeUnsavedChanges(dirty => {
    void desktop.command('app.draftState', { dirty }).catch(() => {});
  }), []);
  useEffect(() => {
    const presentation = () => { void desktop.command('app.page', { page, visible: !document.hidden }).catch(() => {}); };
    presentation(); document.addEventListener('visibilitychange', presentation);
    return () => document.removeEventListener('visibilitychange', presentation);
  }, [page]);
  const invoke = useCallback(async (method: string, args: any = {}) => {
    try { return await desktop.command(method, args); }
    catch (e) { const message = e instanceof Error ? e.message : String(e); setError(message); throw e; }
  }, []);
  const controllerCommand = useCallback((action: string, args?: any) => invoke('controller.' + action, args), [invoke]);
  const visionCommand = useCallback((action: string, args?: any) => invoke('vision.' + action, args), [invoke]);
  const voiceCommand = useCallback((action: string, args?: any) => invoke('voice.' + action, args), [invoke]);
  const appCommand = useCallback((action: string, args?: any) => invoke('app.' + action, args), [invoke]);
  const controllerStatus = status.controller || state?.controller?.status || {};
  const voiceStatus = status.voice || state?.voice?.status || {};
  const visionStatus = status.vision || state?.vision?.status || {};
  const announcement = useStatusAnnouncements(!!state, controllerStatus, voiceStatus, visionStatus, error);
  const currentLabel = page === 'settings' ? 'Settings' : navigation.find(item => item.id === page)?.label || 'Workspace';
  if (!state) return <main className="loading-shell"><div className="loading-content">
    <img src="./brand.png" alt="" /><h1>AccessRelay Studio</h1>
    <p>{bootError || 'Opening your workspace…'}</p>
    {bootError ? <Button variant="primary" onClick={() => void bootstrap()}>Try again</Button> : <Icon name="loader" className="spin" />}
  </div></main>;
  return <AnnouncementContext.Provider value={announcement}><div className="app-shell">
    <LiveAnnouncements />
    <aside className="sidebar" aria-label="Main navigation">
      <div className="brand"><img src="./brand.png" alt="" /><div className="brand-name">AccessRelay<span>Studio</span></div></div>
      <p className="nav-label">Your tools</p>
      <nav className="primary-nav">{navigation.map(item => <button type="button" key={item.id}
        className={'nav-item' + (page === item.id ? ' active' : '')} onClick={() => navigate(item.id)}
        aria-label={item.label} aria-current={page === item.id ? 'page' : undefined} title={item.label}>
        <Icon name={item.icon} size={21} /><span>{item.label}</span>{page === item.id && <span className="nav-active-mark" />}
      </button>)}</nav>
      <div className="sidebar-bottom">
        <button type="button" className={'nav-item' + (page === 'settings' ? ' active' : '')} onClick={() => navigate('settings')}
          aria-current={page === 'settings' ? 'page' : undefined} aria-label="Settings" title="Settings"><Icon name="settings" size={21} /><span>Settings</span></button>
        <button type="button" className="nav-item" onClick={() => setHelp(true)} aria-label="Help" title="Help"><Icon name="help" size={21} /><span>A little guidance</span></button>
        <div className="sidebar-note"><Icon name="shield" size={17} /><div>Runs on your PC.<br />Your setups stay here.</div></div>
      </div>
    </aside>
    <div className="app-content">
      {isDesignPreview && <div className="preview-banner">Design preview — no hardware, capture or audio sessions are connected.</div>}
      <header className="app-topbar">
        <div className="topbar-breadcrumb"><span>Studio</span><Icon name="chevronRight" size={15} /><strong>{currentLabel}</strong></div>
        <div className="topbar-actions">
          <SwitchNavigation controller={state.controller} status={controllerStatus} command={controllerCommand} />
          {(voiceStatus.isRunning || voiceStatus.commands?.recognition?.running) && <Badge tone="success"><Icon name="mic" size={15} />Mic live</Badge>}
          <Badge>Beta</Badge>
          <Button variant="ghost" className="stop-everything" busy={stopping} onClick={async () => {
            window.dispatchEvent(new Event('accessrelay-stop-all'));
            setStopping(true); try { await controllerCommand('stopAll'); } catch {} finally { setStopping(false); }
          }}><Icon name="stop" size={15} />Stop everything</Button>
        </div>
      </header>
      <main className="app-main" id="main-content"><div className="content-width">
        {page === 'workspace' && <WorkspacePage state={state} status={{ controller: controllerStatus, voice: voiceStatus, vision: visionStatus, system: status.system || state.system }}
          command={invoke} navigate={navigate} />}
        {page === 'controller' && <ControllerPage state={state.controller || {}} status={controllerStatus} command={controllerCommand} />}
        {page === 'vision' && <VisionPage state={state.vision || {}} status={visionStatus} command={visionCommand} />}
        {page === 'voice' && <VoicePage state={state.voice || {}} status={voiceStatus} command={voiceCommand} />}
        {page === 'settings' && <SettingsPage state={{ ...state.settings, version: state.version, controller: state.controller }}
          status={controllerStatus} command={appCommand} />}
      </div></main>
    </div>
    {error && <div className="toast"><Notice tone="danger" announce={false}><div className="row"><span>{error}</span>
      <Button variant="ghost" className="icon-button" aria-label="Dismiss message" onClick={() => setError('')}><Icon name="x" size={16} /></Button></div></Notice></div>}
    <DiscardDialog open={pendingPage !== null} onKeep={() => setPendingPage(null)} onDiscard={() => { if (pendingPage) setPage(pendingPage); setPendingPage(null); }} />
    <Modal open={closeRequested} onClose={() => { if (!closing) setCloseRequested(false); }} title="Close Studio and discard unsaved work?"
      description="There are edits or controller recordings that have not been saved. Keep editing to save them before you close."
      footer={<><Button variant="danger" busy={closing} onClick={async () => { setClosing(true); try { await appCommand('close', { discard: true }); } catch { setClosing(false); } }}>Close and discard</Button>
        <Button variant="primary" disabled={closing} onClick={() => setCloseRequested(false)}>Keep editing</Button></>} />
    <Modal open={help} onClose={() => setHelp(false)} title="Find your way around" description="A few starting points for your workspace."
      footer={<Button variant="primary" onClick={() => setHelp(false)}>Got it</Button>}>
      <div className="stack"><div><h3>Controller</h3><p className="subtle">Choose your controller and setup. Map buttons, record sequences, or write Lua, then turn on the virtual controller when you are ready.</p></div>
        <div><h3>Remote Play</h3><p className="subtle">Choose your player and gameplay window. Set up the capture areas, then record a clip. Viewing alone does not save footage.</p></div>
        <div><h3>Voice</h3><p className="subtle">Choose a microphone and voice. Listen in headphones before sending it through the virtual microphone.</p></div>
        <Notice>Stop everything ends vision, voice and controller output together. Opening Studio never starts them automatically.</Notice>
      </div>
    </Modal>
  </div></AnnouncementContext.Provider>;
}
