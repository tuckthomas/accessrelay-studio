// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Nba2K27VisionWorkspace as VisionPage } from '../game-plugins/nba2k27/VisionWorkspace';
import { hasUnsavedChanges } from '../ui';

afterEach(cleanup);
const all = { left: 0, top: 0, width: 1, height: 1 };
const profile = { id: '4b904b4a-c48f-46e6-aa49-cd09637f2c9b', name: 'Stretch Big', heightInches: 78, threePointRating: 84,
  base: 'Current base', upperRelease1: 'Current upper one', upperRelease2: 'Current upper two', usesShotMeter: true,
  meterReleaseType: 'Reviewed cue', shotSpeed: 4, release1Percent: 50, cameraView: '2K' };
function fixture(overrides: any = {}) {
  return { profiles: [profile], selectedProfile: profile, selectedFingerprint: 'native-profile-fingerprint',
    sources: [{ id: 'window-one', title: 'Xbox Remote Play', processId: 100 }, { id: 'window-two', title: 'Another Remote Play window', processId: 200 }],
    selectedSourceId: 'window-one', models: [], modelScanComplete: true,
    settings: { executionProvider: 'cuda', shootButton: 'x', minimumConfidence: .9, crop: all, trainingCapture: {
      configured: false, sourceWidth: 1920, sourceHeight: 1080, gameplayRegion: all, meterRegion: { left: .1, top: .1, width: .1, height: .3 },
      feedbackRegion: { left: .1, top: .1, width: .4, height: .1 }, purpose: 'meterSupervisedTraining', hudName: 'Prior HUD', gameBuild: '', shotCategory: 'Mixed / unclassified' } },
    status: { session: null, stopping: false, training: null, notice: 'Choose your source.' }, ...overrides };
}

describe('vision workflows', () => {
  it('opens and changes workspaces without starting capture or controller output', () => {
    const command = vi.fn().mockResolvedValue(null);
    render(<VisionPage state={fixture()} status={null} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Assistance' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Clips & review' }));
    expect(command).not.toHaveBeenCalled();
  });

  it('keeps recording and its state in the main preview toolbar before any sidebar scrolling', () => {
    const command = vi.fn().mockResolvedValue(null), state = fixture();
    const view = render(<VisionPage state={state} status={null} command={command} />);
    const record = screen.getByRole('button', { name: 'Record training clip' }) as HTMLButtonElement;
    expect(record.closest('.vision-preview-head')).toBeTruthy();
    expect(record.disabled).toBe(true);
    expect(record.getAttribute('title')).toBe('Mark capture areas before recording.');
    expect(document.getElementById(record.getAttribute('aria-describedby')!)?.textContent).toBe('Mark capture areas before recording.');
    view.rerender(<VisionPage state={state} status={{ session: { running: true }, busy: true,
      training: { hasTake: true, isRecording: true, isFinalized: false, storedFrames: 42 } }} command={command} />);
    const stop = screen.getByRole('button', { name: 'Stop training clip recording' }) as HTMLButtonElement;
    expect(stop.closest('.vision-preview-head')).toBeTruthy();
    expect(stop.disabled).toBe(false);
    expect(screen.getByText('Recording · 42 frames written')).toBeTruthy();
  });

  it('retains a native-validated source selection and requires fresh layout confirmation before recording', async () => {
    const command = vi.fn().mockResolvedValue(null), state = fixture();
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.change(screen.getByRole('combobox', { name: 'Remote Play window' }), { target: { value: 'window-two' } });
    await waitFor(() => expect(command).toHaveBeenCalledWith('sourceSelect', { id: 'window-two' }));
    view.rerender(<VisionPage state={{ ...state, selectedSourceId: 'window-two', settings: { ...state.settings,
      trainingCapture: { ...state.settings.trainingCapture, configured: false } } }} status={state.status} command={command} />);
    expect((screen.getByRole('button', { name: 'Record a clip' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Mark capture areas' }) as HTMLButtonElement).disabled).toBe(false);
    expect(command.mock.calls.some(([action]) => ['start', 'recordStart', 'connectOutput', 'armedSet'].includes(action))).toBe(false);
  });

  it('keeps the source picker usable while refreshing its window list on focus', () => {
    const command = vi.fn(() => new Promise(() => {}));
    render(<VisionPage state={fixture()} status={null} command={command} />);
    const picker = screen.getByRole('combobox', { name: 'Remote Play window' }) as HTMLSelectElement;
    fireEvent.focus(picker);
    expect(command).toHaveBeenCalledWith('sourceRefresh');
    expect(picker.disabled).toBe(false);
  });

  it('immediately reflects a closed source and invalidated layout from status without waiting for a full snapshot', () => {
    const state = fixture(); state.settings.trainingCapture.configured = true;
    const command = vi.fn().mockResolvedValue(null);
    const view = render(<VisionPage state={state} status={null} command={command} />);
    expect((screen.getByRole('button', { name: 'Record training clip' }) as HTMLButtonElement).disabled).toBe(false);
    view.rerender(<VisionPage state={state} status={{ selectedSourceId: '', sources: [state.sources[1]], captureLayoutConfirmed: false,
      notice: 'The selected window closed.' }} command={command} />);
    expect((screen.getByRole('combobox', { name: 'Remote Play window' }) as HTMLSelectElement).value).toBe('');
    expect(screen.queryByRole('option', { name: 'Xbox Remote Play' })).toBeNull();
    expect(screen.getByRole('option', { name: 'Another Remote Play window' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Record training clip' }) as HTMLButtonElement).disabled).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });

  it('lets height be cleared and reentered and keeps release blending complementary in saved profile data', async () => {
    const command = vi.fn().mockResolvedValue(profile);
    render(<VisionPage state={fixture()} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit shot setup' });
    fireEvent.change(within(dialog).getByRole('spinbutton', { name: 'Height feet' }), { target: { value: '' } });
    fireEvent.change(within(dialog).getByRole('spinbutton', { name: 'Height feet' }), { target: { value: '6' } });
    fireEvent.change(within(dialog).getByRole('spinbutton', { name: 'Height inches' }), { target: { value: '10' } });
    fireEvent.change(within(dialog).getByRole('slider', { name: 'Upper release 1 allocation' }), { target: { value: '35' } });
    expect(within(dialog).getByText('35% release 1')).toBeTruthy();
    expect(within(dialog).getByText('65% release 2')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save shot setup' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('profileSave', expect.objectContaining({ expectedFingerprint: 'native-profile-fingerprint',
      profile: expect.objectContaining({ id: profile.id, heightInches: 82, release1Percent: 35, upperRelease1: profile.upperRelease1, upperRelease2: profile.upperRelease2, usesShotMeter: true }) })));
  });

  it('does not silently reconfirm retained meter and feedback boxes after source or resolution invalidation', async () => {
    const state = fixture();
    const frozen = { token: 'frozen-frame', url: 'https://media.accessrelay.test/vision/frozen/frozen-frame', sourceWidth: 2560,
      sourceHeight: 1440, player: all, layout: state.settings.trainingCapture };
    const command = vi.fn(async (action: string) => action === 'freeze' ? frozen : null);
    render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Mark capture areas' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose what your clip keeps' });
    const save = within(dialog).getByRole('button', { name: 'Save capture areas' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    fireEvent.click(within(dialog).getByText('Keyboard / precise coordinates'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Use these coordinates' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Mark Player area' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Use these coordinates' }));
    expect(save.disabled).toBe(true);
    expect(within(dialog).getByText('2 of 4 areas marked')).toBeTruthy();
    for (const area of ['Shot meter', 'Shot feedback']) {
      fireEvent.click(within(dialog).getByRole('button', { name: `Mark ${area} area` }));
      fireEvent.click(within(dialog).getByRole('button', { name: 'Use these coordinates' }));
    }
    expect(save.disabled).toBe(false);
    fireEvent.click(save);
    await waitFor(() => expect(command).toHaveBeenCalledWith('layoutSave', expect.objectContaining({ token: 'frozen-frame',
      layout: expect.objectContaining({ sourceWidth: 2560, sourceHeight: 1440, configured: true, meterRegion: state.settings.trainingCapture.meterRegion,
        feedbackRegion: state.settings.trainingCapture.feedbackRegion }), player: all })));
  });

  it('preserves partially marked capture areas and HUD text when the user keeps editing', async () => {
    const state = fixture(), frozen = { token: 'draft-layout', url: 'https://media.accessrelay.test/vision/frozen/draft-layout',
      sourceWidth: 2560, sourceHeight: 1440, player: all, layout: state.settings.trainingCapture };
    const command = vi.fn(async (action: string) => action === 'freeze' ? frozen : null);
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Mark capture areas' }));
    const editor = await screen.findByRole('dialog', { name: 'Choose what your clip keeps' });
    fireEvent.change(within(editor).getByRole('textbox', { name: 'HUD layout name' }), { target: { value: '  My unfinished HUD  ' } });
    fireEvent.click(within(editor).getByText('Keyboard / precise coordinates'));
    fireEvent.click(within(editor).getByRole('button', { name: 'Use these coordinates' }));
    expect(within(editor).getByText('1 of 4 areas marked')).toBeTruthy();
    fireEvent.click(within(editor).getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    view.rerender(<VisionPage state={{ ...state, settings: { ...state.settings } }} status={{ session: null, busy: false }} command={command} />);
    expect((within(editor).getByRole('textbox', { name: 'HUD layout name' }) as HTMLInputElement).value).toBe('  My unfinished HUD  ');
    expect(within(editor).getByText('1 of 4 areas marked')).toBeTruthy();
    expect(hasUnsavedChanges()).toBe(true);
    expect(command).toHaveBeenCalledTimes(1);
  });

  it('keeps unapplied analysis changes through native status refreshes and disallows unvalidated arming', () => {
    const command = vi.fn().mockResolvedValue(null), state = fixture();
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Assistance' }));
    fireEvent.click(screen.getByText('Analysis settings & measured calibration'));
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Estimated stream delay (ms)' }), { target: { value: '120' } });
    view.rerender(<VisionPage state={{ ...state, settings: { ...state.settings } }} status={{ session: { running: true, canArm: false }, output: { armed: false }, performance: { totalDecisions: 23 } }} command={command} />);
    expect((screen.getByRole('spinbutton', { name: 'Estimated stream delay (ms)' }) as HTMLInputElement).value).toBe('120');
    expect((screen.getByRole('checkbox', { name: /Automatic release/ }) as HTMLInputElement).disabled).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });

  it('keeps the exact unsaved shot setup text when closing is declined and native status refreshes', async () => {
    const state = fixture(), command = vi.fn().mockResolvedValue(null);
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const editor = screen.getByRole('dialog', { name: 'Edit shot setup' });
    const name = within(editor).getByRole('textbox', { name: 'Shot setup name' }) as HTMLInputElement;
    const exact = '  My unfinished setup — keep this  ';
    fireEvent.change(name, { target: { value: exact } });
    expect(hasUnsavedChanges()).toBe(true);
    fireEvent.click(within(editor).getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    view.rerender(<VisionPage state={{ ...state, selectedProfile: { ...state.selectedProfile } }} status={{ session: null, busy: false, notice: 'Device status refreshed.' }} command={command} />);
    expect((within(editor).getByRole('textbox', { name: 'Shot setup name' }) as HTMLInputElement).value).toBe(exact);
    expect(hasUnsavedChanges()).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });

  it('guards internal navigation and preserves analysis edits even when the source closes in a status update', async () => {
    const state = fixture(), command = vi.fn().mockResolvedValue(null);
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Assistance' }));
    fireEvent.click(screen.getByText('Analysis settings & measured calibration'));
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Estimated stream delay (ms)' }), { target: { value: '137' } });
    fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
    expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    view.rerender(<VisionPage state={{ ...state, settings: { ...state.settings } }} status={{ selectedSourceId: '', sources: [], captureLayoutConfirmed: false, notice: 'The selected window closed.' }} command={command} />);
    expect((screen.getByRole('tab', { name: 'Assistance' })).getAttribute('aria-selected')).toBe('true');
    expect((screen.getByRole('spinbutton', { name: 'Estimated stream delay (ms)' }) as HTMLInputElement).value).toBe('137');
    expect(hasUnsavedChanges()).toBe(true);
    fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Discard changes' }));
    expect(screen.getByRole('tab', { name: 'Capture' }).getAttribute('aria-selected')).toBe('true');
    expect(hasUnsavedChanges()).toBe(false);
    expect(command).not.toHaveBeenCalled();
  });

  it('keeps a partially reviewed label when a new interval is declined and native clip status refreshes', async () => {
    const review = { token: 'draft-clip', sessionId: 'draft-session', frameCount: 3, enriched: true,
      regions: [{ name: 'player', label: 'Player' }], frames: [{ ticks: 1000, timeMs: 0 }, { ticks: 1100, timeMs: 10 }, { ticks: 1200, timeMs: 20 }],
      labels: { schemaVersion: 1, sessionId: 'draft-session', split: 'train', shots: [] } };
    const state = fixture({ review }), command = vi.fn().mockResolvedValue(null);
    const view = render(<VisionPage state={state} status={null} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Clips & review' }));
    const name = screen.getByRole('textbox', { name: 'Shot name' }) as HTMLInputElement;
    fireEvent.change(name, { target: { value: '  Needs another look  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'New interval' }));
    expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    view.rerender(<VisionPage state={{ ...state, review: { ...review, labels: { ...review.labels } } }} status={{ session: null, notice: 'Clip status refreshed.' }} command={command} />);
    expect((screen.getByRole('textbox', { name: 'Shot name' }) as HTMLInputElement).value).toBe('  Needs another look  ');
    expect(hasUnsavedChanges()).toBe(true);
    expect(command).not.toHaveBeenCalled();
  });

  it('labels the loaded captured game phase rather than controller release or a pending slider frame', async () => {
    const review = { token: 'review-token', sessionId: 'captured-session', frameCount: 6, enriched: true,
      regions: [{ name: 'player', label: 'Player' }, { name: 'meter', label: 'Meter' }, { name: 'feedback', label: 'Shot feedback' }],
      frames: Array.from({ length: 6 }, (_, index) => ({ ticks: 1000 + index * 100, sequence: index + 1, timeMs: index * 10 })),
      labels: { schemaVersion: 1, sessionId: 'captured-session', split: 'train', shots: [] }, physicalReleaseTicks: 8888 };
    const command = vi.fn().mockResolvedValue(review);
    render(<VisionPage state={fixture({ review })} status={null} command={command} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Clips & review' }));
    const target = screen.getByRole('button', { name: 'Mark target game phase at current frame' }) as HTMLButtonElement;
    expect(target.disabled).toBe(true);
    fireEvent.load(screen.getByRole('img', { name: 'player capture, frame 1' }));
    expect(target.disabled).toBe(false);
    fireEvent.change(screen.getByRole('slider', { name: 'Recorded clip frame' }), { target: { value: '3' } });
    expect(target.disabled).toBe(true);
    const image = await screen.findByRole('img', { name: 'player capture, frame 4' });
    fireEvent.load(image); fireEvent.click(target);
    fireEvent.click(screen.getByLabelText('I confirmed my player throughout this interval.'));
    fireEvent.click(screen.getByLabelText('I reviewed this interval and its target evidence.'));
    fireEvent.click(screen.getByRole('button', { name: 'Save reviewed shot' }));
    await waitFor(() => expect(command).toHaveBeenCalledWith('reviewSave', { token: 'review-token', labels: expect.objectContaining({
      sessionId: 'captured-session', labelTimeBasis: 'captured-target-game-phase', shots: [expect.objectContaining({ startTicks: 1000,
        endTicks: 1500, targetReleaseTicks: 1300, playerIdentityConfirmed: true, reviewed: true })] }) }));
    expect(command.mock.calls.some(([action]) => ['start', 'recordStart', 'armedSet'].includes(action))).toBe(false);
  });
});
