import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { WorkspacePage } from './WorkspacePage';
import { previewState } from '../api';

afterEach(cleanup);
function fixture() {
  const state = structuredClone(previewState);
  state.voice.microphoneId = 'mic'; state.voice.devices.microphones = [{ id: 'mic', name: 'Test microphone' }];
  state.vision.selectedProfile = { id: 'shot' }; state.vision.selectedSourceId = 'window';
  const status = { controller: { ...state.controller.status, connected: true }, voice: { ...state.voice.status }, vision: {} as any, system: {} as any };
  return { state, status, command: vi.fn().mockResolvedValue(null), navigate: vi.fn() };
}

describe('dashboard controls and resource use', () => {
  it('opens without starting features and removes the requested filler', () => {
    const props = fixture(); render(<WorkspacePage {...props} />);
    expect(props.command).not.toHaveBeenCalled();
    for (const name of ['Controller output', 'Remote Play capture', 'Voice processing']) expect(screen.getByRole('button', { name, exact: true }).getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByText('Everything has its own space.')).toBeNull();
    expect(screen.queryByText('Your PC is the workspace.')).toBeNull();
  });
  it('starts the chosen feature with saved settings and never arms vision or enables scripts', async () => {
    const props = fixture(); render(<WorkspacePage {...props} />);
    fireEvent.click(screen.getByRole('button', { name: 'Controller output', exact: true }));
    await waitFor(() => expect(props.command).toHaveBeenCalledWith('controller.outputStart', undefined));
    fireEvent.click(screen.getByRole('button', { name: 'Remote Play capture', exact: true }));
    await waitFor(() => expect(props.command).toHaveBeenCalledWith('vision.start', { loadModel: false }));
    fireEvent.click(screen.getByRole('button', { name: 'Voice processing', exact: true }));
    await waitFor(() => expect(props.command).toHaveBeenCalledWith('voice.start', undefined));
    expect(props.command.mock.calls.map(([method]) => method).sort()).toEqual(['controller.outputStart', 'vision.start', 'voice.start']);
  });
  it('stops live features even if their original devices and source are unavailable', async () => {
    const props = fixture(); props.status.controller = { ...props.status.controller, connected: false, outputActive: true };
    props.state.vision.selectedSourceId = ''; props.status.vision = { session: { running: true }, training: { isRecording: true } };
    props.state.voice.devices.microphones = []; props.status.voice = { ...props.status.voice, isRunning: true };
    render(<WorkspacePage {...props} />);
    for (const name of ['Controller output', 'Remote Play capture', 'Voice processing']) fireEvent.click(screen.getByRole('button', { name, exact: true }));
    await waitFor(() => expect(props.command.mock.calls.map(([method]) => method).sort()).toEqual(['controller.outputStop', 'vision.stop', 'voice.stop']));
  });
  it('keeps cancellation available while voice startup is pending', async () => {
    const props = fixture(); let finish!: () => void;
    const starting = new Promise<void>(resolve => { finish = resolve; });
    props.command.mockImplementation((method: string) => method === 'voice.start' ? starting : Promise.resolve(null));
    render(<WorkspacePage {...props} />);
    const toggle = screen.getByRole('button', { name: 'Voice processing', exact: true });
    fireEvent.click(toggle); expect(toggle.textContent).toContain('Cancel'); expect((toggle as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(toggle); await waitFor(() => expect(props.command).toHaveBeenCalledWith('voice.stop', undefined));
    await act(async () => { finish(); await starting; });
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
  });
  it('explains incomplete setup and keeps closed sources from starting capture', () => {
    const props = fixture(); props.status.controller.connected = false; props.status.vision.selectedSourceId = ''; props.state.voice.devices.microphones = [];
    render(<WorkspacePage {...props} />);
    for (const name of ['Controller output', 'Remote Play capture', 'Voice processing']) expect((screen.getByRole('button', { name, exact: true }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Connect a gamepad in Controller.')).toBeTruthy();
    expect(screen.getByText('Choose a shot profile and window in Remote Play.')).toBeTruthy();
    expect(screen.getByText('Choose a microphone in Voice.')).toBeTruthy();
  });
  it('turns off native sampling, waits for confirmed state, and hides the readings', async () => {
    const props = fixture(); const { rerender } = render(<WorkspacePage {...props} />);
    fireEvent.click(screen.getByRole('button', { name: 'Resource monitoring' }));
    await waitFor(() => expect(props.command).toHaveBeenCalledWith('app.resourceMonitoringSet', { enabled: false }));
    props.state.settings.resourceMonitoringEnabled = false; rerender(<WorkspacePage {...props} />);
    expect(screen.getByRole('button', { name: 'Resource monitoring' }).getAttribute('aria-pressed')).toBe('false');
    expect(screen.getByText('Monitoring is off. Resource polling and GPU counters are stopped.')).toBeTruthy();
    expect(screen.queryByText('System RAM')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Resource monitoring' }));
    await waitFor(() => expect(props.command).toHaveBeenCalledWith('app.resourceMonitoringSet', { enabled: true }));
  });
  it('shows system and host usage, bounded trends and capacity without inventing unavailable readings', () => {
    const props = fixture(); props.status.system = { cpuPercent: 12.5, memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 16 * 1024 ** 3,
      gpuPercent: null, appCpuPercent: 1.5, appMemoryBytes: 256 * 1024 ** 2, appPrivateBytes: 300 * 1024 ** 2, appThreadCount: 24,
      logicalProcessors: 8, sampledAtUtc: '2026-11-12T13:00:00Z', history: [{ cpuPercent: 10 }, { cpuPercent: 12.5 }] };
    render(<WorkspacePage {...props} />);
    expect(screen.getByText('8.00 GiB')).toBeTruthy(); expect(screen.getByText('256 MiB')).toBeTruthy();
    expect(screen.getByRole('meter', { name: 'System RAM used' }).getAttribute('aria-valuenow')).toBe('50');
    expect(screen.getByText('Average 11.3% · Peak 12.5%')).toBeTruthy();
    expect(screen.getByText('8 logical processors')).toBeTruthy();
    expect(screen.queryByRole('meter', { name: 'Video memory used' })).toBeNull();
  });
});
