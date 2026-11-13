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
  await screen.findByText(/Controller scripts installed. Open Controller/);
  expect(command.mock.calls.some(([action]) => ['outputStart', 'scriptsSet', 'profileCreate'].includes(action))).toBe(false);
});
it('opens an available NBA training workspace without downloading scripts or starting capture', async () => {
  const command = vi.fn(), openTraining = vi.fn().mockResolvedValue(null);
  render(<MarketplacePage state={{ loaded: true, entries: [{ ...entries[1], id: 'nba2k27', name: 'NBA 2K27', installed: false, trainingPluginId: 'nba2k27' }] }}
    command={command} openTraining={openTraining} />);
  fireEvent.click(screen.getByRole('button', { name: 'Open training', exact: true }));
  await waitFor(() => expect(openTraining).toHaveBeenCalledWith('nba2k27'));
  expect(screen.getByRole('button', { name: 'Install controller scripts' })).toBeTruthy();
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
