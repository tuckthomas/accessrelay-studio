import { selectValue } from '../select-test-helper';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ControllerPage, TrajectoryChart } from './ControllerPage';
import { previewState } from '../api';
import { sequenceTimingFields } from '../sequence-timing';
afterEach(cleanup);
const setup = () => structuredClone(previewState.controller);

describe('controller workflows', () => {
  it('keeps feedback opt-in, saves independent channel levels and requires saving before a test cue', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue({ queued: true });
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Accessibility' }));
    expect((screen.getByLabelText('Audio cues') as HTMLInputElement).checked).toBe(false);
    expect((screen.getByRole('button', { name: 'Test feedback' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('Audio cues')); fireEvent.click(screen.getByLabelText('Controller pulses'));
    fireEvent.change(screen.getByLabelText('Cue volume'), { target: { value: '30' } });
    fireEvent.change(screen.getByLabelText('Pulse intensity'), { target: { value: '20' } });
    expect((screen.getByRole('button', { name: 'Test feedback' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Save feedback preferences' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('feedbackSave', { settings: { audioEnabled: true, hapticsEnabled: true, volume: .3, strength: .2 } }));
    await screen.findByText('Feedback preferences saved.');
    fireEvent.click(screen.getByRole('button', { name: 'Test feedback' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('feedbackTest'));
  });
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
    fireEvent.click(screen.getByText('Advanced sample editing'));
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
    selectValue(screen.getByLabelText('What should it do?'), 'moving');
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
    expect((screen.getByRole('button', { name: 'Start controller' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('starts output and mapped scripts with one action, and keeps pausing scripts separate from stopping the controller', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    const { rerender } = render(<ControllerPage state={state} status={{ ...state.status, connected: true, outputActive: false }} command={command} />);
    expect(screen.queryByLabelText('Run scripts')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Start controller' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('scriptsSet', { enabled: true }));
    expect(command.mock.calls.some(([action]) => action === 'outputStart')).toBe(false);
    rerender(<ControllerPage state={state} status={{ ...state.status, connected: true, outputActive: true, scriptsEnabled: true }} command={command} />);
    fireEvent.click(screen.getByLabelText('Run scripts'));
    await waitFor(() => expect(command).toHaveBeenCalledWith('scriptsSet', { enabled: false }));
    fireEvent.click(screen.getByRole('button', { name: 'Stop controller' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('outputStop', undefined));
  });
  it('saves the selected mapping using the native command shape', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    selectValue(screen.getByLabelText('What should it do?'), 'moving');
    fireEvent.click(screen.getByRole('button', { name: 'Save mapping' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('bindingSave', expect.objectContaining({ control: 'largeRight', script: 'moving', enabled: true })));
    expect(await screen.findByText('Mapping saved.')).toBeTruthy();
  });
  it('keeps an unsaved mapping through background status updates and preserves it after a native error', async () => {
    const state = setup(), command = vi.fn().mockRejectedValue(new Error('Finish training first'));
    const view = render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    selectValue(screen.getByLabelText('What should it do?'), 'stepback');
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
  it('edits measured recording phases with the shared timing controls and saves one complete draft', async () => {
    const state = setup(), id = '67e6e39e-531e-447b-a7a7-763287ca44ea';
    const recording = { id, name: 'Shot', channels: 'rightStick', durationMs: 250,
      frames: [[0, -4000], [40, -32768], [120, -5000], [160, 32767], [210, 0]]
        .map(([timeMs, rightY], i) => ({ timeMs, state: { rightX: i * 100, rightY } })) };
    state.activeProfile.recordings = [{ id, name: 'Shot', durationMs: 250, frameCount: 5 }];
    const command = vi.fn(async (action: string, _args?: any) => action === 'recordGet' ? structuredClone(recording) : null);
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Sequences/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const hold = await screen.findByLabelText('Hold down');
    sequenceTimingFields.forEach(field => expect(screen.getByLabelText(field.label)).toBeTruthy());
    expect((screen.getByLabelText('Before movement') as HTMLInputElement).value).toBe('0');
    expect((screen.getByLabelText('Return to center') as HTMLInputElement).value).toBe('40');
    expect((screen.getByLabelText('Flick duration') as HTMLInputElement).value).toBe('0');
    expect((screen.getByLabelText('Settle time') as HTMLInputElement).value).toBe('0');
    fireEvent.click(screen.getByText('Flick direction'));
    expect(screen.getByLabelText('Horizontal')).toBeTruthy(); expect(screen.getByLabelText('Vertical')).toBeTruthy();
    expect((hold as HTMLInputElement).value).toBe('80');
    expect((screen.getByLabelText('Move down') as HTMLInputElement).value).toBe('40');
    expect((screen.getByLabelText('Move up') as HTMLInputElement).value).toBe('40');
    expect((screen.getByLabelText('Hold up') as HTMLInputElement).value).toBe('50');
    expect(hold.closest('.timing-grid')).toBeTruthy();
    fireEvent.change(hold, { target: { value: '160' } });
    fireEvent.change(screen.getByLabelText('Sequence name'), { target: { value: 'Adjusted shot' } });
    expect(command.mock.calls.some(([action]) => action === 'recordUpdate')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Save sequence' }));
    await screen.findByText('Sequence saved.');
    const saved = command.mock.calls.find(([action]) => action === 'recordUpdate')?.[1].recording;
    expect(saved.name).toBe('Adjusted shot'); expect(saved.durationMs).toBe(330);
    expect(saved.frames.map((frame: any) => frame.timeMs)).toEqual([0, 40, 200, 240, 290]);
    expect(saved.frames.map((frame: any) => frame.state)).toEqual(recording.frames.map(frame => frame.state));
    expect(command.mock.calls.some(([action]) => ['recordRetime', 'outputStart', 'scriptsSet'].includes(action))).toBe(false);
  });
  it('retains phase and name drafts when saving fails and blocks an invalid timing', async () => {
    const state = setup(), id = '67e6e39e-531e-447b-a7a7-763287ca44ea';
    const recording = { id, name: 'Shot', durationMs: 100, frames: [
      { timeMs: 0, state: { rightY: -32768 } }, { timeMs: 80, state: { rightY: 32767 } }] };
    state.activeProfile.recordings = [{ id, name: 'Shot', durationMs: 100, frameCount: 2 }];
    const command = vi.fn(async (action: string) => {
      if (action === 'recordGet') return structuredClone(recording);
      throw new Error('Profile changed on disk');
    });
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Sequences/ })); fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const hold = await screen.findByLabelText('Hold down');
    expect((screen.getByLabelText('Move down') as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText('Move down') as HTMLInputElement).value).toBe('0');
    fireEvent.change(hold, { target: { value: '' } });
    expect((screen.getByRole('button', { name: 'Save sequence' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(hold, { target: { value: '160' } });
    fireEvent.change(screen.getByLabelText('Sequence name'), { target: { value: 'Keep this draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save sequence' }));
    await screen.findByText('Profile changed on disk');
    expect((hold as HTMLInputElement).value).toBe('160');
    expect((screen.getByLabelText('Sequence name') as HTMLInputElement).value).toBe('Keep this draft');
    fireEvent.click(screen.getByRole('button', { name: 'Close', exact: true }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
  });
  it('saves added delay, return, flick, settle and direction in the recording itself', async () => {
    const state = setup(), id = '67e6e39e-531e-447b-a7a7-763287ca44ea';
    const recording = { id, name: 'Shot', channels: 'rightStick', durationMs: 100, frames: [
      { timeMs: 0, state: { rightX: 0, rightY: -32768 } }, { timeMs: 80, state: { rightX: 0, rightY: 32767 } }] };
    state.activeProfile.recordings = [{ id, name: 'Shot', durationMs: 100, frameCount: 2 }];
    const command = vi.fn(async (action: string, _args?: any) => action === 'recordGet' ? structuredClone(recording) : null);
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Sequences/ })); fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await screen.findByLabelText('Before movement');
    for (const [label, value] of [['Before movement','50'],['Return to center','80'],['Flick duration','60'],['Settle time','40']])
      fireEvent.change(screen.getByLabelText(label), { target: { value } });
    fireEvent.click(screen.getByText('Flick direction'));
    fireEvent.change(screen.getByLabelText('Horizontal'), { target: { value: '32767' } });
    fireEvent.change(screen.getByLabelText('Vertical'), { target: { value: '0' } });
    expect(command.mock.calls.filter(([action]) => action === 'recordUpdate')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Save sequence' }));
    await screen.findByText('Sequence saved.');
    const saved = command.mock.calls.find(([action]) => action === 'recordUpdate')?.[1].recording;
    expect(saved.durationMs).toBe(330);
    expect(saved.frames.some((frame: any) => frame.state.rightX === 32767 && frame.state.rightY === 0)).toBe(true);
    expect((screen.getByLabelText('Before movement') as HTMLInputElement).value).toBe('50');
    expect((screen.getByLabelText('Return to center') as HTMLInputElement).value).toBe('80');
    expect((screen.getByLabelText('Flick duration') as HTMLInputElement).value).toBe('60');
    expect((screen.getByLabelText('Settle time') as HTMLInputElement).value).toBe('40');
    expect((screen.getByLabelText('Horizontal') as HTMLInputElement).value).toBe('32767');
  });
  it('keeps setup deletion deliberate and disables deleting the last setup', () => {
    const state = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={state.status} command={command} />);
    expect((screen.getByRole('button', { name: 'Delete profile', hidden: true }) as HTMLButtonElement).disabled).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });
  it('draws full normalized trajectory amplitude and does not amplify tiny int16 recording values', () => {
    const view = render(<TrajectoryChart data={{ normalized: true, durationMs: 100, frames: [{ timeMs: 0, rightY: -1 }, { timeMs: 100, rightY: 1 }] }} />);
    expect(view.container.querySelector('path[stroke="var(--accent)"]')?.getAttribute('d')).toBe('M20.0,164.0 L880.0,16.0');
    view.rerender(<TrajectoryChart recording={{ durationMs: 100, frames: [{ timeMs: 0, state: { rightY: 1 } }, { timeMs: 100, state: { rightY: -1 } }] }} />);
    expect(view.container.querySelector('path[stroke="var(--accent)"]')?.getAttribute('d')).toBe('M20.0,90.0 L880.0,90.0');
  });
  it('keeps NBA feedback controls out of an ordinary controller profile', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Sequences/ }));
    fireEvent.click(screen.getByRole('tab', { name: 'Built-in sequences' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('trajectory', expect.anything()));
    expect(screen.queryByText('Fine-tune from shot feedback')).toBeNull();
    expect(screen.queryByText('NBA 2K27 feedback tuning')).toBeNull();
  });

});
