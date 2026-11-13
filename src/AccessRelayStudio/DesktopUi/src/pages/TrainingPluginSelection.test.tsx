// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { VisionPage } from './VisionPage';
afterEach(cleanup);

it('keeps NBA controls absent until the training plugin is installed and selected', () => {
  const command = vi.fn(), openMarketplace = vi.fn();
  const view = render(<VisionPage mode="training" state={{ plugins: [], selectedPluginId: 'nba2k27' }} command={command} openMarketplace={openMarketplace} />);
  expect(screen.getByText('No training plugins installed')).toBeTruthy();
  expect(screen.queryByRole('combobox', { name: 'Shot profile' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Browse Marketplace' }));
  expect(openMarketplace).toHaveBeenCalledOnce();
  expect(command).not.toHaveBeenCalled();
  view.rerender(<VisionPage mode="training" state={{ plugins: [{ id: 'nba2k27', name: 'NBA 2K27' }], selectedPluginId: 'nba2k27' }} command={command} />);
  expect(screen.getByRole('combobox', { name: 'Shot profile' })).toBeTruthy();
});
