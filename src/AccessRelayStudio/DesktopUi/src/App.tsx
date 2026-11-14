import { useCallback, useEffect, useState } from 'react';
import { desktop, isDesignPreview } from './api';
import type { StudioState, StudioStatus } from './api';
import { Badge, Button, DiscardDialog, Icon, Modal, Notice, hasUnsavedChanges, subscribeUnsavedChanges } from './ui';
import { WorkspacePage } from './pages/WorkspacePage';
import { ControllerPage } from './pages/ControllerPage';
import { SettingsPage } from './pages/SettingsPage';
import { VoicePage } from './pages/VoicePage';
import { VisionPage } from './pages/VisionPage';
import { TrainingJobsPanel } from './pages/TrainingJobsPanel';
import { MarketplacePage } from './pages/MarketplacePage';
import { AnnouncementContext, LiveAnnouncements, useStatusAnnouncements } from './announcements';
import { currentTheme, saveTheme, type Theme } from './theme';
import { SwitchNavigation } from './SwitchNavigation';
import { GamePluginProvider } from './game-plugins/registry';

type Page = 'workspace' | 'controller' | 'vision' | 'training' | 'voice' | 'marketplace' | 'settings';
const navigation: { id: Page; label: string; icon: string }[] = [
  { id: 'workspace', label: 'Workspace', icon: 'dashboard' },
  { id: 'controller', label: 'Controller', icon: 'gamepad' },
  { id: 'vision', label: 'Live Gameplay', icon: 'camera' },
  { id: 'training', label: 'Model Training', icon: 'book' },
  { id: 'voice', label: 'Voice', icon: 'audio' },
  { id: 'marketplace', label: 'Marketplace', icon: 'store' }
];
export function App() {
  const [navigationRequest, setNavigationRequest] = useState(0);
  const [theme, setTheme] = useState<Theme>(currentTheme);
  useEffect(() => { void desktop.command('app.themeSet', { theme }).catch(() => {}); }, [theme]);
  const [page, setPage] = useState<Page>('workspace');
  const [pendingPage, setPendingPage] = useState<Page | null>(null);
  const [state, setState] = useState<StudioState | null>(null);
  const [status, setStatus] = useState<StudioStatus>({});
  const [error, setError] = useState('');
  const [bootError, setBootError] = useState('');
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
  const marketplaceCommand = useCallback((action: string, args?: any) => invoke('app.marketplace.' + action, args), [invoke]);
  const controllerStatus = status.controller || state?.controller?.status || {};
  const voiceStatus = status.voice || state?.voice?.status || {};
  const visionStatus = status.vision || state?.vision?.status || {};
  const captureWindow = { detached: (status.gameplayWindow || state?.gameplayWindow)?.detached ?? false,
    detach: () => appCommand('gameplayDetach'), attach: () => appCommand('gameplayAttach') };
  const announcement = useStatusAnnouncements(!!state, controllerStatus, voiceStatus, visionStatus, error);
  const currentLabel = page === 'settings' ? 'Settings' : navigation.find(item => item.id === page)?.label || 'Workspace';
  if (!state) return <main className="loading-shell"><div className="loading-content">
    <img src="./brand.png" alt="" /><h1>AccessRelay Studio</h1>
    <p>{bootError || 'Opening your workspace…'}</p>
    {bootError ? <Button variant="primary" onClick={() => void bootstrap()}>Try again</Button> : <Icon name="loader" className="spin" />}
  </div></main>;
  return <GamePluginProvider descriptors={state.gamePlugins}><AnnouncementContext.Provider value={announcement}><div className="app-shell">
    <LiveAnnouncements />
    <aside className="sidebar" aria-label="Main navigation">
      <div className="brand"><img src="./brand.png" alt="" /><div className="brand-name">AccessRelay<span>Studio</span></div></div>
      <nav className="primary-nav">{navigation.map(item => <Button variant="unstyled" type="button" key={item.id}
        className={'nav-item' + (page === item.id ? ' active' : '')} onClick={() => navigate(item.id)}
        aria-label={item.label} aria-current={page === item.id ? 'page' : undefined} title={item.label}>
        <Icon name={item.icon} size={21} /><span>{item.label}</span>{page === item.id && <span className="nav-active-mark" />}
      </Button>)}</nav>
      <div className="sidebar-bottom">
        <Button variant="unstyled" type="button" className={'nav-item' + (page === 'settings' ? ' active' : '')} onClick={() => navigate('settings')}
          aria-current={page === 'settings' ? 'page' : undefined} aria-label="Settings" title="Settings"><Icon name="settings" size={21} /><span>Settings</span></Button>
      </div>
    </aside>
    <div className="app-content">
      {isDesignPreview && <div className="preview-banner">Design preview — no hardware, capture or audio sessions are connected.</div>}
      <header className="app-topbar">
        <div className="topbar-breadcrumb"><span>Studio</span><Icon name="chevronRight" size={15} /><strong>{currentLabel}</strong></div>
        <div className="topbar-actions">
          <Badge>Beta</Badge>
          <Button className="theme-toggle" onClick={() => { const next = theme === 'light' ? 'dark' : 'light'; saveTheme(next); setTheme(next); }}
            aria-label={theme === 'light' ? 'Switch to dark mode' : 'Switch to light mode'}>
            <Icon name={theme === 'light' ? 'moon' : 'sun'} size={18} />{theme === 'light' ? 'Dark mode' : 'Light mode'}
          </Button>
        </div>
      </header>
      <main className="app-main" id="main-content"><div className="content-width">
        {page === 'workspace' && <WorkspacePage state={state} status={{ controller: controllerStatus, voice: voiceStatus, vision: visionStatus, system: status.system || state.system }}
          command={invoke} navigate={navigate} />}
        {page === 'controller' && <ControllerPage state={state.controller || {}} status={controllerStatus} command={controllerCommand} />}
        {page === 'vision' && <VisionPage state={state.vision || {}} status={visionStatus} command={visionCommand} mode="live" captureWindow={captureWindow} />}
        {page === 'training' && <div className="page-stack"><VisionPage state={state.vision || {}} status={visionStatus} command={visionCommand} mode="training" captureWindow={captureWindow} openMarketplace={() => navigate('marketplace')} />
          <details><summary>Assistant offline workflows</summary><TrainingJobsPanel initial={state.settings?.assistant} command={appCommand} /></details></div>}
        {page === 'voice' && <VoicePage state={state.voice || {}} status={voiceStatus} command={voiceCommand} />}
        {page === 'marketplace' && <MarketplacePage state={state.marketplace || {}} command={marketplaceCommand} openTraining={async pluginId => {
          if (state.vision?.selectedPluginId !== pluginId) await visionCommand('pluginSelect', { id: pluginId });
          navigate('training');
        }} />}
        {page === 'settings' && <SettingsPage state={{ ...state.settings, version: state.version, controller: state.controller }}
          status={controllerStatus} command={appCommand} openSwitchNavigation={() => setNavigationRequest(value => value + 1)} />}
      </div></main>
    </div>
    <SwitchNavigation controller={state.controller || {}} status={controllerStatus} command={controllerCommand}
      showLauncher={false} openRequest={navigationRequest} />
    {error && <div className="toast"><Notice tone="danger" announce={false}><div className="row"><span>{error}</span>
      <Button variant="ghost" className="icon-button" aria-label="Dismiss message" onClick={() => setError('')}><Icon name="x" size={16} /></Button></div></Notice></div>}
    <DiscardDialog open={pendingPage !== null} onKeep={() => setPendingPage(null)} onDiscard={() => { if (pendingPage) setPage(pendingPage); setPendingPage(null); }} />
    <Modal open={closeRequested} onClose={() => { if (!closing) setCloseRequested(false); }} title="Close Studio and discard unsaved work?"
      description="There are edits or controller recordings that have not been saved. Keep editing to save them before you close."
      footer={<><Button variant="danger" busy={closing} onClick={async () => { setClosing(true); try { await appCommand('close', { discard: true }); } catch { setClosing(false); } }}>Close and discard</Button>
        <Button variant="primary" disabled={closing} onClick={() => setCloseRequested(false)}>Keep editing</Button></>} />
  </div></AnnouncementContext.Provider></GamePluginProvider>;
}
