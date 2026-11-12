import { expect, test, type Locator, type Page } from '@playwright/test';

async function openLibrary(page: Page) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Your workspace', exact: true })).toBeVisible();
  // Synthetic browser-preview data only; no native connection or user profiles.
  await page.evaluate(async () => {
    const { previewState, desktop } = await import('/src/api.ts');
    previewState.voice.profiles = Array.from({ length: 8 }, (_, index) => ({
      id: `voice-${index}`, name: `Voice ${index + 1}`, kind: 'reference', durationMs: 15000,
    }));
    previewState.controller.activeProfile.recordings = Array.from({ length: 14 }, (_, index) => ({
      id: `sequence-${index}`, name: `Sequence ${index + 1}`, durationMs: 500, frameCount: 12,
    }));
    await desktop.command('app.page', { page: 'workspace' });
  });
  await page.getByRole('button', { name: 'Voice', exact: true }).click();
  await page.getByRole('tab', { name: /^Your voices/ }).click();
}

async function expectInside(menu: Locator, boundary: Locator) {
  await expect(menu).toBeVisible();
  await expect.poll(async () => {
    const box = await menu.boundingBox(), view = await boundary.boundingBox();
    return !!box && !!view && box.x >= view.x + 9 && box.y >= view.y + 9
      && box.x + box.width <= view.x + view.width - 9 && box.y + box.height <= view.y + view.height - 9;
  }).toBe(true);
}

test('voice actions flip above the bottom row without extending or clipping the scroll area', async ({ page }) => {
  await openLibrary(page);
  const main = page.locator('.app-main');
  await main.evaluate(element => { element.scrollTop = element.scrollHeight; });
  const trigger = page.getByRole('button', { name: 'More actions for Voice 8', exact: true });
  const scrollHeight = await main.evaluate(element => element.scrollHeight);
  await trigger.click();
  const menu = page.getByRole('menu', { name: 'More actions for Voice 8', exact: true });
  await expect(menu).toHaveAttribute('data-side', 'top');
  await expectInside(menu, main);
  await expect(menu.getByRole('menuitem', { name: 'Delete', exact: true })).toBeInViewport();
  expect(await main.evaluate(element => element.scrollHeight)).toBe(scrollHeight);

  // Reposition while open, then constrain a menu taller than the remaining space.
  for (const height of [600, 300]) {
    await page.setViewportSize({ width: 1100, height });
    await main.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expectInside(menu, main);
  }
  await menu.press('End');
  await expect(menu.getByRole('menuitem', { name: 'Delete', exact: true })).toBeFocused();
  await expect(menu.getByRole('menuitem', { name: 'Delete', exact: true })).toBeInViewport();
  await menu.press('Escape');
  await expect(menu).not.toBeVisible();
  await expect(trigger).toBeFocused();
});

test('voice actions open below when space allows, dismiss outside and support keyboard selection', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1200 });
  await openLibrary(page);
  const trigger = page.getByRole('button', { name: 'More actions for Voice 1', exact: true });
  await trigger.focus();
  await trigger.press('ArrowDown');
  const menu = page.getByRole('menu', { name: 'More actions for Voice 1', exact: true });
  await expect(menu).toHaveAttribute('data-side', 'bottom');
  await expectInside(menu, page.locator('.app-main'));
  await expect(menu.getByRole('menuitem', { name: 'Rename', exact: true })).toBeFocused();
  await page.getByRole('heading', { name: 'A collection that sounds like you.' }).click();
  await expect(menu).not.toBeVisible();
  await trigger.press('ArrowDown');
  await page.getByRole('menuitem', { name: 'Rename', exact: true }).press('Enter');
  await expect(menu).not.toBeVisible();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByRole('dialog').locator(':focus')).toHaveCount(1);
  await page.getByRole('dialog').getByRole('button', { name: 'Close dialog' }).click();
  await expect(trigger).toBeFocused();
});

test('saved sequence actions use the same adaptive menu inside the scrolling list', async ({ page }) => {
  await openLibrary(page);
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: /^Sequences/ }).click();
  const trigger = page.getByRole('button', { name: 'More actions for Sequence 14', exact: true });
  await trigger.scrollIntoViewIfNeeded();
  await trigger.click();
  const menu = page.getByRole('menu', { name: 'More actions for Sequence 14', exact: true });
  await expectInside(menu, page.locator('.app-main'));
  await menu.getByRole('menuitem', { name: 'Duplicate', exact: true }).click();
  await expect(menu).not.toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Make another version' })).toBeVisible();
});
