// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { VoicePage } from './VoicePage';
import { hasUnsavedChanges } from '../ui';

afterEach(cleanup);

function fixture(overrides: any = {}) {
  return {
    devices: { microphones: [{ id: 'mic-one', name: 'Desk microphone', isDefault: true }], outputs: [{ id: 'phones-one', name: 'Headphones', isDefault: true }] },
    models: [{ id: 'builtin', name: 'Built-in effects', usesReference: false }], profiles: [],
    microphoneId: 'mic-one', outputId: 'phones-one', destination: 'headphones', selectedModelId: 'builtin',
    settings: { preset: 'clean', pitchSemitones: 0, gainDb: 0, muted: false },
    status: { isRunning: false, isStarting: false, busy: false, message: 'Voice processing is off.' },
    ...overrides,
  };
}

describe('voice workflows', () => {
  it('does not start microphone capture or output merely by opening the page', () => {
    const command = vi.fn().mockResolvedValue(null);
    render(<VoicePage state={fixture()} status={null} command={command} />);
    expect(command).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Try this voice' })).toBeTruthy();
  });

  it.each([false, true])('saves the native route before starting live preview (reference voice: %s)', async usesReference => {
    const command = vi.fn().mockResolvedValue(null);
    render(<VoicePage state={fixture({ models: [{ id: 'builtin', name: 'Selected voice model', usesReference }] })} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Try this voice' }));
    await waitFor(() => expect(command).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[0]).toEqual(['routeSave', { microphoneId: 'mic-one', outputId: 'phones-one', destination: 'headphones' }]);
    expect(command.mock.calls[1]).toEqual(['start']);
  });

  it('keeps a reference voice preview live until explicitly stopped', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ models: [{ id: 'builtin', name: 'Reference model', usesReference: true }] });
    render(<VoicePage state={state} status={{ ...state.status, isRunning: true, message: 'Voice processing is live' }} command={command} />);
    expect(screen.getByText('Voice is live')).toBeTruthy();
    expect(screen.queryByText(/Reference preview records/)).toBeNull();
    expect(command).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Stop voice' }));
    await waitFor(() => expect(command).toHaveBeenCalledExactlyOnceWith('stop'));
  });

  it('shows route errors and never starts output after a failed route save', async () => {
    const command = vi.fn().mockRejectedValue(new Error('The selected microphone is unavailable.'));
    render(<VoicePage state={fixture()} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Try this voice' }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('microphone is unavailable'));
    expect(command).toHaveBeenCalledTimes(1);
    expect(command.mock.calls[0][0]).toBe('routeSave');
  });

  it('preserves a live edit while its native acknowledgement is pending', async () => {
    let finish!: () => void;
    const command = vi.fn().mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
    const state = fixture();
    const { rerender } = render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '3.2' } });
    await waitFor(() => expect(command).toHaveBeenCalledTimes(1));
    rerender(<VoicePage state={{ ...state, settings: { ...state.settings } }} status={{ ...state.status, inputPeak: .4 }} command={command} />);
    expect((screen.getByRole('slider', { name: 'Voice pitch in semitones' }) as HTMLInputElement).value).toBe('3.2');
    finish();
  });

  it('applies tuning and mute together without an Apply button', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '2.5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Mute' }));
    expect((screen.getByRole('slider', { name: 'Voice pitch in semitones' }) as HTMLInputElement).value).toBe('2.5');
    expect(screen.queryByRole('button', { name: 'Apply adjustments' })).toBeNull();
    await waitFor(() => expect(command).toHaveBeenCalledWith('settingsSave', { settings: expect.objectContaining({ pitchSemitones: 2.5, muted: true }) }));
  });

  it('keeps cancellation available while the native model is loading', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    render(<VoicePage state={state} status={{ ...state.status, isStarting: true, busy: true, operation: 'start', message: 'Loading the reference voice…' }} command={command} />);
    const cancel = screen.getByRole('button', { name: 'Cancel start' });
    expect((cancel as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(cancel);
    await waitFor(() => expect(command).toHaveBeenCalledWith('stop'));
  });

  it('commits the reviewed trim before saving the reference voice', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ clip: { id: 'local-clip', name: 'Reference speech', durationMs: 10_000, startMs: 0, endMs: 5000,
      waveform: [{ minimum: -.5, maximum: .6 }, { minimum: -.2, maximum: .3 }], hasCleaned: false, useCleaned: false } });
    render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Your voices/ }));
    fireEvent.click(screen.getAllByRole('button', { name: 'Create reference voice' })[0]);
    await waitFor(() => expect((screen.getByRole('button', { name: 'Save reference voice' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Reference clip start in seconds' }), { target: { value: '1' } });
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Reference clip end in seconds' }), { target: { value: '6' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Reference voice name' }), { target: { value: 'My studio voice' } });
    command.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Save reference voice' }));
    await waitFor(() => expect(command).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[0]).toEqual(['clipTrim', { startMs: 1000, endMs: 6000 }]);
    expect(command.mock.calls[1]).toEqual(['clipSave', { name: 'My studio voice' }]);
  });

  it('does not treat live effects as unsaved dialog edits when changing tabs', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    render(<VoicePage state={state} status={state.status} command={command} />);
    expect(hasUnsavedChanges()).toBe(false);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '4.1' } });
    await waitFor(() => expect(command).toHaveBeenCalledWith('settingsSave', expect.anything()));
    expect(hasUnsavedChanges()).toBe(false);
    fireEvent.click(screen.getByRole('tab', { name: /^Your voices/ }));
    expect(screen.queryByRole('dialog', { name: 'Discard unsaved changes?' })).toBeNull();
    expect(screen.getByRole('tab', { name: /^Your voices/ }).getAttribute('aria-selected')).toBe('true');
  });

  it('finishes the latest live edit before selecting another saved voice', async () => {
    let finish!: () => void;
    const command = vi.fn().mockImplementation((action: string) => action === 'settingsSave' ? new Promise<void>(resolve => { finish = resolve; }) : Promise.resolve(null));
    const state = fixture({ profiles: [{ id: 'saved-voice', name: 'Warm desk voice', kind: 'effects' }] });
    render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice output gain in decibels' }), { target: { value: '5' } });
    await waitFor(() => expect(command).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByRole('combobox', { name: 'Saved voice profile' }), { target: { value: 'saved-voice' } });
    expect(command).not.toHaveBeenCalledWith('profileSelect', expect.anything());
    finish();
    await waitFor(() => expect(command).toHaveBeenCalledWith('profileSelect', { id: 'saved-voice' }));
  });

  it('coalesces rapid slider changes while native audio is busy and sends the final value', async () => {
    let finish!: () => void;
    const command = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve; })).mockResolvedValue(null);
    render(<VoicePage state={fixture()} status={null} command={command} />);
    const pitch = screen.getByRole('slider', { name: 'Voice pitch in semitones' });
    fireEvent.change(pitch, { target: { value: '1' } });
    await waitFor(() => expect(command).toHaveBeenCalledTimes(1));
    for (const value of ['2', '3', '4', '5']) fireEvent.change(pitch, { target: { value } });
    expect((pitch as HTMLInputElement).disabled).toBe(false);
    expect(command).toHaveBeenCalledTimes(1);
    finish();
    await waitFor(() => expect(command).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[1][1].settings.pitchSemitones).toBe(5);
  });

  it('offers live timbre and effects for AI reference voices too', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ models: [{ id: 'builtin', name: 'AI model', usesReference: true }], profiles: [{ id: 'reference', name: 'My voice', kind: 'reference' }], selectedProfileId: 'reference' });
    render(<VoicePage state={state} status={{ ...state.status, isRunning: true }} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Timbre' }), { target: { value: '3' } });
    await waitFor(() => expect(command).toHaveBeenCalledWith('settingsSave', { settings: expect.objectContaining({ effects: expect.objectContaining({ formantSemitones: 3 }) }) }));
    fireEvent.click(screen.getByRole('button', { name: 'Save voice settings' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('profileSaveProcessing', undefined));
    expect(command).not.toHaveBeenCalledWith('stop');
  });

  it('reports a failed live edit and retries on the next change without locking sliders', async () => {
    const command = vi.fn().mockRejectedValueOnce(new Error('Audio settings unavailable.')).mockResolvedValue(null);
    render(<VoicePage state={fixture()} status={null} command={command} />);
    const pitch = screen.getByRole('slider', { name: 'Voice pitch in semitones' });
    fireEvent.change(pitch, { target: { value: '1' } });
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Audio settings unavailable'));
    fireEvent.change(pitch, { target: { value: '2' } });
    await waitFor(() => expect(command).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[1][1].settings.pitchSemitones).toBe(2);
  });

  it('keeps a reference name and trim when clearing is cancelled', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ clip: { id: 'local-clip', name: 'Reference speech', durationMs: 10_000, startMs: 0, endMs: 5000,
      waveform: [{ minimum: -.5, maximum: .6 }], hasCleaned: false, useCleaned: false } });
    const { rerender } = render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: /^Your voices/ }));
    fireEvent.click(screen.getAllByRole('button', { name: 'Create reference voice' })[0]);
    fireEvent.change(screen.getByRole('textbox', { name: 'Reference voice name' }), { target: { value: 'Keep this draft' } });
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Reference clip start in seconds' }), { target: { value: '1.25' } });
    fireEvent.click(screen.getByRole('button', { name: 'Clear editor' }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    rerender(<VoicePage state={{ ...state, clip: { ...state.clip } }} status={{ ...state.status, clipPositionMs: 2000 }} command={command} />);
    expect((screen.getByRole('textbox', { name: 'Reference voice name' }) as HTMLInputElement).value).toBe('Keep this draft');
    expect((screen.getByRole('spinbutton', { name: 'Reference clip start in seconds' }) as HTMLInputElement).value).toBe('1.25');
    expect(command).not.toHaveBeenCalledWith('clipClear', undefined);
    expect(hasUnsavedChanges()).toBe(true);
  });

  it('registers name dialog edits but does not mark live mute as an unsaved draft', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ settings: { ...defaultEffects(), muted: true } });
    render(<VoicePage state={state} status={state.status} command={command} />);
    expect(hasUnsavedChanges()).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Save voice settings' }));
    expect(hasUnsavedChanges()).toBe(false);
    fireEvent.change(screen.getByRole('textbox', { name: 'Effects voice name' }), { target: { value: 'A name I typed' } });
    await waitFor(() => expect(hasUnsavedChanges()).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByRole('textbox', { name: 'Effects voice name' }) as HTMLInputElement).value).toBe('A name I typed');
  });
});

function defaultEffects() { return { preset: 'clean', pitchSemitones: 0, gainDb: 0 }; }
