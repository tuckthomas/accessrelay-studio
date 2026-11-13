import { expect, test } from '@playwright/test';
test('nonvisual feedback stays opt-in and persists independently of controller profile selection', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await expect(page.getByRole('checkbox', { name: 'Audio cues', exact: true })).not.toBeChecked();
  await expect(page.getByRole('button', { name: 'Test feedback', exact: true })).toBeDisabled();
  await page.getByRole('checkbox', { name: 'Audio cues', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Controller pulses', exact: true }).check();
  await page.getByLabel('Cue volume', { exact: true }).fill('30');
  await page.getByRole('button', { name: 'Save feedback preferences', exact: true }).click();
  await expect(page.getByText('Feedback preferences saved.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Test feedback', exact: true }).click();
  await expect(page.getByText('Design preview only. Run the installed app to hear or feel feedback.')).toBeVisible();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await expect(page.getByRole('checkbox', { name: 'Audio cues', exact: true })).toBeChecked();
  await expect(page.getByLabel('Cue volume', { exact: true })).toHaveValue('30');
});
test('smoothing saves independently per stick and persists across workspace navigation', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  const left = page.getByLabel('Left stick smoothing', { exact: true });
  await expect(left).toHaveValue('0');
  await left.fill('25');
  await page.getByRole('button', { name: 'Save smoothing', exact: true }).click();
  await expect(page.getByText('Stick smoothing saved.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await expect(left).toHaveValue('25');
  await expect(page.getByLabel('Right stick smoothing', { exact: true })).toHaveValue('0');
});
test('sticky controls save with the setup and remain editable after navigating away', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await page.getByLabel('A button', { exact: true }).check();
  await page.getByLabel('Left trigger', { exact: true }).check();
  await page.getByRole('button', { name: 'Save sticky controls' }).click();
  await expect(page.getByText('Sticky controls saved. Release the controls before pressing to latch.')).toBeVisible();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('tab', { name: 'Accessibility' }).click();
  await expect(page.getByLabel('A button', { exact: true })).toBeChecked();
  await expect(page.getByLabel('Left trigger', { exact: true })).toBeChecked();
  await expect(page.getByRole('button', { name: 'Save sticky controls' })).toBeDisabled();
});
test('task navigation remains usable at desktop widths without horizontal overflow', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Your workspace', exact: true })).toBeVisible();
  await expect(page.getByText('Design preview — no hardware, capture or audio sessions are connected.')).toBeVisible();
  for (const width of [1100, 1440, 1700]) {
    await page.setViewportSize({ width, height: 930 });
    for (const name of ['Workspace', 'Controller', 'Remote Play', 'Voice', 'Settings']) {
      await page.getByRole('complementary', { name: 'Main navigation' }).getByRole('button', { name, exact: true }).click();
      await expect(page.locator('main h1')).toBeVisible();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
      expect(overflow, name + ' overflows at ' + width).toBe(false);
    }
  }
  expect(errors).toEqual([]);
});
test('setup dialog works with keyboard and keeps the UI local in preview', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await page.getByRole('button', { name: 'Create a controller profile' }).click();
  const dialog = page.getByRole('dialog', { name: 'A setup of your own' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Profile name').fill('Keyboard setup');
  await dialog.getByLabel('Profile name').press('Enter');
  await expect(dialog).not.toBeVisible();
  await expect(page.getByLabel('Controller profile', { exact: true })).toHaveValue(/.+/);
  await page.getByRole('tab', { name: 'Lua scripts 0' }).click();
  await expect(page.getByRole('heading', { name: 'Your script editor' })).toBeVisible();
});
test('mapping primary action stays visible on a desktop window', async ({ page }) => {
  await page.setViewportSize({ width: 1536, height: 960 });
  await page.goto('/');
  await page.getByRole('button', { name: 'Controller', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save mapping' })).toBeInViewport();
  await expect(page.getByText('Use the sequence timing saved in this setup.')).toBeVisible();
  await page.screenshot({ path: 'D:/AccessRelayStudio/DesignReview/controller-final.png', fullPage: false });
});
