import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
  fireEvent.change(screen.getByLabelText('What should it do?'), { target: { value: 'moving' } });
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
  it('keeps microphone capture visible after leaving the Voice workspace', async () => {
    state.voice.status = { ...state.voice.status, isRunning: true, destination: 'headphones' };
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    expect(screen.getByText('Mic live')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Settings', exact: true }));
    expect(screen.getByText('Mic live')).toBeTruthy();
    expect(harness.command.mock.calls.some(([method]) => method === 'voice.start')).toBe(false);
  });
  it('uses live system readings only in the Workspace resource section', async () => {
    render(<App />); await screen.findByRole('heading', { name: 'Your workspace', exact: true });
    emit({ type: 'status', status: { system: { cpuPercent: 12.5, gpuPercent: null, memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 16 * 1024 ** 3 } } });
    expect(screen.getByText('CPU 12.5% · GPU —')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Controller', exact: true }));
    expect(screen.queryByText('Resource use')).toBeNull();
  });
});
