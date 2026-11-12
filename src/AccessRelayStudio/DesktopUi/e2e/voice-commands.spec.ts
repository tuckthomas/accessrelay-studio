import { expect, test } from '@playwright/test';

test('voice commands save bounded choices without starting capture and protect unsaved edits', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Voice', exact: true }).click();
  await page.getByRole('tab', { name: 'Voice commands', exact: true }).click();
  await expect(page.getByText('Local commands are off.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start command listening', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Add voice command', exact: true }).click();
  await page.getByLabel('Command 1 name', { exact: true }).fill('Pass');
  await page.getByLabel('Command 1 phrase', { exact: true }).fill('pass');
  await page.getByLabel('Command 1 duration (ms)', { exact: true }).fill('80');
  await page.getByRole('tab', { name: 'Live voice', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByLabel('Command 1 phrase', { exact: true })).toHaveValue('pass');
  await page.getByRole('button', { name: 'Save voice commands', exact: true }).click();
  await expect(page.getByText('Voice commands saved. Listening remains off.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Live voice', exact: true }).click();
  await page.getByRole('tab', { name: 'Voice commands', exact: true }).click();
  await expect(page.getByLabel('Command 1 phrase', { exact: true })).toHaveValue('pass');
  await expect(page.getByLabel('Command 1 duration (ms)', { exact: true })).toHaveValue('80');
  await expect(page.getByRole('button', { name: 'Record sound for command 1', exact: true })).toBeDisabled();
  await expect(page.getByText('Local commands are off.', { exact: true })).toBeVisible();
});
