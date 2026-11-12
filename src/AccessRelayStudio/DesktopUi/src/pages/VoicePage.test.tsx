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

  it('saves the explicit native route before requesting a preview', async () => {
    const command = vi.fn().mockResolvedValue(null);
    render(<VoicePage state={fixture()} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Try this voice' }));
    await waitFor(() => expect(command).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[0]).toEqual(['routeSave', { microphoneId: 'mic-one', outputId: 'phones-one', destination: 'headphones' }]);
    expect(command.mock.calls[1]).toEqual(['start']);
  });

  it('shows route errors and never starts output after a failed route save', async () => {
    const command = vi.fn().mockRejectedValue(new Error('The selected microphone is unavailable.'));
    render(<VoicePage state={fixture()} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Try this voice' }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('microphone is unavailable'));
    expect(command).toHaveBeenCalledTimes(1);
    expect(command.mock.calls[0][0]).toBe('routeSave');
  });

  it('preserves unapplied tuning while native meter status refreshes', () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    const { rerender } = render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '3.2' } });
    rerender(<VoicePage state={{ ...state, settings: { ...state.settings } }} status={{ ...state.status, inputPeak: .4 }} command={command} />);
    expect((screen.getByRole('slider', { name: 'Voice pitch in semitones' }) as HTMLInputElement).value).toBe('3.2');
    expect(command).not.toHaveBeenCalled();
  });

  it('keeps unapplied tuning when mute changes and applies the current mute state', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    const { rerender } = render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '2.5' } });
    rerender(<VoicePage state={{ ...state, settings: { ...state.settings, muted: true } }} status={state.status} command={command} />);
    expect((screen.getByRole('slider', { name: 'Voice pitch in semitones' }) as HTMLInputElement).value).toBe('2.5');
    fireEvent.click(screen.getByRole('button', { name: 'Apply adjustments' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('settingsSave', { settings: { ...state.settings, pitchSemitones: 2.5, muted: true } }));
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

  it('keeps unapplied effects through Keep editing and native status refreshes', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture();
    const { rerender } = render(<VoicePage state={state} status={state.status} command={command} />);
    expect(hasUnsavedChanges()).toBe(false);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice pitch in semitones' }), { target: { value: '4.1' } });
    await waitFor(() => expect(hasUnsavedChanges()).toBe(true));
    fireEvent.click(screen.getByRole('tab', { name: /^Your voices/ }));
    expect(screen.getByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    rerender(<VoicePage state={{ ...state }} status={{ ...state.status, inputPeak: .7 }} command={command} />);
    expect((screen.getByRole('slider', { name: 'Voice pitch in semitones' }) as HTMLInputElement).value).toBe('4.1');
    expect(screen.getByRole('tab', { name: 'Live voice' }).getAttribute('aria-selected')).toBe('true');
    expect(command).not.toHaveBeenCalled();
  });

  it('does not select another saved voice until effects changes are discarded', async () => {
    const command = vi.fn().mockResolvedValue(null);
    const state = fixture({ profiles: [{ id: 'saved-voice', name: 'Warm desk voice', kind: 'effects' }] });
    render(<VoicePage state={state} status={state.status} command={command} />);
    fireEvent.change(screen.getByRole('slider', { name: 'Voice output gain in decibels' }), { target: { value: '5' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Saved voice profile' }), { target: { value: 'saved-voice' } });
    expect(command).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect((screen.getByRole('slider', { name: 'Voice output gain in decibels' }) as HTMLInputElement).value).toBe('5');
    fireEvent.change(screen.getByRole('combobox', { name: 'Saved voice profile' }), { target: { value: 'saved-voice' } });
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('profileSelect', { id: 'saved-voice' }));
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
