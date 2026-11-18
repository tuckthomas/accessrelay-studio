// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { VisionPage } from './VisionPage';
import { selectValue } from '../select-test-helper';

afterEach(cleanup);
const state = {
  plugins: [{ id: 'nba2k27', name: 'NBA 2K27' }], selectedPluginId: 'nba2k27',
  sources: [{ id: 'game-window', title: 'A different game' }], selectedSourceId: 'game-window',
  profiles: [], settings: {}, status: {}
};

it('previews any game without game plugin controls, NBA profiles, or training clutter', async () => {
  const command = vi.fn().mockResolvedValue(null);
  render(<VisionPage state={state} status={{}} command={command} />);
  expect(screen.queryByRole('combobox', { name: 'Game plugin' })).toBeNull();
  expect(screen.queryByRole('combobox', { name: 'Shot profile' })).toBeNull();
  expect(screen.queryByText('Shot meter')).toBeNull();
  expect(screen.queryByText('Ready when you are')).toBeNull();
  expect(screen.queryByRole('button', { name: 'Record training clip' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Start capture' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('start', { loadModel: false }));
});

it('chooses Xbox or an open window with the same capture source picker', async () => {
  const command = vi.fn().mockResolvedValue(null);
  const view = render(<VisionPage state={state} status={{}} command={command} />);
  const picker = screen.getByRole('combobox', { name: 'Capture source' });
  expect(screen.getAllByRole('combobox')).toHaveLength(1);
  expect(picker.textContent).toContain('A different game');
  selectValue(picker, 'xbox');
  await waitFor(() => expect(command).toHaveBeenCalledWith('captureMode', { id: 'xbox' }));
  view.rerender(<VisionPage state={state} status={{ xbox: { direct: true } }} command={command} />);
  expect(picker.textContent).toContain('Direct Xbox Remote Play');
  selectValue(picker, 'game-window');
  await waitFor(() => expect(command).toHaveBeenCalledWith('sourceSelect', { id: 'game-window' }));
});

it('locks capture source selection during live capture', () => {
  const command = vi.fn();
  render(<VisionPage state={{ ...state, plugins: [] }} status={{ session: { running: true } }} command={command} />);
  expect((screen.getByRole('combobox', { name: 'Capture source' }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByRole('button', { name: 'Stop capture' })).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});

it('detaches through the host and renders the preview in only one window', async () => {
  const command = vi.fn().mockResolvedValue(null), detach = vi.fn().mockResolvedValue(null), attach = vi.fn().mockResolvedValue(null);
  const status = { session: { running: true }, preview: { url: 'https://media.accessrelay.test/vision/preview?v=1' } };
  const view = render(<VisionPage state={state} status={status} command={command} captureWindow={{ detach, attach }} />);
  expect(screen.getByRole('img', { name: 'Live gameplay capture' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Detach' }));
  await waitFor(() => expect(detach).toHaveBeenCalledOnce());
  view.rerender(<VisionPage state={state} status={status} command={command} captureWindow={{ detach, attach, detached: true }} />);
  expect(screen.queryByRole('img')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Bring back' }));
  await waitFor(() => expect(attach).toHaveBeenCalledOnce());
  expect(command).not.toHaveBeenCalled();
});

it('opens direct Xbox sign-in and allows cancelling a pending code', async () => {
  const command = vi.fn().mockResolvedValue(null);
  const view = render(<VisionPage state={state} status={{ xbox: { direct: true } }} command={command} />);
  expect(screen.queryByRole('combobox', { name: 'Gameplay window' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Sign in to Xbox' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('xboxSignIn', undefined));
  view.rerender(<VisionPage state={state} status={{ busy: true, xbox: { direct: true, signInCode: 'ABCD', verificationPage: 'https://microsoft.com/link' } }} command={command} />);
  expect(screen.getByText('ABCD')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('xboxCancel', undefined));
});

it('shows the connected console and measured stats while capture is independent of output', async () => {
  const command = vi.fn().mockResolvedValue(null);
  render(<VisionPage state={state} status={{ selectedSourceId: 'xhome', xbox: { direct: true, signedIn: true, consoleId: 'a',
    consoles: [{ id: 'a', name: 'Living room', powerState: 'On' }], connected: true,
    statistics: { width: 1920, height: 1080, decodedFps: 59.7, jitterMs: 1.1, decodeMs: 3.4, rttMs: null } } }} command={command} />);
  expect(screen.getByText(/59.7 FPS/).textContent).toContain('ICE RTT —');
  fireEvent.click(screen.getByRole('button', { name: 'Start capture' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('start', { loadModel: false }));
  expect(command).toHaveBeenCalledTimes(1);
});

it('restores the Xbox player during capture and shows whether controller output reaches the Xbox channel', async () => {
  const command = vi.fn().mockResolvedValue(null);
  const xbox = { direct: true, signedIn: true, consoleId: 'a', connected: true };
  const view = render(<VisionPage state={state} status={{ session: { running: true }, xbox }} command={command} />);
  expect(screen.getByText('Xbox controller output is off.')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Show Xbox player' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('xboxShowPlayer', undefined));
  fireEvent.click(screen.getByRole('button', { name: 'Connect controller' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('connectOutput', undefined));
  view.rerender(<VisionPage state={state} status={{ session: { running: true }, xbox: { ...xbox, controllerConnected: true } }} command={command} />);
  expect(screen.getByText('Xbox controller output is on.')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Disconnect controller' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('xboxControllerDisconnect', undefined));
});
