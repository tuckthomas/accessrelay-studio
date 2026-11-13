import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Select, SelectOption } from './form-controls';

afterEach(cleanup);
describe('switch access to shared choices', () => {
  it('steps through enabled grouped choices without opening a pointer popup', () => {
    const changed = vi.fn();
    const view = render(<Select aria-label="Sequence choice" value="one" onValueChange={changed}>
      <SelectOption value="one">First</SelectOption><SelectOption value="disabled" disabled>Unavailable</SelectOption>
      <optgroup label="Saved sequences"><SelectOption value="saved">Saved sequence</SelectOption></optgroup>
      <optgroup label="Disabled group" disabled><SelectOption value="group-disabled">Hidden from scanning</SelectOption></optgroup>
    </Select>);
    const control = screen.getByRole('combobox', { name: 'Sequence choice' });
    fireEvent(control, new CustomEvent('accessrelay-select-adjust', { detail: 1 }));
    expect(changed).toHaveBeenLastCalledWith('saved');
    expect(screen.queryByRole('listbox')).toBeNull();
    view.rerender(<Select aria-label="Sequence choice" value="saved" onValueChange={changed}>
      <SelectOption value="one">First</SelectOption><SelectOption value="disabled" disabled>Unavailable</SelectOption>
      <optgroup label="Saved sequences"><SelectOption value="saved">Saved sequence</SelectOption></optgroup>
    </Select>);
    fireEvent(control, new CustomEvent('accessrelay-select-adjust', { detail: -1 }));
    expect(changed).toHaveBeenLastCalledWith('one');
  });
  it('does not adjust a disabled shared choice', () => {
    const changed = vi.fn();
    render(<Select aria-label="Locked choice" disabled value="one" onValueChange={changed}><SelectOption value="one">First</SelectOption><SelectOption value="two">Second</SelectOption></Select>);
    fireEvent(screen.getByRole('combobox', { name: 'Locked choice' }), new CustomEvent('accessrelay-select-adjust', { detail: 1 }));
    expect(changed).not.toHaveBeenCalled();
  });
});
