import { selectValue } from './select-test-helper';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const harness = vi.hoisted(() => ({ listeners: new Set<(message: any) => void>(), command: vi.fn() }));
vi.mock('./api', async importOriginal => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, isDesignPreview: true, desktop: { command: harness.command, subscribe: (listener: any) => {
    harness.listeners.add(listener); return () => harness.listeners.delete(listener);
  } } };
});
import { App } from './App';
import { previewState } from './api';
let state: any;
beforeEach(() => {
  state = structuredClone(previewState);
  harness.listeners.clear(); harness.command.mockReset();
  harness.command.mockImplementation(async (method: string) => method === 'app.bootstrap' ? structuredClone(state) : null);
});
afterEach(cleanup);
const emit = (message: any) => act(() => { harness.listeners.forEach(listener => listener(message)); });
async function openMappingDraft() {
  render(<App />);
  await screen.findByRole('heading', { name: 'Your workspace', exact: true });
  fireEvent.click(screen.getByRole('button', { name: 'Controller', exact: true }));
  selectValue(screen.getByLabelText('What should it do?'), 'moving');
}
describe('workspace draft and capture visibility', () => {
  it('keeps an unsaved mapping through status updates and a cancelled navigation', async () => {
    await openMappingDraft();
    emit({ type: 'status', status: { controller: { ...state.controller.status, meanPollMs: 4.2 } } });
    fireEvent.click(screen.getByRole('button', { name: 'Voice', exact: true }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('moving');
    expect(harness.command).toHaveBeenCalledWith('app.draftState', { dirty: true });
  });
  it('changes pages only after the user explicitly discards a draft', async () => {
    await openMappingDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Settings', exact: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(await screen.findByRole('heading', { name: 'Studio settings' })).toBeTruthy();
    await waitFor(() => expect(harness.command).toHaveBeenCalledWith('app.draftState', { dirty: false }));
  });
  it('handles a native Close request without discarding edits until requested', async () => {
    await openMappingDraft();
    emit({ type: 'closeRequested' });
    expect(screen.getByRole('dialog', { name: 'Close Studio and discard unsaved work?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('moving');
    expect(harness.command.mock.calls.some(([method]) => method === 'app.close')).toBe(false);
    let acknowledgeClose!: (value: null) => void;
    const closeAcknowledgement = new Promise<null>(resolve => { acknowledgeClose = resolve; });
    harness.command.mockImplementation((method: string) => method === 'app.close' ? closeAcknowledgement : Promise.resolve(null));
    emit({ type: 'closeRequested' });
    fireEvent.click(screen.getByRole('button', { name: 'Close and discard' }));
    await waitFor(() => expect(harness.command).toHaveBeenCalledWith('app.close', { discard: true }));
    await act(async () => { acknowledgeClose(null); await closeAcknowledgement; });
    expect((screen.getByRole('button', { name: 'Keep editing' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Close and discard' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('keeps runtime badges out of the navbar while voice is running', async () => {
    state.voice.status = { ...state.voice.status, isRunning: true, destination: 'headphones' };
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    expect(screen.queryByText('Mic live')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Settings', exact: true }));
    expect(screen.queryByText('Mic live')).toBeNull();
    expect(harness.command.mock.calls.some(([method]) => method === 'voice.start')).toBe(false);
  });
  it('uses live system readings only in the Workspace resource section', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    emit({ type: 'status', status: { system: { cpuPercent: 12.5, gpuPercent: null, memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 16 * 1024 ** 3 } } });
    expect(screen.getByText('12.5%')).toBeTruthy();
    expect(screen.queryByText('All quiet')).toBeNull();
    expect(document.querySelector('.local-indicator')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Controller', exact: true }));
    expect(screen.queryByText('Resource use')).toBeNull();
  });
});

describe('semantic hardware and model announcements', () => {
  const updates = () => screen.getByRole('status', { name: 'Studio status updates' });
  const errors = () => screen.getByRole('alert', { name: 'Studio errors' });
  it('announces command lifecycle/faults without adding a navbar badge', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    emit({ type: 'status', status: { voice: { commands: { recognition: { running: true, ready: true }, output: { enabled: true } } } } });
    await waitFor(() => expect(updates().textContent).toBe('Local voice commands are listening.'));
    expect(screen.queryByText('Mic live')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Settings', exact: true }));
    expect(screen.queryByText('Mic live')).toBeNull();
    emit({ type: 'status', status: { voice: { commands: { recognition: { running: false, error: 'Local recognizer fell behind.' }, output: { enabled: false } } } } });
    await waitFor(() => expect(errors().textContent).toBe('Local recognizer fell behind.'));
    expect(screen.queryByText('Mic live')).toBeNull();
  });
  it('announces feedback device failures without reading cue statistics', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    emit({ type: 'status', status: { controller: { feedback: { error: 'Audio cue unavailable: endpoint closed.', played: 0, dropped: 0 } } } });
    await waitFor(() => expect(errors().textContent).toBe('Audio cue unavailable: endpoint closed.'));
    emit({ type: 'status', status: { controller: { feedback: { error: 'Audio cue unavailable: endpoint closed.', played: 10, dropped: 2 } } } });
    expect(errors().textContent).toBe('Audio cue unavailable: endpoint closed.');
  });
  it('announces an existing failure on bootstrap without reading ordinary initial state', async () => {
    state.voice.status.error = 'The microphone is unavailable.';
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    await waitFor(() => expect(errors().textContent).toBe('The microphone is unavailable.'));
    expect(updates().textContent).toBe('');
  });
  it('starts quietly and announces transitions without repeating telemetry snapshots', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    expect(updates().textContent).toBe('');
    emit({ type: 'status', status: { controller: { connected: true }, voice: { isStarting: true } } });
    await waitFor(() => expect(updates().textContent).toBe('Controller connected. Starting voice processing.'));
    const observerCallback = vi.fn();
    const observer = new MutationObserver(observerCallback);
    observer.observe(updates(), { childList: true, characterData: true, subtree: true });
    try {
      for (const meanPollMs of [2, 4, 6]) emit({ type: 'status', status: {
        controller: { connected: true, meanPollMs }, voice: { isStarting: true, inputPeak: meanPollMs / 10 }
      } });
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 80)); });
      expect(observerCallback).not.toHaveBeenCalled();
    } finally { observer.disconnect(); }
    emit({ type: 'status', status: { controller: { connected: false }, voice: { isRunning: true } } });
    await waitFor(() => expect(updates().textContent).toBe('Controller disconnected. Voice processing is live.'));
  });
  it('announces asynchronous voice and capture failures once, independently of the visible page', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    fireEvent.click(screen.getByRole('button', { name: 'Settings', exact: true }));
    emit({ type: 'status', status: { voice: { error: 'Microphone disconnected.' }, vision: {
      session: { captureFailed: true, message: 'Capture window closed.' }
    } } });
    await waitFor(() => expect(errors().textContent).toBe('Microphone disconnected. Capture window closed.'));
    emit({ type: 'status', status: { voice: { error: 'Microphone disconnected.', inputPeak: 0 }, vision: {
      session: { captureFailed: true, message: 'Capture window closed.', frames: 99 }
    } } });
    expect(errors().textContent).toBe('Microphone disconnected. Capture window closed.');
  });
  it('announces readiness and measured calibration notices without reading per-frame decisions', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    emit({ type: 'status', status: { vision: { session: { running: true, modelReady: true },
      notice: 'Measured calibration is ready for your review.' } } });
    await waitFor(() => expect(updates().textContent).toContain('Vision model ready.'));
    expect(updates().textContent).toContain('Measured calibration is ready for your review.');
    emit({ type: 'status', status: { vision: { session: { running: true, modelReady: true, frames: 100 },
      output: { state: 'trackingShot', lastConfidence: .95 }, notice: 'Measured calibration is ready for your review.' } } });
    expect(updates().textContent).not.toContain('trackingShot');
  });
  it('delivers hardware errors inside the active profile dialog without duplicate visible alerts', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    fireEvent.click(screen.getByRole('button', { name: 'Controller', exact: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Create controller profile' }));
    const dialog = screen.getByRole('dialog', { name: 'Create controller profile' });
    emit({ type: 'status', status: { voice: { error: 'Microphone disconnected.' } } });
    await waitFor(() => expect(within(dialog).getByRole('alert', { name: 'Studio errors' }).textContent).toBe('Microphone disconnected.'));
    expect(screen.getAllByRole('alert').every(element => element.hasAttribute('data-status-announcer') || element.closest('[data-status-announcer]'))).toBe(true);
  });
});
