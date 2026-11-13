import { fireEvent, screen } from '@testing-library/react';
export function selectValue(control: HTMLElement, value: string) {
  fireEvent.keyDown(control, { key: 'ArrowDown' });
  const option = screen.getAllByRole('option').find(item => item.getAttribute('data-value') === value);
  if (!option) throw new Error('Missing select option: ' + value);
  fireEvent.click(option);
}
