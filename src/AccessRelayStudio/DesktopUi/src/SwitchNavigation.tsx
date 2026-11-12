import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Button, Field, Modal, Notice, activeModal } from './ui';
import { SwitchScanner, navigationEvents, type NavigationCursor, type NavigationSnapshot } from './switch-scanner';

type Settings = { mode: 'automatic' | 'step'; source: 'keyboard' | 'controller'; intervalMs: number; debounceMs: number;
  stepKey: string; activateKey: string; step: string; activate: string };
const defaults: Settings = { mode: 'automatic', source: 'keyboard', intervalMs: 1500, debounceMs: 150,
  stepKey: 'Enter', activateKey: 'Space', step: 'dPadRight', activate: 'a' };
const keys = ['Space', 'Enter', 'F13', 'F14', 'F15'];
function load(): Settings {
  try {
    const value = JSON.parse(localStorage.getItem('accessrelay-switch-navigation-v1') || 'null');
    if (value && ['automatic', 'step'].includes(value.mode) && ['keyboard', 'controller'].includes(value.source)
      && keys.includes(value.stepKey) && keys.includes(value.activateKey) && value.stepKey !== value.activateKey
      && typeof value.intervalMs === 'number' && value.intervalMs >= 300 && value.intervalMs <= 5000
      && Number.isInteger(value.debounceMs) && value.debounceMs >= 50 && value.debounceMs <= 1000
      && typeof value.step === 'string' && typeof value.activate === 'string') return value;
  } catch { /* Storage may be unavailable; defaults remain usable. */ }
  return defaults;
}

export function SwitchNavigation({ controller, status, command }: { controller: any; status: any; command: (action: string, args?: any) => Promise<any> }) {
  const [settings, setSettings] = useState<Settings>(load), [open, setOpen] = useState(false), [active, setActive] = useState(false);
  const [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [error, setError] = useState('');
  const [editing, setEditing] = useState<HTMLInputElement | HTMLSelectElement | null>(null);
  const [container, setContainer] = useState<Element>(document.body);
  const scanner = useRef<SwitchScanner | null>(null), nativeActive = useRef(false), mounted = useRef(true);
  const cursor = useRef<NavigationCursor>({ generation: -1, sequence: 0 });
  const anchor = useRef({ native: 0, browser: 0 });
  const settingsRef = useRef(settings); settingsRef.current = settings;
  const args = (enabled: boolean) => ({ enabled, step: settingsRef.current.step, activate: settingsRef.current.activate, debounceMs: settingsRef.current.debounceMs });
  const stop = useCallback((reason = 'Switch navigation stopped.') => {
    setActive(false); setEditing(null); setMessage(reason);
    if (nativeActive.current) {
      nativeActive.current = false;
      void command('navigationSet', args(false)).catch(e => setError(String(e instanceof Error ? e.message : e)));
    }
  }, [command]);
  const start = useCallback(async () => {
    if (busy || active) return;
    setBusy(true); setError('');
    cursor.current = { generation: -1, sequence: 0 };
    try {
      const config = settingsRef.current;
      if (config.stepKey === config.activateKey || config.step === config.activate) throw new Error('Choose different step and select switches.');
      if (config.source === 'controller') {
        const snapshot = await command('navigationSet', args(true)) as NavigationSnapshot;
        nativeActive.current = true;
        if (!mounted.current) { await command('navigationSet', args(false)); nativeActive.current = false; return; }
        if (!snapshot || !snapshot.enabled || !Number.isFinite(snapshot.nowMs)) throw new Error('The controller navigation connection is unavailable.');
        anchor.current = { native: snapshot.nowMs, browser: performance.now() };
        cursor.current = { generation: snapshot.generation, sequence: 0 };
      }
      if (document.hidden || !document.hasFocus()) throw new Error('Bring Studio into focus before starting switch navigation.');
      try { localStorage.setItem('accessrelay-switch-navigation-v1', JSON.stringify(config)); } catch { /* Preferences are optional. */ }
      setOpen(false); setActive(true); setMessage('Switch navigation started. Escape or F8 stops it.');
    } catch (e) { stop('Switch navigation could not start.'); setError(String(e instanceof Error ? e.message : e)); }
    finally { if (mounted.current) setBusy(false); }
  }, [active, busy, command, stop]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      if (nativeActive.current) { nativeActive.current = false; void command('navigationSet', args(false)).catch(() => {}); }
    };
  }, [command]);
  useEffect(() => {
    if (!active) return;
    const scope = () => {
      const modal = activeModal();
      const menu = document.querySelector('[role="menu"][data-state="open"]');
      return menu && (!modal || modal.contains(menu)) ? menu : modal || document.querySelector('.app-shell') || document.body;
    };
    const current = new SwitchScanner(scope, setEditing); scanner.current = current;
    const destination = () => activeModal() || document.querySelector('.app-shell') || document.body;
    setContainer(destination());
    const observer = new MutationObserver(() => setContainer(destination()));
    observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['open'] });
    const timer = settings.mode === 'automatic' ? window.setInterval(() => {
      if (document.hasFocus() && !document.hidden) current.step();
    }, settings.intervalMs) : undefined;
    const blur = () => stop('Switch navigation stopped because Studio lost focus.');
    const stopAll = () => stop();
    const visibility = () => { if (document.hidden) blur(); };
    window.addEventListener('blur', blur); document.addEventListener('visibilitychange', visibility);
    window.addEventListener('accessrelay-stop-all', stopAll);
    return () => {
      window.clearInterval(timer); observer.disconnect(); current.dispose(); scanner.current = null;
      window.removeEventListener('blur', blur); document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('accessrelay-stop-all', stopAll);
    };
  }, [active, settings.mode, settings.intervalMs, stop]);
  useEffect(() => {
    const last = new Map<string, number>();
    const keydown = (event: KeyboardEvent) => {
      if (event.code === 'F8' && !event.repeat) { event.preventDefault(); event.stopImmediatePropagation(); if (active) stop(); else void start(); return; }
      if (!active) return;
      if (event.code === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); stop(); return; }
      if (settings.source !== 'keyboard' || ![settings.activateKey, settings.stepKey].includes(event.code)) return;
      event.preventDefault(); event.stopImmediatePropagation();
      if (event.repeat || performance.now() - (last.get(event.code) ?? -Infinity) < settings.debounceMs) return;
      last.set(event.code, performance.now());
      if (event.code === settings.activateKey) scanner.current?.activate();
      else if (settings.mode === 'step') scanner.current?.step();
    };
    window.addEventListener('keydown', keydown, true);
    return () => window.removeEventListener('keydown', keydown, true);
  }, [active, settings, start, stop]);
  useEffect(() => {
    if (!active || settings.source !== 'controller' || !status.navigation) return;
    const snapshot = status.navigation as NavigationSnapshot;
    // The command acknowledgement can precede a periodic status update. Ignore
    // snapshots from before activation, including their old disabled/connection state.
    if (snapshot.generation < cursor.current.generation) return;
    if (!snapshot.enabled) { nativeActive.current = false; stop('Controller navigation ended.'); return; }
    if (status.connected === false) { stop('Controller disconnected. Switch navigation stopped.'); return; }
    try {
      const now = anchor.current.native + performance.now() - anchor.current.browser;
      const actions = navigationEvents(snapshot, cursor.current, now);
      for (const action of actions) {
        if (action === 'activate') scanner.current?.activate(); else if (settings.mode === 'step') scanner.current?.step();
      }
    } catch (e) { stop(); setError(String(e instanceof Error ? e.message : e)); }
  }, [active, settings.source, settings.mode, status.navigation, status.connected, stop]);
  const label = editing?.getAttribute('aria-label') || (editing?.id ? document.querySelector(`label[for="${CSS.escape(editing.id)}"]`)?.textContent : '') || 'Selected control';
  const toolbar = active && <div className="scan-toolbar" aria-label="Switch navigation controls">
    {editing ? <div data-scan-editor="" className="row"><span>{label}</span><Button onClick={() => scanner.current?.adjust(-1)}>Decrease / previous</Button>
      <Button onClick={() => scanner.current?.adjust(1)}>Increase / next</Button><Button onClick={() => scanner.current?.finishEditing()}>Finish editing</Button>
      <Button onClick={() => stop()}>Stop scanning</Button></div>
      : <><span>{settings.mode === 'automatic' ? 'Automatic scanning' : 'Step scanning'} · {settings.source === 'keyboard' ? `${settings.activateKey} selects` : 'Controller switches select'}</span>
        <Button onClick={() => stop()}>Stop scanning</Button></>}
  </div>;
  return <>
    <Button variant="ghost" onClick={() => setOpen(true)} aria-label="Switch navigation">Switch navigation</Button>
    <Modal open={open} onClose={() => { if (!busy) setOpen(false); }} title="Switch navigation" description="Use one switch to select as focus moves, or two switches to step and select. F8 starts or stops; Escape stops scanning."
      footer={<><Button disabled={busy} onClick={() => setOpen(false)}>Close</Button>{active ? <Button onClick={() => stop()}>Stop scanning</Button> : <Button variant="primary" busy={busy} onClick={() => void start()}>Start scanning</Button>}</>}>
      <Field label="Scan mode"><select disabled={active || busy} value={settings.mode} onChange={event => setSettings(value => ({ ...value, mode: event.target.value as Settings['mode'] }))}>
        <option value="automatic">One switch · automatic scanning</option><option value="step">Two switches · step and select</option></select></Field>
      <Field label="Switch source"><select disabled={active || busy} value={settings.source} onChange={event => setSettings(value => ({ ...value, source: event.target.value as Settings['source'] }))}>
        <option value="keyboard">Keyboard switches</option><option value="controller">Selected physical controller</option></select></Field>
      {settings.source === 'controller' ? <><Notice>Controller navigation pauses gameplay output and sequences. Release controls before returning to the game. Scanning stops when Studio loses focus.</Notice>
        {(['step', 'activate'] as const).map(key => <Field key={key} label={key === 'step' ? 'Controller step switch' : 'Controller select switch'}><select disabled={active || busy} value={settings[key]} onChange={event => setSettings(value => ({ ...value, [key]: event.target.value }))}>
          {(controller.controls || []).map((control: any) => <option key={control.id} value={control.id}>{control.label}</option>)}</select></Field>)}</>
        : (['stepKey', 'activateKey'] as const).map(key => <Field key={key} label={key === 'stepKey' ? 'Step key' : 'Select key'}><select disabled={active || busy} value={settings[key]} onChange={event => setSettings(value => ({ ...value, [key]: event.target.value }))}>
          {keys.map(code => <option key={code} value={code}>{code}</option>)}</select></Field>)}
      <Field label="Scan interval (ms)"><input type="number" min={300} max={5000} step={100} disabled={active || busy} value={settings.intervalMs} onChange={event => setSettings(value => ({ ...value, intervalMs: Math.max(300, Math.min(5000, Number(event.target.value) || 300)) }))} /></Field>
      <Field label="Switch debounce (ms)"><input type="number" min={50} max={1000} step={10} disabled={active || busy} value={settings.debounceMs} onChange={event => setSettings(value => ({ ...value, debounceMs: Math.max(50, Math.min(1000, Number(event.target.value) || 50)) }))} /></Field>
      <p className="subtle">For sliders and choices, select the field, scan to decrease/previous or increase/next, then choose Finish editing. Text fields still need a text-entry device or the Windows on-screen keyboard.</p>
      {message && <Notice>{message}</Notice>}{error && <Notice tone="danger">{error}</Notice>}
    </Modal>
    {toolbar && createPortal(toolbar, container.isConnected ? container : document.body)}
    {!open && message && <span className="sr-only" role="status">{message}</span>}
    {!open && error && <div className="toast"><Notice tone="danger">{error}</Notice></div>}
  </>;
}
