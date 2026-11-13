import type { Locator } from '@playwright/test';
export async function selectOption(control: Locator, option: string | { value?: string; label?: string; index?: number }) {
  await control.click();
  const options = control.page().getByRole('option');
  if (typeof option !== 'string' && option.label !== undefined)
    await control.page().getByRole('option', { name: option.label, exact: true }).click();
  else if (typeof option !== 'string' && option.index !== undefined) await options.nth(option.index).click();
  else {
    const value = typeof option === 'string' ? option : option.value;
    await control.page().locator('[role="option"][data-value=' + JSON.stringify(value) + ']').click();
  }
}
