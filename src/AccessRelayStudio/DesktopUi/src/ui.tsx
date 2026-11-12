import { cloneElement, isValidElement, useEffect, useId, useRef, useState } from 'react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import {
  Activity, ArrowDown, ArrowLeft, ArrowRight, ArrowUpRight, AudioLines, BookOpen, Camera, Check,
  ChevronDown, ChevronLeft, ChevronRight, Circle, CircleHelp, CirclePause, CirclePlay, Copy,
  Download, Ellipsis, Eye, FileCode2, FilePlus2, FolderOpen, Gamepad2, Gauge, Headphones,
  Info, LayoutDashboard, List, LoaderCircle, Mic, Monitor, MoreHorizontal, Pencil, Plus,
  Power, Radio, RefreshCw, Save, Search, Settings2, ShieldCheck, SlidersHorizontal, Square,
  Trash2, Upload, Volume2, Waves, X, Zap
} from 'lucide-react';

export interface PageProps { state: any; status: any; command: (action: string, args?: any) => Promise<any> }
const unsavedDrafts = new Set<symbol>();
const draftListeners = new Set<(dirty: boolean) => void>();
let lastDirty = false;
function publishDraftState() {
  const dirty = hasUnsavedChanges();
  if (dirty === lastDirty) return;
  lastDirty = dirty; draftListeners.forEach(listener => listener(dirty));
}
export function hasUnsavedChanges() { return unsavedDrafts.size > 0; }
export function subscribeUnsavedChanges(listener: (dirty: boolean) => void) {
  draftListeners.add(listener); listener(hasUnsavedChanges());
  return () => { draftListeners.delete(listener); };
}
export function useUnsavedChanges(dirty: boolean) {
  const token = useRef(Symbol('local-draft'));
  useEffect(() => {
    if (dirty) unsavedDrafts.add(token.current); else unsavedDrafts.delete(token.current);
    publishDraftState();
    return () => { unsavedDrafts.delete(token.current); publishDraftState(); };
  }, [dirty]);
}
const icons: Record<string, any> = {
  activity: Activity, arrowDown: ArrowDown, arrowLeft: ArrowLeft, arrowRight: ArrowRight,
  arrowUpRight: ArrowUpRight, audio: AudioLines, book: BookOpen, camera: Camera, check: Check,
  chevronDown: ChevronDown, chevronLeft: ChevronLeft, chevronRight: ChevronRight, circle: Circle,
  help: CircleHelp, pause: CirclePause, play: CirclePlay, copy: Copy, download: Download,
  more: MoreHorizontal, eye: Eye, code: FileCode2, filePlus: FilePlus2, folder: FolderOpen,
  gamepad: Gamepad2, gauge: Gauge, headphones: Headphones, info: Info, dashboard: LayoutDashboard,
  list: List, loader: LoaderCircle, mic: Mic, monitor: Monitor, pencil: Pencil, plus: Plus,
  power: Power, radio: Radio, refresh: RefreshCw, save: Save, search: Search, settings: Settings2,
  shield: ShieldCheck, sliders: SlidersHorizontal, stop: Square, trash: Trash2, upload: Upload,
  volume: Volume2, waves: Waves, x: X, zap: Zap
};
export function Icon({ name, size = 20, className = '' }: { name: string; size?: number; className?: string }) {
  const Component = icons[name] || Circle;
  return <Component size={size} strokeWidth={1.75} aria-hidden="true" className={className} />;
}
export function Button({ variant = 'secondary', busy = false, children, className = '', disabled, ...props }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' | 'ghost' | 'danger'; busy?: boolean }) {
  return <button type="button" {...props} className={'button button-' + variant + ' ' + className}
    disabled={disabled || busy} aria-busy={busy || undefined}>{busy && <Icon name="loader" size={17} className="spin" />}{children}</button>;
}
export function Card({ title, description, actions, children, className = '' }:
  { title?: ReactNode; description?: ReactNode; actions?: ReactNode; children?: ReactNode; className?: string }) {
  return <section className={'card ' + className}>
    {(title || description || actions) && <div className="card-heading"><div>{title && <h2>{title}</h2>}
      {description && <p className="subtle">{description}</p>}</div>{actions}</div>}{children}
  </section>;
}
export function Field({ label, hint, children, className = '' }: { label: ReactNode; hint?: ReactNode; children: ReactNode; className?: string }) {
  const id = useId(), wrapper = useRef<HTMLDivElement>(null);
  const direct = isValidElement(children) && typeof children.type === 'string' && ['input', 'select', 'textarea'].includes(children.type);
  const inputId = direct ? ((children.props as any).id || id) : id;
  useEffect(() => {
    if (direct) return;
    const input = wrapper.current?.querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('input:not([type="hidden"]),select,textarea');
    const fieldLabel = wrapper.current?.querySelector<HTMLLabelElement>('label');
    if (!input || !fieldLabel) return;
    if (!input.id) input.id = id + '-input';
    fieldLabel.htmlFor = input.id;
    if (hint && !input.hasAttribute('aria-describedby')) input.setAttribute('aria-describedby', id + '-hint');
  }, [children, direct, hint, id]);
  return <div className={'field ' + className} ref={wrapper}><label id={id + '-label'} htmlFor={inputId}>{label}</label>
    {direct ? cloneElement(children as any, { id: inputId, 'aria-describedby': hint ? id + '-hint' : undefined })
      : <div aria-labelledby={id + '-label'}>{children}</div>}
    {hint && <p id={id + '-hint'} className="field-hint">{hint}</p>}</div>;
}
export function Toggle({ label, description, checked, onChange, disabled = false }: {
  label: ReactNode; description?: ReactNode; checked: boolean; onChange: (value: boolean) => void; disabled?: boolean;
}) {
  return <label className={'toggle-row' + (disabled ? ' is-disabled' : '')}><span><span className="toggle-label">{label}</span>
    {description && <span className="field-hint">{description}</span>}</span>
    <span className="switch"><input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)} disabled={disabled} />
      <span className="switch-track" aria-hidden="true" /></span></label>;
}
export function Badge({ tone = 'neutral', children }: { tone?: 'success' | 'warning' | 'neutral' | 'danger' | 'mint'; children: ReactNode }) {
  return <span className={'badge badge-' + tone}>{children}</span>;
}
export function EmptyState({ title, description, icon = 'folder', action }: { title: string; description: ReactNode; icon?: string; action?: ReactNode }) {
  return <div className="empty-state"><span className="empty-icon"><Icon name={icon} size={28} /></span>
    <h3>{title}</h3><p>{description}</p>{action}</div>;
}
export function SectionHeader({ title, description, eyebrow, actions }: { title: string; description?: ReactNode; eyebrow?: string; actions?: ReactNode }) {
  return <div className="section-header"><div>{eyebrow && <p className="eyebrow">{eyebrow}</p>}<h1>{title}</h1>{description && <p>{description}</p>}</div>
    {actions && <div className="header-actions">{actions}</div>}</div>;
}
export function Tabs({ items, value, onChange, ariaLabel = 'Section' }: {
  items: { id: string; label: string; count?: number }[]; value: string; onChange: (value: string) => void; ariaLabel?: string;
}) {
  return <div className="tabs" role="tablist" aria-label={ariaLabel}>{items.map((item, index) =>
    <button type="button" key={item.id} role="tab" tabIndex={value === item.id ? 0 : -1}
      aria-selected={value === item.id} className={value === item.id ? 'active' : ''}
      onClick={() => onChange(item.id)} onKeyDown={event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : -1) + items.length) % items.length;
        onChange(items[next].id); (event.currentTarget.parentElement?.children[next] as HTMLElement)?.focus();
      }}>{item.label}{item.count !== undefined && <span className="tab-count">{item.count}</span>}</button>)}</div>;
}
export function Notice({ tone = 'info', title, children }: { tone?: 'info' | 'success' | 'warning' | 'danger'; title?: ReactNode; children: ReactNode }) {
  return <div className={'notice notice-' + tone} role={tone === 'danger' ? 'alert' : 'status'}><Icon name={tone === 'success' ? 'check' : 'info'} size={19} />
    <div>{title && <strong>{title}</strong>}<div>{children}</div></div></div>;
}
export function Meter({ value = 0, label, max = 100, tone = 'mint' }: { value?: number; label?: string; max?: number; tone?: string }) {
  const percentage = Math.min(100, Math.max(0, (Number.isFinite(value) ? value : 0) / max * 100));
  return <div className="meter-group">{label && <div className="meter-label"><span>{label}</span><span>{Math.round(percentage)}%</span></div>}
    <div className={'meter meter-' + tone} role="meter" aria-label={label || 'Level'} aria-valuemin={0} aria-valuemax={max} aria-valuenow={value}>
      <span style={{ width: percentage + '%' }} /></div></div>;
}
export function Modal({ open, onClose, title, description, children, footer, wide = false }: {
  open: boolean; onClose: () => void; title: string; description?: ReactNode; children?: ReactNode; footer?: ReactNode; wide?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null); const titleId = useId(); const closeRef = useRef(onClose);
  closeRef.current = onClose;
  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement as HTMLElement | null;
    const focus = () => {
      const elements = ref.current?.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex="0"]');
      return [...(elements || [])].filter(item => item.offsetParent !== null);
    };
    const timer = window.setTimeout(() => (focus()[0] || ref.current)?.focus(), 0);
    const keydown = (event: KeyboardEvent) => {
      const dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
      if (dialogs[dialogs.length - 1] !== ref.current) return;
      if (event.key === 'Escape') { event.preventDefault(); closeRef.current(); }
      if (event.key === 'Tab') {
        const elements = focus(); if (!elements.length) { event.preventDefault(); return; }
        const first = elements[0], last = elements[elements.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }
    };
    document.addEventListener('keydown', keydown);
    const overflow = document.body.style.overflow; document.body.style.overflow = 'hidden';
    return () => { window.clearTimeout(timer); document.removeEventListener('keydown', keydown); document.body.style.overflow = overflow; previous?.focus(); };
  }, [open]);
  if (!open) return null;
  return <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <div className={'modal ' + (wide ? 'modal-wide' : '')} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} ref={ref}>
      <div className="modal-heading"><div><h2 id={titleId}>{title}</h2>{description && <p>{description}</p>}</div>
        <Button variant="ghost" className="icon-button" aria-label="Close dialog" onClick={onClose}><Icon name="x" /></Button></div>
      <div className="modal-body">{children}</div>{footer && <div className="modal-footer">{footer}</div>}
    </div></div>;
}
export function ConfirmDialog({ open, title, description, onClose, onConfirm, confirmLabel = 'Delete' }: {
  open: boolean; title: string; description: ReactNode; onClose: () => void; onConfirm: () => void | Promise<void>; confirmLabel?: string;
}) {
  const [busy, setBusy] = useState(false); const [error, setError] = useState('');
  return <Modal open={open} onClose={() => { if (!busy) { setError(''); onClose(); } }} title={title}
    footer={<><Button onClick={onClose} disabled={busy}>Cancel</Button><Button variant="danger" busy={busy} onClick={async () => {
      setBusy(true); setError(''); try { await onConfirm(); onClose(); } catch (e) { setError(String(e instanceof Error ? e.message : e)); } finally { setBusy(false); }
    }}>{confirmLabel}</Button></>}><p>{description}</p>{error && <Notice tone="danger">{error}</Notice>}</Modal>;
}
export function DiscardDialog({ open, onKeep, onDiscard }: { open: boolean; onKeep: () => void; onDiscard: () => void }) {
  return <Modal open={open} onClose={onKeep} title="Discard unsaved changes?"
    description="You have edits that have not been saved. Keep editing, or discard them to continue."
    footer={<><Button variant="danger" onClick={onDiscard}>Discard changes</Button><Button variant="primary" onClick={onKeep}>Keep editing</Button></>} />;
}
export function humanize(value: string = '') {
  return String(value).replace(/([A-Z])/g, ' $1').replace(/[_-]/g, ' ').replace(/^./, letter => letter.toUpperCase());
}
export function formatMs(value: any) { return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(value < 10 ? 2 : 1) + ' ms' : '—'; }
export function formatDuration(value: number = 0) { return value < 1000 ? value + ' ms' : (value / 1000).toFixed(2) + ' s'; }
