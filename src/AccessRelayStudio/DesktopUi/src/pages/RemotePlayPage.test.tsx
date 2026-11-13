import { selectValue } from '../select-test-helper';
// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { VisionPage } from './VisionPage';

afterEach(cleanup);
const state = {
  plugins: [{ id: 'nba2k27', name: 'NBA 2K27' }], selectedPluginId: '',
  sources: [{ id: 'game-window', title: 'A different game' }], selectedSourceId: 'game-window',
  profiles: [], settings: {}, status: {}
};

it('previews any game without an NBA profile, training controls, or an idle tag', async () => {
  const command = vi.fn().mockResolvedValue(null);
  render(<VisionPage state={state} status={{}} command={command} />);
  expect(screen.queryByRole('combobox', { name: 'Shot profile' })).toBeNull();
  expect(screen.queryByText('Shot meter')).toBeNull();
  expect(screen.queryByText('Ready when you are')).toBeNull();
  expect(screen.queryByRole('button', { name: 'Record training clip' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Start capture' }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('start', { loadModel: false }));
});

it('shows NBA configuration only after the host selects the NBA plugin', async () => {
  const command = vi.fn().mockResolvedValue(null);
  const view = render(<VisionPage state={state} status={{}} command={command} />);
  selectValue(screen.getByRole('combobox', { name: 'Game plugin' }), 'nba2k27');
  await waitFor(() => expect(command).toHaveBeenCalledWith('pluginSelect', { id: 'nba2k27' }));
  expect(screen.queryByRole('combobox', { name: 'Shot profile' })).toBeNull();
  view.rerender(<VisionPage state={{ ...state, selectedPluginId: 'nba2k27' }} status={{}} command={command} />);
  expect(screen.getByRole('combobox', { name: 'Shot profile' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Record training clip' })).toBeNull();
  expect(screen.getByRole('heading', { name: 'Live analysis' })).toBeTruthy();
  expect(screen.queryByText('Ready when you are')).toBeNull();
});

it('does not require an installed game plugin and locks selection during capture', () => {
  const command = vi.fn();
  render(<VisionPage state={{ ...state, plugins: [] }} status={{ session: { running: true } }} command={command} />);
  expect((screen.getByRole('combobox', { name: 'Game plugin' }) as HTMLSelectElement).disabled).toBe(true);
  expect(screen.getByRole('button', { name: 'Stop capture' })).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});

it('keeps training and assistance on separate plugin surfaces', () => {
  const command = vi.fn().mockResolvedValue(null);
  const view = render(<VisionPage state={{ ...state, selectedPluginId: 'nba2k27' }} status={{}} command={command} mode="training" />);
  expect(screen.getByRole('heading', { name: 'AI Model Training' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Record training clip' })).toBeTruthy();
  expect(screen.queryByRole('heading', { name: 'Live analysis' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Detach' })).toBeNull();
  view.rerender(<VisionPage state={{ ...state, selectedPluginId: 'nba2k27' }} status={{}} command={command} mode="live" />);
  expect(screen.getByRole('heading', { name: 'Live Gameplay' })).toBeTruthy();
  expect(screen.queryByRole('tab', { name: 'Record clips' })).toBeNull();
  expect(screen.getByRole('heading', { name: 'Live analysis' })).toBeTruthy();
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
