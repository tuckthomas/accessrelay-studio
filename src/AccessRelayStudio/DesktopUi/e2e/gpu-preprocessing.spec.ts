import { expect, test } from '@playwright/test';
import { selectOption } from './select-control';

test('GPU preparation is an explicit draft, keeps release disabled and respects CPU compatibility', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(async () => {
    const api = await import('/src/api.ts' as string);
    api.previewState.vision.selectedPluginId = 'nba2k27';
    api.previewState.vision.settings = { ...api.previewState.vision.settings, executionProvider: 'cuda', preprocessingBackend: 'cpu' };
    api.previewState.vision.status = { ...api.previewState.vision.status, session: { running: false, canArm: true } };
    await api.desktop.command('app.page', { page: 'workspace' });
  });
  await page.getByRole('button', { name: 'Live Gameplay', exact: true }).click();
  await page.getByText('Analysis settings & measured calibration', { exact: true }).click();
  const preparation = page.getByRole('combobox', { name: 'Image preparation', exact: true });
  await expect(preparation).toHaveText('CPU · compatible default');
  await selectOption(preparation, 'd3D11Cuda');
  await expect(page.getByText('Experimental GPU preparation is observation-only.', { exact: false })).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Automatic release', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(preparation).toHaveText('GPU · experimental');
  await selectOption(page.getByRole('combobox', { name: 'Inference device', exact: true }), 'cpu');
  await expect(preparation).toHaveText('CPU · compatible default');
  await preparation.click();
  await expect(page.getByRole('option', { name: 'GPU · experimental', exact: true })).toHaveAttribute('aria-disabled', 'true');
  await page.keyboard.press('Escape');
  await expect(preparation).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false);
});
