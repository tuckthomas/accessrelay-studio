import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ControllerPage, TrajectoryChart } from './ControllerPage';
import { previewState } from '../api';
afterEach(cleanup);
const setup = () => structuredClone(previewState.controller);

describe('controller workflows', () => {
  it('saves independent smoothing settings without losing unsaved sticky choices', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Accessibility' }));
    expect((screen.getByLabelText('Left stick smoothing') as HTMLInputElement).value).toBe('0');
    fireEvent.click(screen.getByLabelText('B button'));
    fireEvent.change(screen.getByLabelText('Left stick smoothing'), { target: { value: '25' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save smoothing' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('smoothingSave', { settings: { leftTimeConstantMs: 25, rightTimeConstantMs: 0 } }));
    await screen.findByText('Stick smoothing saved.');
    expect((screen.getByLabelText('B button') as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('tab', { name: 'Button mapping' }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
  });
  it('keeps sticky edits through status updates and guards navigation until saved or discarded', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    const { rerender } = render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Accessibility' }));
    fireEvent.click(screen.getByLabelText('A button'));
    rerender(<ControllerPage state={structuredClone(state)} status={{ ...state.status, meanPollMs: 4 }} command={command} />);
    expect((screen.getByLabelText('A button') as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('tab', { name: 'Button mapping' }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save sticky controls' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('stickySave', { controls: ['a'] }));
    await screen.findByText('Sticky controls saved. Release the controls before pressing to latch.');
    fireEvent.click(screen.getByRole('tab', { name: 'Button mapping' }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('preserves a rejected sticky draft and prevents changes while recording', async () => {
    const state = setup(), command = vi.fn().mockRejectedValue(new Error('Finish the recording first.'));
    const { rerender } = render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Accessibility' }));
    fireEvent.click(screen.getByLabelText('Left trigger'));
    fireEvent.click(screen.getByRole('button', { name: 'Save sticky controls' }));
    await screen.findByText('Finish the recording first.');
    expect((screen.getByLabelText('Left trigger') as HTMLInputElement).checked).toBe(true);
    rerender(<ControllerPage state={state} status={{ ...state.status, recording: { isRecording: true } }} command={command} />);
    expect((screen.getByLabelText('Left trigger') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Save sticky controls' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('does not partially save a recording draft when interval retiming is rejected', async () => {
    const state = setup(), id = '67e6e39e-531e-447b-a7a7-763287ca44ea';
    const recording = { id, name: 'Original sequence', channels: 'rightStick', durationMs: 100,
      frames: [{ timeMs: 0, state: { rightX: 0, rightY: -32767 } }, { timeMs: 80, state: { rightX: 0, rightY: 32767 } }] };
    state.activeProfile.recordings = [{ id, name: recording.name, durationMs: 100, frameCount: 2 }];
    const command = vi.fn(async (action: string) => {
      if (action === 'recordGet') return structuredClone(recording);
      if (action === 'recordRetime') throw new Error('Choose a valid interval');
      return null;
    });
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /Sequences/ })); fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    fireEvent.change(await screen.findByLabelText('Sequence name'), { target: { value: 'Uncommitted draft' } });
    fireEvent.click(screen.getByText('Adjust an interval'));
    fireEvent.click(screen.getByRole('button', { name: 'Save interval timing' }));
    expect(await screen.findByText('Choose a valid interval')).toBeTruthy();
    expect(command.mock.calls.some(([action]) => action === 'recordUpdate')).toBe(false);
    expect(command).toHaveBeenCalledWith('recordRetime', expect.objectContaining({ id, recording: expect.objectContaining({ name: 'Uncommitted draft' }) }));
    expect((screen.getByLabelText('Sequence name') as HTMLInputElement).value).toBe('Uncommitted draft');
  });
  it('protects unsaved mapping text while selecting another control or controller tab', () => {
    const state = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByLabelText('What should it do?'), { target: { value: 'moving' } });
    fireEvent.click(screen.getByRole('tab', { name: /Sequences/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('moving');
    fireEvent.click(screen.getByRole('button', { name: 'A button: Pass through' }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByRole('heading', { name: 'Large right paddle' })).toBeTruthy();
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('moving');
    expect(command).not.toHaveBeenCalled();
  });
  it('never starts output or sequences while opening or changing workspaces', () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Input & response' }));
    expect(command).not.toHaveBeenCalled();
    expect((screen.getByRole('button', { name: /Connect output/ }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('saves the selected mapping using the native command shape', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    fireEvent.change(screen.getByLabelText('What should it do?'), { target: { value: 'moving' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save mapping' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('bindingSave', expect.objectContaining({ control: 'largeRight', script: 'moving', enabled: true })));
    expect(await screen.findByText('Mapping saved.')).toBeTruthy();
  });
  it('keeps an unsaved mapping through background status updates and preserves it after a native error', async () => {
    const state = setup(), command = vi.fn().mockRejectedValue(new Error('Finish training first'));
    const view = render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    fireEvent.change(screen.getByLabelText('What should it do?'), { target: { value: 'stepback' } });
    view.rerender(<ControllerPage state={structuredClone(state)} status={{ ...state.status, connected: true, meanPollMs: 4.1 }} command={command} />);
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('stepback');
    fireEvent.click(screen.getByRole('button', { name: 'Save mapping' }));
    expect(await screen.findByText('Finish training first')).toBeTruthy();
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('stepback');
    expect(screen.getByText('Unsaved changes')).toBeTruthy();
  });
  it('saves a built-in timing draft without sending controller output', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue({ frames: [], durationMs: 800 });
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /Sequences/ }));
    fireEvent.click(screen.getByRole('tab', { name: 'Built-in sequences' }));
    fireEvent.change(screen.getByLabelText('Hold down'), { target: { value: '550' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save timing' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('timingSave', expect.objectContaining({ variant: 'rhythm', timing: expect.objectContaining({ downHold: 550 }) })));
    expect(command.mock.calls.some(([name]) => ['outputStart', 'scriptsSet'].includes(name))).toBe(false);
  });
  it('keeps setup deletion deliberate and disables deleting the last setup', () => {
    const state = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={state.status} command={command} />);
    expect((screen.getByRole('button', { name: 'Delete setup', hidden: true }) as HTMLButtonElement).disabled).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });
  it('draws full normalized trajectory amplitude and does not amplify tiny int16 recording values', () => {
    const view = render(<TrajectoryChart data={{ normalized: true, durationMs: 100, frames: [{ timeMs: 0, rightY: -1 }, { timeMs: 100, rightY: 1 }] }} />);
    expect(view.container.querySelector('path[stroke="#367456"]')?.getAttribute('d')).toBe('M20.0,164.0 L880.0,16.0');
    view.rerender(<TrajectoryChart recording={{ durationMs: 100, frames: [{ timeMs: 0, state: { rightY: 1 } }, { timeMs: 100, state: { rightY: -1 } }] }} />);
    expect(view.container.querySelector('path[stroke="#367456"]')?.getAttribute('d')).toBe('M20.0,90.0 L880.0,90.0');
  });
  it('reloads feedback-adjusted recording timing before a later edit is saved', async () => {
    const state = setup(), id = '57e31ee3-bb31-45b7-adb5-99fb9059ade6';
    let recording = { id, name: 'Test sequence', channels: 'rightStick', durationMs: 120, frames: [
      { timeMs: 0, state: { buttons: 0, leftTrigger: 0, rightTrigger: 0, leftX: 0, leftY: 0, rightX: 0, rightY: -32767 } },
      { timeMs: 80, state: { buttons: 0, leftTrigger: 0, rightTrigger: 0, leftX: 0, leftY: 0, rightX: 0, rightY: 32767 } }
    ] };
    state.activeProfile.recordings = [{ id, name: recording.name, durationMs: 120, frameCount: 2 }];
    const command = vi.fn(async (action: string) => {
      if (action === 'recordGet') return structuredClone(recording);
      if (action === 'nbaFeedback') recording = { ...recording, durationMs: 136, frames: [recording.frames[0], { ...recording.frames[1], timeMs: 96 }] };
      return null;
    });
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /Sequences/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await screen.findByLabelText('Sequence name');
    fireEvent.click(screen.getByText('NBA 2K27 feedback tuning'));
    fireEvent.change(screen.getByLabelText('Timing feedback'), { target: { value: 'slightlyEarly' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply feedback adjustment' }));
    await waitFor(() => expect(command.mock.calls.filter(([action]) => action === 'recordGet')).toHaveLength(2));
    fireEvent.change(screen.getByLabelText('Sequence name'), { target: { value: 'Renamed sequence' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save sequence' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('recordUpdate', expect.objectContaining({ recording: expect.objectContaining({ durationMs: 136, name: 'Renamed sequence', frames: expect.arrayContaining([expect.objectContaining({ timeMs: 96 })]) }) })));
  });
});
