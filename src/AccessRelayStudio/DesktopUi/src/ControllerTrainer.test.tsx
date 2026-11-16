import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { selectValue } from './select-test-helper';
import { ControllerTrainer } from './ControllerTrainer';
import { ControllerPage } from './pages/ControllerPage';
import { previewState } from './api';

afterEach(cleanup);
const pack = { key: 'test-game@2.0.0', manifestSha256: 'a'.repeat(64), manifest: { id: 'test-game', version: '2.0.0', name: 'Shot trainer', description: 'Train shots', license: '0BSD', assets: [], tuning: [], presets: [],
  controllerTrainer: { shots: [
    { id: 'standing-still', label: 'Standing Still', instructions: 'Stand still and shoot once.' },
    { id: 'off-dribble', label: 'Off-Dribble', instructions: 'Dribble into a shot.' },
    { id: 'fade-away', label: 'Fade-Away', instructions: 'Move into a fade.' }],
  timingFeedback: [{ id: 'none', label: 'No timing feedback', adjustmentMs: 0 }, { id: 'early', label: 'Early', adjustmentMs: 16 }, { id: 'excellent', label: 'Excellent', adjustmentMs: 0 }, { id: 'late', label: 'Late', adjustmentMs: -16 }],
  tempoFeedback: [{ id: 'none', label: 'No tempo feedback', adjustmentMs: 0 }, { id: 'fast', label: 'Fast', adjustmentMs: 8 }, { id: 'excellent', label: 'Excellent', adjustmentMs: 0 }, { id: 'slow', label: 'Slow', adjustmentMs: -8 }] } } };
const scriptId = '15a2e223-7676-4d11-89d0-ff22353cb37c';
const setup = (saved = false, assigned = false) => {
  const state = structuredClone(previewState.controller);
  state.gamePacks = [pack];
  state.activeProfile.gamePack = { id: pack.manifest.id, version: pack.manifest.version, manifestSha256: pack.manifestSha256, tuning: {} };
  if (saved) state.activeProfile.luaScripts = [{ id: scriptId, name: 'Standing Still', training: {
    packId: 'test-game', shotId: 'standing-still', shotInput: 'rightStick', holdMs: 400, tempoMs: 40, baselineDurationMs: 740, baselineFrames: 6,
    baselineTimingFeedback: 'late', baselineTempoFeedback: 'fast', minimumHoldMs: 1, minimumTempoMs: 2, sourceMatches: true } }];
  if (assigned) Object.assign(state.activeProfile.bindings.find((item: any) => item.control === 'largeRight'), { script: 'lua', enabled: true, luaScriptId: scriptId });
  const status = { ...state.status, connected: true, supportsEliteKeypad: true };
  return { state, status };
};

describe('recorded shot trainer', () => {
  it('offers the replacement trainer when the active controller profile still uses the older pack', () => {
    const { state, status } = setup();
    state.activeProfile.gamePack.version = '1.0.1';
    state.gamePacks.unshift({ ...pack, key: 'test-game@1.0.1', manifest: { ...pack.manifest, version: '1.0.1', name: 'Older fixed script', controllerTrainer: undefined } });
    render(<ControllerPage state={state} status={status} command={vi.fn()} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Plugins' }));
    expect((screen.getByLabelText('Installed package') as HTMLButtonElement).value).toBe(pack.key);
    expect((screen.getByRole('button', { name: 'Create profile from plugin' }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByRole('tab', { name: 'Standing Still' })).toBeNull();
  });
  it('appears inside Controller Plugins with three independent shot types and starts no processing on open', () => {
    const { state, status } = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Plugins' }));
    for (const name of ['Standing Still', 'Off-Dribble', 'Fade-Away']) expect(screen.getByRole('tab', { name })).toBeTruthy();
    expect(command).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('tab', { name: 'Off-Dribble' }));
    fireEvent.click(screen.getByRole('button', { name: 'Record baseline' }));
    expect(command).toHaveBeenCalledWith('trainerStart', { key: pack.key, shotId: 'off-dribble', shotInput: 'rightStick' });
  });

  it('requires baseline feedback, converts physical input, then assigns before showing practice tuning', async () => {
    const initial = setup(), command = vi.fn().mockResolvedValue({ id: scriptId });
    const { rerender } = render(<ControllerTrainer {...initial} command={command} pack={pack} />);
    fireEvent.click(screen.getByRole('button', { name: 'Record baseline' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('trainerStart', { key: pack.key, shotId: 'standing-still', shotInput: 'rightStick' }));
    const context = { key: pack.key, profileId: initial.state.activeProfile.id, shotId: 'standing-still', shotInput: 'rightStick' };
    rerender(<ControllerTrainer {...initial} status={{ ...initial.status, trainerRecording: context, recording: { isRecording: true, hasInput: true, elapsedMs: 740 } }} command={command} pack={pack} />);
    fireEvent.click(screen.getByRole('button', { name: 'Stop baseline recording' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('recordStop', undefined));
    rerender(<ControllerTrainer {...initial} status={{ ...initial.status, trainerRecording: context, recording: { pending: { durationMs: 740, frameCount: 6 } } }} command={command} pack={pack} />);
    const convert = screen.getByRole('button', { name: 'Convert baseline to Lua' }) as HTMLButtonElement;
    expect(convert.disabled).toBe(true);
    selectValue(screen.getByLabelText('Baseline Shot Timing'), 'late');
    expect(convert.disabled).toBe(true);
    selectValue(screen.getByLabelText('Baseline Shot Tempo'), 'fast');
    fireEvent.click(convert);
    await waitFor(() => expect(command).toHaveBeenCalledWith('trainerConvert', { key: pack.key, shotId: 'standing-still', timingFeedback: 'late', tempoFeedback: 'fast' }));
    await screen.findByText('Baseline saved as Lua. Choose its controller assignment below.');
    const saved = setup(true);
    rerender(<ControllerTrainer {...saved} command={command} pack={pack} />);
    expect(screen.queryByLabelText('Shot Timing')).toBeNull();
    selectValue(screen.getByLabelText('Controller key, input or paddle'), 'largeRight');
    fireEvent.click(screen.getByRole('button', { name: 'Assign shot' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('trainerAssign', { key: pack.key, id: scriptId, control: 'largeRight' }));
    await screen.findByText('Shot assigned. Tap once to play the complete shot; Run scripts keeps its current state.');
    const assigned = setup(true, true);
    rerender(<ControllerTrainer {...assigned} command={command} pack={pack} />);
    selectValue(screen.getByLabelText('Shot Timing'), 'early'); selectValue(screen.getByLabelText('Shot Tempo'), 'fast');
    expect(screen.getByText(/hold \+16 ms; stick stroke \+8 ms/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Apply shot feedback' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('trainerTune', { key: pack.key, id: scriptId, feedback: true, timingFeedback: 'early', tempoFeedback: 'fast' }));
    await screen.findByText('Feedback applied to this shot’s Lua. Run scripts keeps its current state.');
    expect((screen.getByRole('button', { name: 'Apply shot feedback' }) as HTMLButtonElement).disabled).toBe(true);
    expect(command.mock.calls.some(([action]) => ['scriptsSet', 'outputStart'].includes(action))).toBe(false);
  });

  it('synchronizes sliders and typed durations, guards drafts and preserves rejected changes', async () => {
    const props = setup(true, true), command = vi.fn().mockRejectedValue(new Error('Whole recording exceeds 15000 ms.'));
    render(<ControllerTrainer {...props} command={command} pack={pack} />);
    fireEvent.change(screen.getByLabelText('Hold before release (ms)'), { target: { value: '450' } });
    expect((screen.getByLabelText('Hold before release (ms) slider') as HTMLInputElement).value).toBe('450');
    fireEvent.change(screen.getByLabelText('Stick stroke duration (ms) slider'), { target: { value: '52' } });
    expect((screen.getByLabelText('Stick stroke duration (ms)') as HTMLInputElement).value).toBe('52');
    fireEvent.click(screen.getByRole('tab', { name: 'Fade-Away' }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save manual durations' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('trainerTune', { key: pack.key, id: scriptId, holdMs: 450, tempoMs: 52 }));
    await screen.findByText('Whole recording exceeds 15000 ms.');
    expect((screen.getByLabelText('Hold before release (ms)') as HTMLInputElement).value).toBe('450');
    fireEvent.change(screen.getByLabelText('Hold before release (ms)'), { target: { value: '' } });
    expect((screen.getByRole('button', { name: 'Save manual durations' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps recording ownership across shot tabs and prevents unavailable paddles and conflicting assignments', async () => {
    const props = setup(true, true), command = vi.fn();
    props.status.supportsEliteKeypad = false;
    Object.assign(props.state.activeProfile.bindings.find((item: any) => item.control === 'a'), { script: 'lua', luaScriptId: 'another-script' });
    const { rerender } = render(<ControllerTrainer {...props} command={command} pack={pack} />);
    fireEvent.click(screen.getByLabelText('Controller key, input or paddle'));
    expect(screen.getByRole('option', { name: /A button.*already assigned/ }).getAttribute('data-disabled')).not.toBeNull();
    expect(screen.getByRole('option', { name: /Small left paddle.*unavailable/ }).getAttribute('data-disabled')).not.toBeNull();
    fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Escape' });
    rerender(<ControllerTrainer {...props} status={{ ...props.status, trainerRecording: { key: pack.key, profileId: props.state.activeProfile.id, shotId: 'standing-still', shotInput: 'rightStick' }, recording: { isRecording: true } }} command={command} pack={pack} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Fade-Away' }));
    expect((screen.getByRole('button', { name: 'Record baseline' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/Finish the recording in its shot tab/)).toBeTruthy();
  });

  it('supports button baselines without inventing tempo and protects custom Lua', () => {
    const props = setup(true, true), command = vi.fn();
    Object.assign(props.state.activeProfile.luaScripts[0].training, { shotInput: 'x', tempoMs: null });
    const { rerender } = render(<ControllerTrainer {...props} command={command} pack={pack} />);
    expect((screen.getByLabelText('Shot Tempo') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByLabelText('Stick stroke duration (ms)')).toBeNull();
    props.state.activeProfile.luaScripts[0].training.sourceMatches = false;
    rerender(<ControllerTrainer {...props} command={command} pack={pack} />);
    expect(screen.getByText(/This Lua script has custom edits/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save manual durations' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
