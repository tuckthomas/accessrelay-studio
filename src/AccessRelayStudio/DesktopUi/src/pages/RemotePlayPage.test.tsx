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
  fireEvent.click(screen.getByRole('button', { name: 'Start preview' }));
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
  expect(screen.getByRole('button', { name: 'Record training clip' })).toBeTruthy();
  expect(screen.queryByText('Ready when you are')).toBeNull();
});

it('does not require an installed game plugin and locks selection during capture', () => {
  const command = vi.fn();
  render(<VisionPage state={{ ...state, plugins: [] }} status={{ session: { running: true } }} command={command} />);
  expect((screen.getByRole('combobox', { name: 'Game plugin' }) as HTMLSelectElement).disabled).toBe(true);
  expect(screen.getByRole('button', { name: 'Stop preview' })).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});
