export interface NavigationSnapshot {
  enabled: boolean; generation: number; nowMs: number; sourceConnected?: boolean;
  events: { sequence: number; timestampMs: number; action: 'step' | 'activate' }[];
}
export interface NavigationCursor { generation: number; sequence: number }

export function navigationEvents(snapshot: NavigationSnapshot, cursor: NavigationCursor, estimatedNow: number) {
  if (!Number.isSafeInteger(snapshot.generation) || snapshot.generation < 0 || !Number.isFinite(snapshot.nowMs)
    || !Array.isArray(snapshot.events) || snapshot.events.length > 32)
    throw new Error('Navigation input is invalid. Restart switch navigation.');
  if (snapshot.generation < cursor.generation) return [];
  if (snapshot.generation > cursor.generation) { cursor.generation = snapshot.generation; cursor.sequence = 0; }
  const actions: ('step' | 'activate')[] = [];
  let last = 0;
  for (const event of snapshot.events) {
    if (!Number.isSafeInteger(event.sequence) || event.sequence <= last || !Number.isFinite(event.timestampMs)
      || event.timestampMs < 0 || event.timestampMs > snapshot.nowMs || !['step', 'activate'].includes(event.action))
      throw new Error('Navigation input is invalid. Restart switch navigation.');
    last = event.sequence;
    if (event.sequence <= cursor.sequence) continue;
    if (event.sequence !== cursor.sequence + 1 || Math.max(snapshot.nowMs, estimatedNow) - event.timestampMs > 1000)
      throw new Error('Navigation input was delayed or lost. Restart switch navigation.');
    actions.push(event.action); cursor.sequence = event.sequence;
  }
  return actions;
}

const selector = 'button,a[href],input:not([type="hidden"]),select,textarea,summary,[role="menuitem"],[tabindex]';
function available(element: HTMLElement) {
  return !element.matches(':disabled,[aria-disabled="true"]') && !element.closest('[inert],[aria-hidden="true"]')
    && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden';
}

/** DOM focus is the single scanning cursor; changes to a dialog or controls are
 * resolved before every step/activation so detached controls cannot be clicked. */
export type ScanEditable = HTMLInputElement | HTMLSelectElement | HTMLButtonElement;
export class SwitchScanner {
  private target: HTMLElement | null = null;
  private editing: ScanEditable | null = null;
  private observer: MutationObserver;
  constructor(private scope: () => Element, private onEdit: (field: ScanEditable | null) => void) {
    this.observer = new MutationObserver(() => this.refresh());
    this.observer.observe(document.body, { childList: true, subtree: true, attributes: true,
      attributeFilter: ['disabled', 'hidden', 'open', 'inert', 'aria-hidden', 'aria-disabled', 'class', 'style'] });
    this.refresh();
  }
  private targets() {
    const base = this.scope();
    if (this.editing && (!base.contains(this.editing) || !available(this.editing))) this.finishEditing();
    const scope = this.editing ? document.querySelector('[data-scan-editor]') || base : base;
    return [...scope.querySelectorAll<HTMLElement>(selector)].filter(available);
  }
  private focus(target: HTMLElement | null) {
    if (this.target === target && document.activeElement === target) { target?.classList.add('scan-target'); return; }
    this.target?.classList.remove('scan-target'); this.target = target;
    target?.classList.add('scan-target');
    target?.focus({ preventScroll: true }); target?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
  refresh() {
    const targets = this.targets();
    if (!this.target || !targets.includes(this.target)) this.focus(targets[0] || null);
    else if (!this.target.classList.contains('scan-target')) this.target.classList.add('scan-target');
  }
  step() {
    const targets = this.targets(), index = this.target ? targets.indexOf(this.target) : -1;
    this.focus(targets.length ? targets[(index + 1) % targets.length] : null);
  }
  activate() {
    const targets = this.targets();
    if (!this.target || !targets.includes(this.target)) { this.refresh(); return; }
    const target = this.target;
    if (target instanceof HTMLSelectElement || target.matches('[data-scan-choice]') || (target instanceof HTMLInputElement && ['range', 'number'].includes(target.type))) {
      this.editing = target as ScanEditable; this.onEdit(this.editing); return;
    }
    target.click();
  }
  adjust(direction: -1 | 1) {
    const field = this.editing;
    if (!field || !available(field) || !this.scope().contains(field)) { this.finishEditing(); return; }
    if (field instanceof HTMLButtonElement && field.matches('[data-scan-choice]')) {
      field.dispatchEvent(new CustomEvent('accessrelay-select-adjust', { detail: direction })); return;
    }
    if (field instanceof HTMLSelectElement) {
      const options = [...field.options].filter(option => !option.disabled && !option.hidden && !option.closest('optgroup:disabled'));
      if (!options.length) return;
      const current = options.findIndex(option => option.value === field.value);
      const next = options[(current + direction + options.length) % options.length];
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')!.set!.call(field, next.value);
    } else if (field instanceof HTMLInputElement) {
      const step = Number(field.step) > 0 ? Number(field.step) : 1;
      const min = field.min === '' ? 0 : Number(field.min), max = field.max === '' ? 100 : Number(field.max);
      const next = Math.min(max, Math.max(min, (Number(field.value) || 0) + direction * step));
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(field, String(Number(next.toFixed(6))));
    }
    field.dispatchEvent(new Event('input', { bubbles: true })); field.dispatchEvent(new Event('change', { bubbles: true }));
  }
  finishEditing() {
    const field = this.editing; this.editing = null; this.onEdit(null);
    if (field && available(field) && this.scope().contains(field)) this.focus(field);
  }
  dispose() { this.observer.disconnect(); this.target?.classList.remove('scan-target'); this.editing = null; }
}
