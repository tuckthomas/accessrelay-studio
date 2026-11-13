import { selectOption } from './select-control';
import { expect, test } from '@playwright/test';

test('pack tuning renders a declared schema, creates a separate setup and pauses execution', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.controller.gamePacks = [{ key: 'example@1', manifestSha256: 'a'.repeat(64), models: [], manifest: {
      id: 'example', version: '1', name: 'Synthetic switch example', description: 'Synthetic preview only.', license: '0BSD', assets: [],
      tuning: [{ id: 'press-ms', label: 'Press duration', kind: 'range', minimum: 20, maximum: 500, step: 10, default: 100, choices: [] }],
      presets: [{ name: 'Slow press', values: { 'press-ms': 300 } }],
    } }];
    await desktop.command('app.page', { page: 'workspace' });
  });
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Game packs', exact: true }).click();
  await selectOption(page.getByLabel('Tuning preset', { exact: true }), '0');
  await expect(page.getByLabel('Press duration', { exact: true })).toHaveValue('300');
  await page.getByRole('tab', { name: 'Button mapping', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await page.getByRole('button', { name: 'Create setup from pack', exact: true }).click();
  await expect(page.getByLabel('Controller profile', { exact: true })).toContainText('Synthetic switch example');
  await expect(page.getByText('This controller profile is linked to this pack and version.', { exact: false })).toBeVisible();
  await page.getByLabel('Press duration', { exact: true }).fill('200');
  await page.getByRole('button', { name: 'Save pack tuning', exact: true }).click();
  await expect(page.getByText('Game pack updated.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save pack tuning', exact: true })).toBeDisabled();
  await page.getByRole('tab', { name: 'Button mapping', exact: true }).click();
  await page.getByRole('tab', { name: 'Game packs', exact: true }).click();
  await expect(page.getByLabel('Press duration', { exact: true })).toHaveValue('200');
});
