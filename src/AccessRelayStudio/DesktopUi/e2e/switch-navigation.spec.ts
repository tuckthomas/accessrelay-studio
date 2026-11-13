import { selectOption } from './select-control';
import { expect, test, type Page } from '@playwright/test';

async function configure(page: Page, mode = 'step') {
  await page.addInitScript(config => localStorage.setItem('accessrelay-switch-navigation-v1', JSON.stringify(config)), {
    mode, source: 'keyboard', intervalMs: 300, debounceMs: 50, stepKey: 'Enter', activateKey: 'Space', step: 'dPadRight', activate: 'a'
  });
  await page.clock.install();
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Your workspace', exact: true })).toBeVisible();
}
async function press(page: Page, key: string) { await page.clock.fastForward(60); await page.keyboard.press(key); }
async function stepTo(page: Page, label: string) {
  for (let count = 0; count < 100; count++) {
    if (await page.locator('.scan-target').evaluateAll((elements, expected) => elements.some(element =>
      element.getAttribute('aria-label') === expected || element.textContent?.trim() === expected), label)) return;
    await press(page, 'Enter');
  }
  throw new Error('Scanner never reached ' + label);
}

test('two-switch navigation opens pages, scopes to a modal, and exits without activating the focused control', async ({ page }) => {
  await configure(page);
  await press(page, 'F8');
  await expect(page.locator('.scan-target')).toHaveCount(1);
  await stepTo(page, 'Controller'); await press(page, 'Space');
  await expect(page.getByRole('heading', { name: 'Controller' })).toBeVisible();
  await stepTo(page, 'Create controller profile'); await press(page, 'Space');
  const dialog = page.getByRole('dialog', { name: 'Create controller profile' });
  await expect(dialog.locator('.scan-target')).toHaveCount(1);
  for (let count = 0; count < 5; count++) {
    await press(page, 'Enter'); await expect(dialog.locator('.scan-target')).toHaveCount(1);
  }
  await press(page, 'Escape');
  await expect(page.locator('.scan-target')).toHaveCount(0);
  await expect(dialog).toBeVisible();
  await page.reload();
  await expect(page.locator('.scan-target')).toHaveCount(0);
});

test('switch scanning edits a slider and a choice without native popup or mouse input', async ({ page }) => {
  await configure(page);
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.controller.profiles.push({ id: 'second-preview-setup', name: 'Second setup' });
    await desktop.command('app.page', { page: 'workspace' });
  });
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await press(page, 'F8');
  const range = page.getByLabel('Left stick smoothing', { exact: true });
  for (let count = 0; count < 100 && !(await range.evaluate(element => element.classList.contains('scan-target'))); count++) await press(page, 'Enter');
  await expect(range).toHaveClass(/scan-target/); await press(page, 'Space');
  await expect(page.locator('[data-scan-editor]')).toBeVisible();
  await stepTo(page, 'Increase / next'); await press(page, 'Space');
  await expect(range).toHaveValue('1');
  await stepTo(page, 'Finish editing'); await press(page, 'Space');
  await expect(page.locator('[data-scan-editor]')).toHaveCount(0);
  await stepTo(page, 'Save smoothing'); await press(page, 'Space');
  await expect(page.getByText('Stick smoothing saved.', { exact: true })).toBeVisible();
  const choice = page.getByRole('combobox', { name: 'Controller profile', exact: true });
  for (let count = 0; count < 100 && !(await choice.evaluate(element => element.classList.contains('scan-target'))); count++) await press(page, 'Enter');
  await expect(choice).toHaveClass(/scan-target/); await press(page, 'Space');
  await stepTo(page, 'Increase / next'); await press(page, 'Space');
  await expect(choice).toHaveAttribute('value', 'second-preview-setup');
  // Profile selection temporarily disables the picker while native state changes;
  // that ends editing of the old context while the scanning ring remains usable.
  await expect(page.locator('[data-scan-editor]')).toHaveCount(0);
  await stepTo(page, 'Stop scanning'); await press(page, 'Space');
  await expect(page.locator('.scan-target')).toHaveCount(0);
});

test('automatic scanning advances and stops on focus loss; held switches do not repeat', async ({ page }) => {
  await configure(page, 'automatic');
  await press(page, 'F8');
  await expect(page.locator('.scan-target')).toHaveCount(1);
  const first = await page.locator('.scan-target').getAttribute('aria-label');
  await page.clock.fastForward(310);
  await expect.poll(() => page.locator('.scan-target').getAttribute('aria-label')).not.toBe(first);
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(page.locator('.scan-target')).toHaveCount(0);
  await expect(page.getByText('Switch navigation stopped because Studio lost focus.')).toBeAttached();
});

test('dynamic disabled controls are skipped and keyboard repeat cannot advance repeatedly', async ({ page }) => {
  await configure(page);
  await press(page, 'F8');
  await expect(page.locator('.scan-target')).toHaveCount(1);
  await page.locator('.scan-target').evaluate(element => { (element as HTMLButtonElement).disabled = true; });
  await expect(page.locator('.scan-target')).not.toBeDisabled();
  await press(page, 'Enter');
  const target = await page.locator('.scan-target').getAttribute('aria-label');
  await page.evaluate(() => window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Enter', repeat: true, bubbles: true })));
  expect(await page.locator('.scan-target').getAttribute('aria-label')).toBe(target);
  await stepTo(page, 'Stop scanning'); await press(page, 'Space');
  await expect(page.locator('.scan-target')).toHaveCount(0);
});

test('controller bridge preserves event order, ignores pre-activation snapshots and stops on lost history', async ({ page }) => {
  await configure(page);
  // Synthetic bridge only: the Windows suite separately exercises the actual
  // runtime's physical-edge routing and neutral gameplay output.
  await page.evaluate(async () => {
    const { desktop, previewState } = await import('/src/api.ts');
    const listeners = new Set<(event: MessageEvent) => void>();
    let generation = 0;
    (window as any).navigationRequests = [];
    window.chrome = { ...window.chrome, webview: {
      addEventListener: (_name, listener) => { listeners.add(listener); },
      removeEventListener: (_name, listener) => { listeners.delete(listener); },
      postMessage: message => {
        let result = null;
        if (message.method === 'controller.navigationSet') {
          (window as any).navigationRequests.push(message.params);
          result = { enabled: message.params.enabled, generation: ++generation, nowMs: 100, events: [] };
        }
        listeners.forEach(listener => listener(new MessageEvent('message', { data: { type: 'response', id: message.id, ok: true, result } })));
      }
    } };
    await desktop.command('app.page', { page: 'workspace' });
    (window as any).navigationSend = (navigation: any) => listeners.forEach(listener => listener(new MessageEvent('message', {
      data: { type: 'status', status: { controller: { ...previewState.controller.status, connected: true, navigation } } }
    })));
    (window as any).navigationSend({ enabled: false, generation: 0, nowMs: 0, events: [] });
  });
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Switch navigation', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Switch navigation' });
  await selectOption(dialog.getByLabel('Switch source'), 'controller');
  await dialog.getByRole('button', { name: 'Start scanning' }).click();
  await expect(page.locator('.scan-target')).toHaveCount(1);
  const step = { sequence: 1, timestampMs: 105, action: 'step' };
  await page.evaluate(event => (window as any).navigationSend({ enabled: true, generation: 1, nowMs: 110, events: [event] }), step);
  await expect(page.getByRole('button', { name: 'Controller', exact: true })).toHaveClass(/scan-target/);
  await page.evaluate(event => (window as any).navigationSend({ enabled: true, generation: 1, nowMs: 115, events: [event] }), step);
  await expect(page.getByRole('button', { name: 'Controller', exact: true })).toHaveClass(/scan-target/);
  await page.evaluate(() => (window as any).navigationSend({ enabled: false, generation: 0, nowMs: 0, events: [] }));
  await expect(page.locator('.scan-target')).toHaveCount(1);
  await page.evaluate(event => (window as any).navigationSend({ enabled: true, generation: 1, nowMs: 140,
    events: [event, { sequence: 2, timestampMs: 135, action: 'activate' }] }), step);
  await expect(page.getByRole('heading', { name: 'Controller' })).toBeVisible();
  await page.evaluate(() => (window as any).navigationSend({ enabled: true, generation: 1, nowMs: 180,
    events: [{ sequence: 4, timestampMs: 175, action: 'activate' }] }));
  await expect(page.locator('.scan-target')).toHaveCount(0);
  await expect(page.getByText('Navigation input was delayed or lost. Restart switch navigation.')).toBeVisible();
  expect(await page.evaluate(() => (window as any).navigationRequests.at(-1).enabled)).toBe(false);
});
