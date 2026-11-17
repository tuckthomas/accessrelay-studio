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

it('loads Add-Ons on opening and requests only the chosen package for installation', async () => {
  const command = vi.fn().mockImplementation(async action => ({ loaded: true, entries: entries.map(entry => ({ ...entry, installed: action === 'install' || entry.installed })) }));
  render(<MarketplacePage state={{ loaded: false }} command={command} />);
  await screen.findByRole('heading', { name: 'Pulse script' });
  expect(command).toHaveBeenCalledWith('refresh', undefined);
  fireEvent.click(screen.getByRole('button', { name: 'Install', exact: true }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('install', { id: 'pulse', version: '1.0.0' }));
  await screen.findByText(/Package installed. Open Controller/);
  expect(command.mock.calls.some(([action]) => ['outputStart', 'scriptsSet', 'profileCreate'].includes(action))).toBe(false);
});

it('groups game add-ons onto a game card with cover art provided by the add-on and opens the bespoke NBA 2K27 page when installed', async () => {
  const command = vi.fn(), openGameAddon = vi.fn().mockResolvedValue(null), openTraining = vi.fn().mockResolvedValue(null);
  const coverUrl = 'https://media.accessrelay.test/marketplace/cover/nba-hash';
  render(<MarketplacePage state={{
    loaded: true,
    entries: [{ ...entries[1], id: 'nba2k27', name: 'NBA 2K27', kind: 'training-plugin', gameId: 'nba2k27', installed: true, trainingPluginId: 'nba2k27', coverUrl }]
  }} command={command} openGameAddon={openGameAddon} openTraining={openTraining} />);

  const cover = screen.getByRole('img', { name: 'NBA 2K27 cover art' }) as HTMLImageElement;
  expect(cover.getAttribute('src')).toBe(coverUrl);
  fireEvent.click(screen.getByRole('button', { name: 'Open NBA 2K27', exact: true }));
  await waitFor(() => expect(openGameAddon).toHaveBeenCalledWith({ gameId: 'nba2k27', pluginId: 'nba2k27', title: 'NBA 2K27', coverUrl }));

  fireEvent.click(screen.getByRole('button', { name: 'Open training', exact: true }));
  await waitFor(() => expect(openTraining).toHaveBeenCalledWith('nba2k27'));
  expect(screen.getByRole('button', { name: /Source/ })).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});

it('filters scripts/plugins using shared controls and keeps already-installed items disabled', () => {
  const command = vi.fn();
  render(<MarketplacePage state={{ loaded: true, entries }} command={command} />);
  selectValue(screen.getByRole('combobox', { name: 'Add-Ons content type' }), 'plugin');
  expect(screen.queryByRole('heading', { name: 'Pulse script' })).toBeNull();
  expect((screen.getByRole('button', { name: 'Installed' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'missing' } });
  expect(screen.getByText('No matching add-ons')).toBeTruthy();
  expect(command).not.toHaveBeenCalled();
});

it('requires explicit installation before opening a game add-on and filters every content type', async () => {
  const command = vi.fn().mockResolvedValue(null), openGameAddon = vi.fn(), openTraining = vi.fn();
  const training = { ...entries[0], id: 'nba2k27-training', name: 'NBA Training', kind: 'training-plugin', gameId: 'nba2k27', installed: false };
  render(<MarketplacePage state={{ loaded: true, entries: [...entries, training,
    { ...entries[0], id: 'profile', name: 'Controller config', kind: 'controller-profile', gameId: 'another-game', installed: false }] }}
    command={command} openGameAddon={openGameAddon} openTraining={openTraining} />);

  expect(screen.queryByRole('button', { name: 'Open NBA 2K27' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Open training' })).toBeNull();

  selectValue(screen.getByRole('combobox', { name: 'Add-Ons content type' }), 'training-plugin');
  expect(screen.getByRole('heading', { name: 'NBA Training' })).toBeTruthy();
  expect(screen.queryByRole('heading', { name: 'Pulse script' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Install', exact: true }));
  await waitFor(() => expect(command).toHaveBeenCalledWith('install', { id: training.id, version: training.version }));
  expect(openGameAddon).not.toHaveBeenCalled();
  expect(openTraining).not.toHaveBeenCalled();

  selectValue(screen.getByRole('combobox', { name: 'Add-Ons content type' }), 'controller-profile');
  expect(screen.getByRole('heading', { name: 'Controller config' })).toBeTruthy();
  selectValue(screen.getByRole('combobox', { name: 'Add-Ons game' }), 'nba2k27');
  expect(screen.queryByRole('heading', { name: 'Controller config' })).toBeNull();
});
