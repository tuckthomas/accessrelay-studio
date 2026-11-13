import { expect, test } from '@playwright/test';

test('shared profile control aligns its create button and dropdown flips within the viewport', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  const picker = page.getByRole('combobox', { name: 'Controller profile', exact: true });
  const create = page.getByRole('button', { name: 'Create controller profile', exact: true });
  const [controlBox, createBox] = await Promise.all([picker.boundingBox(), create.boundingBox()]);
  expect(Math.abs(controlBox!.y - createBox!.y)).toBeLessThan(1);
  expect(Math.abs(controlBox!.height - createBox!.height)).toBeLessThan(1);
  await page.locator('.profile-picker').evaluate(element => Object.assign((element as HTMLElement).style,
    { position: 'fixed', bottom: '8px', left: '300px', width: '330px', zIndex: '5' }));
  await picker.click();
  const popup = page.getByRole('listbox');
  await expect(popup).toBeVisible();
  await expect(popup).toHaveAttribute('data-side', 'top');
  const bounds = await popup.boundingBox();
  expect(bounds!.y).toBeGreaterThanOrEqual(0);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(930);
  expect(await popup.evaluate(element => getComputedStyle(element).borderRadius)).toBe('10px');
  await page.keyboard.press('Escape');
  await expect(picker).toBeFocused();
});

test('shared dropdown remains usable inside a modal and follows dark mode', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Switch to dark mode' }).click();
  await page.getByRole('button', { name: 'Model Training', exact: true }).click();
  await page.getByRole('combobox', { name: 'Game plugin', exact: true }).click();
  await page.getByRole('option', { name: 'NBA 2K27', exact: true }).click();
  await page.getByRole('button', { name: 'Create shot profile', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a shot profile' });
  const camera = dialog.getByRole('combobox', { name: 'Camera', exact: true });
  await camera.click();
  const option = dialog.getByRole('option', { name: '2K Low', exact: true });
  await expect(option).toBeVisible();
  const popup = dialog.getByRole('listbox');
  expect(await popup.evaluate(element => getComputedStyle(element).backgroundColor)).toBe('rgb(32, 42, 52)');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await expect(camera).toBeFocused();
  await camera.press('ArrowDown');
  await dialog.getByRole('option', { name: '2K Low', exact: true }).click();
  await expect(camera).toHaveText('2K Low');
});
