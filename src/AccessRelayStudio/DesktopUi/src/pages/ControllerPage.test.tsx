import { selectValue } from '../select-test-helper';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ControllerPage } from './ControllerPage';
import { previewState } from '../api';
afterEach(cleanup);
const scriptId='3570fa0f-f77f-4a50-9309-2fc6a239fce2';
const setup = () => { const state=structuredClone(previewState.controller); state.activeProfile.luaScripts=[{id:scriptId,name:'Test input',source:'wait(80)'}] as any; return state; };

describe('controller workflows', () => {
  it('has one Scripts page, opens mapped Lua directly and has no sequence editors or built-in choices', async () => {
    const state=setup(), command=vi.fn(async(action:string)=>action==='luaGet'?{id:scriptId,name:'Test input',source:'wait(80)'}:null);
    (state.activeProfile.bindings.find((binding:any)=>binding.control==='largeRight') as any).script='lua';
    (state.activeProfile.bindings.find((binding:any)=>binding.control==='largeRight') as any).luaScriptId=scriptId;
    render(<ControllerPage state={state} status={state.status} command={command}/>);
    expect(screen.queryByRole('tab',{name:/Sequences/})).toBeNull();
    expect(screen.queryByRole('option',{name:/Timed|Alternating|Moving|Two-stage/})).toBeNull();
    fireEvent.click(screen.getByRole('button',{name:'Edit script'}));
    expect((await screen.findByLabelText('Lua source') as HTMLTextAreaElement).value).toBe('wait(80)');
    expect(command).toHaveBeenCalledWith('luaGet',{id:scriptId});
    expect(screen.queryByText('Built-in sequences')).toBeNull();
    expect(screen.queryByLabelText('Hold down')).toBeNull();
    expect(command.mock.calls.some(([action])=>['outputStart','scriptsSet'].includes(action))).toBe(false);
  });
  it('saves captured controller input directly as Lua and opens it without a recording editor', async () => {
    const state=setup(), saved={id:'7c79ba32-2222-4444-a111-45b238950fa1',name:'Captured shot',source:'output.replay(80, "RightY", [[\n0,-32768\n]])'};
    const status={...state.status,recording:{isRecording:false,pending:{durationMs:80,frameCount:1}}};
    const command=vi.fn(async(action:string)=>action==='recordSave'?saved:null);
    render(<ControllerPage state={state} status={status} command={command}/>);
    fireEvent.click(screen.getByRole('tab',{name:/^Scripts/}));
    fireEvent.click(screen.getByRole('button',{name:'Record input'}));
    fireEvent.change(screen.getByLabelText('Script name'),{target:{value:'Captured shot'}});
    fireEvent.click(screen.getByRole('button',{name:'Save as Lua script'}));
    await waitFor(()=>expect(command).toHaveBeenCalledWith('recordSave',{name:'Captured shot'}));
    expect((await screen.findByLabelText('Lua source') as HTMLTextAreaElement).value).toBe(saved.source);
    expect(screen.queryByRole('dialog',{name:'Record controller input'})).toBeNull();
    expect(screen.queryByLabelText('Hold down')).toBeNull();
  });
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

  it('protects unsaved mapping text while selecting another control or controller tab', () => {
    const state = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={state.status} command={command} />);
    selectValue(screen.getByLabelText('What should it do?'), 'lua:' + scriptId);
    fireEvent.click(screen.getByRole('tab', { name: /^Scripts/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('lua:' + scriptId);
    fireEvent.click(screen.getByRole('button', { name: 'A button: Normal input' }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByRole('heading', { name: 'Large right paddle' })).toBeTruthy();
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('lua:' + scriptId);
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
    expect(screen.getByRole('button', { name: 'Start controller' }).getAttribute('data-state')).toBe('off');
    expect(screen.queryByLabelText('Run scripts')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Start controller' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('scriptsSet', { enabled: true }));
    expect(command.mock.calls.some(([action]) => action === 'outputStart')).toBe(false);
    rerender(<ControllerPage state={state} status={{ ...state.status, connected: true, outputActive: true, scriptsEnabled: true }} command={command} />);
    expect(screen.getByRole('button', { name: 'Stop controller' }).getAttribute('data-state')).toBe('on');
    fireEvent.click(screen.getByLabelText('Run scripts'));
    await waitFor(() => expect(command).toHaveBeenCalledWith('scriptsSet', { enabled: false }));
    fireEvent.click(screen.getByRole('button', { name: 'Stop controller' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('outputStop', undefined));
  });
  it('saves the selected mapping using the native command shape', async () => {
    const state = setup(), command = vi.fn().mockResolvedValue(null);
    render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    selectValue(screen.getByLabelText('What should it do?'), 'lua:' + scriptId);
    fireEvent.click(screen.getByRole('button', { name: 'Save mapping' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('bindingSave', expect.objectContaining({ control: 'largeRight', script: 'lua', luaScriptId: scriptId, enabled: true })));
    expect(await screen.findByText('Mapping saved.')).toBeTruthy();
  });
  it('keeps an unsaved mapping through background status updates and preserves it after a native error', async () => {
    const state = setup(), command = vi.fn().mockRejectedValue(new Error('Finish training first'));
    const view = render(<ControllerPage state={state} status={{ ...state.status, connected: true }} command={command} />);
    selectValue(screen.getByLabelText('What should it do?'), 'lua:' + scriptId);
    view.rerender(<ControllerPage state={structuredClone(state)} status={{ ...state.status, connected: true, meanPollMs: 4.1 }} command={command} />);
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('lua:' + scriptId);
    fireEvent.click(screen.getByRole('button', { name: 'Save mapping' }));
    expect(await screen.findByText('Finish training first')).toBeTruthy();
    expect((screen.getByLabelText('What should it do?') as HTMLSelectElement).value).toBe('lua:' + scriptId);
    expect(screen.getByText('Unsaved changes')).toBeTruthy();
  });




  it('keeps setup deletion deliberate and disables deleting the last setup', () => {
    const state = setup(), command = vi.fn();
    render(<ControllerPage state={state} status={state.status} command={command} />);
    expect((screen.getByRole('button', { name: 'Delete profile', hidden: true }) as HTMLButtonElement).disabled).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });



});
