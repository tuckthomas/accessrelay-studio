import { expect, test } from '@playwright/test';

test('assistant access is explicit and drafts require review before inactive apply and undo', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Assistant access', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Enable assistant access', exact: true })).toBeDisabled();
  await page.evaluate(async () => {
    const api = await import('/src/api.ts' as string);
    api.previewState.settings.assistant = { access: { enabled: false }, bridgeAvailable: true, drafts: [], jobs: [], canUndo: false,
      registration: { inputs: [] }, clientConfiguration: '{"mcpServers":{"accessrelay":{"command":"local fixture"}}}' };
    await api.desktop.command('app.assistantState');
  });
  await page.getByRole('checkbox', { name: 'Allow configuration drafts', exact: true }).check();
  await page.getByRole('button', { name: 'Enable assistant access', exact: true }).click();
  await expect(page.getByText('Access enabled', { exact: true })).toBeVisible();
  await page.evaluate(async () => {
    const api = await import('/src/api.ts' as string);
    api.previewState.settings.assistant.drafts = [{ id: 'fixture-draft', profileName: 'Everyday setup',
      changes: ['Sticky controls: A'], proposed: { stickyControls: ['a'], smoothing: { leftTimeConstantMs: 20, rightTimeConstantMs: 0 } } }];
    await api.desktop.command('app.assistantState');
  });
  await page.getByRole('button', { name: 'Review draft', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Review assistant draft', exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText('Sticky controls: A', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Apply with processing off', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Close dialog', exact: true })).toBeFocused();
  await dialog.getByRole('button', { name: 'Apply with processing off', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Undo last assistant change', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Undo last assistant change', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Undo last assistant change', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Revoke assistant access', exact: true }).click();
  await expect(page.getByText('Access off', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false);
});
