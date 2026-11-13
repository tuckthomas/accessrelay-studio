import { expect, test } from '@playwright/test';

test('hardware alerts remain accessible inside an isolating modal without replaying old messages', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Your workspace', exact: true })).toBeVisible();
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.controller.status = { ...previewState.controller.status, connected: true };
    await desktop.command('app.page', { page: 'workspace' });
  });
  await expect(page.getByRole('status', { name: 'Studio status updates' })).toHaveText('Controller connected.');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('button', { name: 'Create controller profile', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Create controller profile' });
  await expect(dialog.getByRole('status', { name: 'Studio status updates' })).toBeEmpty();
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.voice.status = { ...previewState.voice.status, error: 'Microphone disconnected.' };
    await desktop.command('app.page', { page: 'workspace' });
  });
  await expect(dialog.getByRole('alert', { name: 'Studio errors' })).toHaveText('Microphone disconnected.');
  await expect(page.getByRole('alert', { name: 'Studio errors' })).toHaveCount(1);
  await expect(dialog.getByRole('button', { name: 'Close dialog' })).toBeFocused();
});
