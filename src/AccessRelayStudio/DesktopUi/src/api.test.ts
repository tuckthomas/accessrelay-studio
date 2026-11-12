import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DesktopBridge } from './api';

describe('native desktop bridge', () => {
  let handler: ((event: MessageEvent) => void) | undefined;
  let sent: any[];
  let bridge: DesktopBridge;
  beforeEach(() => {
    sent = [];
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      postMessage: (message: any) => sent.push(message),
      addEventListener: (_: string, value: (event: MessageEvent) => void) => { handler = value; },
      removeEventListener: () => { handler = undefined; }
    } } });
    bridge = new DesktopBridge();
  });
  afterEach(() => { bridge.dispose(); vi.useRealTimers(); Object.defineProperty(window, 'chrome', { configurable: true, value: undefined }); });
  const receive = (data: any) => handler?.({ data } as MessageEvent);
  it('forwards native Close requests without treating them as completed commands', () => {
    const listener = vi.fn(); bridge.subscribe(listener); receive({ type: 'closeRequested' });
    expect(listener).toHaveBeenCalledWith({ type: 'closeRequested' });
  });
  it('correlates out-of-order command responses and sends the versioned command schema', async () => {
    const first = bridge.command('controller.bindingSave', { control: 'x', script: 'rhythm' });
    const second = bridge.command('voice.stop');
    expect(sent[0]).toMatchObject({ protocol: 1, method: 'controller.bindingSave', params: { control: 'x', script: 'rhythm' } });
    receive({ type: 'response', id: sent[1].id, ok: true, result: 'stopped' });
    receive({ type: 'response', id: sent[0].id, ok: true, result: { saved: true } });
    await expect(first).resolves.toEqual({ saved: true }); await expect(second).resolves.toBe('stopped');
  });
  it('surfaces the native error and ignores unknown response ids', async () => {
    const pending = bridge.command('controller.profileDelete', { id: 'one' });
    const rejection = expect(pending).rejects.toThrow('Keep at least one setup');
    receive({ type: 'response', id: 'unknown', ok: true });
    receive({ type: 'response', id: sent[0].id, ok: false, error: 'Keep at least one setup' });
    await rejection;
  });
  it('separates status/snapshots from command responses and unsubscribes listeners', () => {
    const listener = vi.fn(), unsubscribe = bridge.subscribe(listener);
    receive({ type: 'status', status: { controller: { outputActive: false } } });
    receive({ type: 'snapshot', state: { version: 'beta' } });
    expect(listener).toHaveBeenCalledTimes(2);
    unsubscribe(); receive({ type: 'status', status: {} }); expect(listener).toHaveBeenCalledTimes(2);
  });
  it('expires a missing response instead of leaving controls pending indefinitely', async () => {
    vi.useFakeTimers();
    const pending = bridge.command('app.bootstrap'), rejection = expect(pending).rejects.toThrow('taking too long');
    await vi.advanceTimersByTimeAsync(180_001); await rejection;
  });
  it('rejects pending commands when the window closes', async () => {
    const pending = bridge.command('vision.start'), rejection = expect(pending).rejects.toThrow('window closed');
    bridge.dispose(); await rejection;
  });
});
