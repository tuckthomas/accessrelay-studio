// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MarketplacePage } from './MarketplacePage';
import { selectValue } from '../select-test-helper';
afterEach(cleanup);
const entries = [
  { id: 'pulse', name: 'Pulse script', description: 'A script', author: 'Example author', version: '1.0.0', kind: 'script', license: '0BSD', tags: ['switch'], installed: false },
  { id: 'switch-kit', name: 'Switch plugin', description: 'A plugin', author: 'Example author', version: '1.0.0', kind: 'plugin', license: '0BSD', tags: ['switch'], installed: true }
];
it('loads the catalog on opening and requests only the chosen package for installation', async () => {
  const command = vi.fn().mockImplementation(async action => ({ loaded: true, entries: entries.map(entry => ({ ...entry, installed: action === 'install' || entry.installed })) }));
  render(<MarketplacePage state={{ loaded: false }} command={command} />);
  await screen.findByRole('heading', { name: 'Pulse script' });
  expect(command).toHaveBeenCalledWith('refresh', undefined);
  fireEvent.click(screen.getByRole('button', { name: 'Install', exact: true }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('install', { id: 'pulse', version: '1.0.0' }));
  await screen.findByText(/Package installed. Open Controller/);
  expect(command.mock.calls.some(([action]) => ['outputStart', 'scriptsSet', 'profileCreate'].includes(action))).toBe(false);
});
it('opens an available NBA training workspace without downloading scripts or starting capture', async () => {
  const command = vi.fn(), openTraining = vi.fn().mockResolvedValue(null);
  render(<MarketplacePage state={{ loaded: true, entries: [{ ...entries[1], id: 'nba2k27-training', name: 'NBA 2K27 Model Training', kind: 'training-plugin', gameId: 'nba2k27', installed: true, trainingPluginId: 'nba2k27' }] }}
    command={command} openTraining={openTraining} />);
  fireEvent.click(screen.getByRole('button', { name: 'Open training', exact: true }));
  await waitFor(() => expect(openTraining).toHaveBeenCalledWith('nba2k27'));
  expect(screen.queryByRole('button', { name: 'Installed' })).toBeNull();
  expect(screen.getByRole('button', { name: /View source/ })).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});
it('filters scripts/plugins using shared controls and keeps already-installed items disabled', () => {
  const command = vi.fn();
  render(<MarketplacePage state={{ loaded: true, entries }} command={command} />);
  selectValue(screen.getByRole('combobox', { name: 'Marketplace content type' }), 'plugin');
  expect(screen.queryByRole('heading', { name: 'Pulse script' })).toBeNull();
  expect((screen.getByRole('button', { name: 'Installed' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'missing' } });
  expect(screen.getByText('No matching packages')).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});

it('requires installing training separately from controller scripts and filters every content type', async () => {
  const command = vi.fn().mockResolvedValue(null), openTraining = vi.fn();
  const training = { ...entries[0], id: 'nba2k27-training', name: 'NBA Training', kind: 'training-plugin', gameId: 'nba2k27' };
  render(<MarketplacePage state={{ loaded: true, entries: [...entries, training,
    { ...entries[0], id: 'profile', name: 'Controller config', kind: 'controller-profile', gameId: 'another-game' }] }} command={command} openTraining={openTraining} />);
  expect(screen.queryByRole('button', { name: 'Open training' })).toBeNull();
  selectValue(screen.getByRole('combobox', { name: 'Marketplace content type' }), 'training-plugin');
  expect(screen.getByRole('heading', { name: 'NBA Training' })).toBeTruthy();
  expect(screen.queryByRole('heading', { name: 'Pulse script' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Install', exact: true }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('install', { id: training.id, version: training.version }));
  expect(openTraining).not.toHaveBeenCalled();
  selectValue(screen.getByRole('combobox', { name: 'Marketplace content type' }), 'controller-profile');
  expect(screen.getByRole('heading', { name: 'Controller config' })).toBeTruthy();
  selectValue(screen.getByRole('combobox', { name: 'Marketplace game' }), 'nba2k27');
  expect(screen.queryByRole('heading', { name: 'Controller config' })).toBeNull();
});
