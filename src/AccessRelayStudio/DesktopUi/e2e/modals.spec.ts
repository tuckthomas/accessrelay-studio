import { expect, test } from '@playwright/test';

test('modal wraps focus, blocks background focus and restores its opener', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  const opener = page.getByRole('button', { name: 'Create controller profile', exact: true });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'Create controller profile' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAccessibleDescription('Give it a name you will recognize when you come back.');
  const first = dialog.getByRole('button', { name: 'Close dialog' });
  const last = dialog.getByRole('button', { name: 'Cancel' });
  await first.focus();
  await page.keyboard.press('Shift+Tab');
  await expect(last).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(first).toBeFocused();
  // Programmatic focus cannot escape the native modal's inert background either.
  await page.locator('.sidebar button[aria-label="Controller"]').evaluate(element => element.focus());
  await expect(first).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).not.toBeVisible();
  await expect(opener).toBeFocused();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Controller', exact: true })).toBeVisible();
});

test('nested native request isolates the lower dialog and returns focus on dismissal', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  const opener = page.getByRole('button', { name: 'Create controller profile', exact: true });
  await opener.click();
  const help = page.getByRole('dialog', { name: 'Create controller profile' });
  await expect(help.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  // Simulate a host Close request while an existing dialog is open.
  await page.evaluate(async () => {
    const listeners = new Set<(event: MessageEvent) => void>();
    window.chrome = { ...window.chrome, webview: {
      addEventListener: (_name, listener) => { listeners.add(listener); },
      removeEventListener: (_name, listener) => { listeners.delete(listener); },
      postMessage: message => { listeners.forEach(listener => listener(new MessageEvent('message', {
        data: { type: 'response', id: message.id, ok: true, result: null }
      }))); }
    } };
    const { desktop } = await import('/src/api.ts');
    await desktop.command('app.page', { page: 'workspace' });
    listeners.forEach(listener => listener(new MessageEvent('message', { data: { type: 'closeRequested' } })));
  });
  const close = page.getByRole('dialog', { name: 'Close Studio and discard unsaved work?' });
  await expect(close).toBeVisible();
  await expect(close.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  await page.locator('dialog').filter({ hasText: 'Create controller profile' }).getByRole('button', { name: 'Cancel', includeHidden: true }).evaluate(element => element.focus());
  await expect(close.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(close).not.toBeVisible();
  await expect(help.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  expect(await page.evaluate(() => document.body.style.overflow)).toBe('hidden');
  await page.keyboard.press('Escape');
  await expect(opener).toBeFocused();
  expect(await page.evaluate(() => document.body.style.overflow)).toBe('');
});
