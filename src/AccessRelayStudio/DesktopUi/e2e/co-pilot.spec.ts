import { test, expect } from '@playwright/test';

test('co-pilot requires different participants, preserves owners and stays off by default', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.controller.devices = [{ id: 'physical-a', name: 'Primary synthetic pad' }, { id: 'physical-b', name: 'Companion synthetic pad' }];
    await desktop.command('app.page', { page: 'workspace' });
  });
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility', exact: true }).click();
  const enabled = page.getByRole('checkbox', { name: 'Enable co-pilot merging', exact: true });
  await expect(enabled).not.toBeChecked();
  await enabled.click();
  const save = page.getByRole('button', { name: 'Save co-pilot preferences', exact: true });
  await expect(save).toBeDisabled();
  await page.getByLabel('Primary controller', { exact: true }).selectOption('physical-a');
  await page.getByLabel('Companion controller', { exact: true }).selectOption('physical-a');
  await expect(save).toBeDisabled();
  await page.getByLabel('Companion controller', { exact: true }).selectOption('physical-b');
  await page.getByLabel('Left stick owner', { exact: true }).selectOption('strongest');
  await page.getByText('Button ownership', { exact: true }).click();
  await page.getByLabel('A button owner', { exact: true }).selectOption('companion');
  await save.click();
  await expect(page.getByText('Co-pilot saved. Release controls before continuing.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Voice', exact: true }).click();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility', exact: true }).click();
  await expect(enabled).toBeChecked();
  await expect(page.getByLabel('Left stick owner', { exact: true })).toHaveValue('strongest');
  await enabled.click(); await save.click();
  await expect(page.getByText('Co-pilot disabled.', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Primary controller', { exact: true })).toHaveValue('physical-a');
});
