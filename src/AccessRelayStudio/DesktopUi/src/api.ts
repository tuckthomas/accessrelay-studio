export interface SystemStatus { cpuPercent?: number | null; memoryUsedBytes?: number | null; memoryTotalBytes?: number | null;
  gpuPercent?: number | null; vramUsedBytes?: number | null; vramTotalBytes?: number | null; gpuName?: string | null }
export interface StudioState { version: string; controller: any; vision: any; voice: any; settings: any; system?: SystemStatus }
export interface StudioStatus { controller?: any; vision?: any; voice?: any; system?: SystemStatus }
type HostMessage = { type: 'snapshot'; state: StudioState } | { type: 'status'; status: StudioStatus }
  | { type: 'closeRequested' }
  | { type: 'response'; id: string; ok: boolean; result?: any; error?: string };
type WebView = { postMessage: (value: any) => void; addEventListener: (name: string, handler: (event: MessageEvent) => void) => void;
  removeEventListener: (name: string, handler: (event: MessageEvent) => void) => void };
declare global { interface Window { chrome?: { webview?: WebView } } }
const getWebView = () => window.chrome?.webview;
export const isDesignPreview = !getWebView() && import.meta.env.DEV;

export class DesktopBridge {
  private pending = new Map<string, { resolve: (value: any) => void; reject: (reason: Error) => void; timer: number }>();
  private listeners = new Set<(message: HostMessage) => void>();
  private attached = false;
  private onMessage = (event: MessageEvent) => {
    const message = event.data as HostMessage;
    if (!message || typeof message !== 'object') return;
    if (message.type === 'response') {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      window.clearTimeout(pending.timer); this.pending.delete(message.id);
      if (message.ok) pending.resolve(message.result); else pending.reject(new Error(message.error || 'The action could not be completed.'));
    } else if (message.type === 'snapshot' || message.type === 'status' || message.type === 'closeRequested') this.listeners.forEach(listener => listener(message));
  };
  subscribe(listener: (message: HostMessage) => void) {
    this.listeners.add(listener);
    if (!this.attached && getWebView()) { getWebView()!.addEventListener('message', this.onMessage); this.attached = true; }
    return () => { this.listeners.delete(listener); };
  }
  async command(method: string, params: any = {}): Promise<any> {
    const webview = getWebView();
    if (!webview) {
      if (!isDesignPreview) throw new Error('The desktop connection is unavailable. Close this window and reopen AccessRelay Studio.');
      const result = await previewCommand(method, params);
      if (method !== 'app.bootstrap') this.listeners.forEach(listener => listener({ type: 'snapshot', state: structuredClone(previewState) }));
      return result;
    }
    if (!this.attached) { webview.addEventListener('message', this.onMessage); this.attached = true; }
    const id = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => { this.pending.delete(id); reject(new Error('This action is taking too long. Check its status before trying again.')); }, 180_000);
      this.pending.set(id, { resolve, reject, timer });
      try { webview.postMessage({ protocol: 1, id, method, params }); }
      catch (error) { window.clearTimeout(timer); this.pending.delete(id); reject(error); }
    });
  }
  dispose() {
    if (this.attached) getWebView()?.removeEventListener('message', this.onMessage);
    this.pending.forEach(item => { window.clearTimeout(item.timer); item.reject(new Error('The desktop window closed.')); });
    this.pending.clear(); this.listeners.clear(); this.attached = false;
  }
}
export const desktop = new DesktopBridge();

const timing = { startDelay: 0, downSweep: 0, downHold: 512, upSweep: 80, upHold: 48, centerHold: 120, flick: 64, settle: 240, flickX: 0, flickY: -32767 };
const controls = [
  ['a', 'A button'], ['b', 'B button'], ['x', 'X button'], ['y', 'Y button'], ['leftBumper', 'Left bumper'],
  ['rightBumper', 'Right bumper'], ['leftTrigger', 'Left trigger'], ['rightTrigger', 'Right trigger'],
  ['leftStick', 'Left stick click'], ['rightStick', 'Right stick click'], ['dPadUp', 'D-pad up'],
  ['dPadDown', 'D-pad down'], ['dPadLeft', 'D-pad left'], ['dPadRight', 'D-pad right'],
  ['menu', 'Menu'], ['view', 'View'], ['smallLeft', 'Small left paddle'], ['smallRight', 'Small right paddle'],
  ['largeLeft', 'Large left paddle'], ['largeRight', 'Large right paddle']
].map(([id, label]) => ({ id, label }));
const scripts = [['passthrough', 'Pass through'], ['rhythm', 'Timed stick sequence'], ['intentionalMiss', 'Alternating stick sequence'],
  ['moving', 'Moving stick sequence'], ['stepback', 'Two-stage stick sequence']].map(([id, label]) => ({ id, label }));
const profileId = '420113cb-0585-4a35-af51-0425196c008f';
const disconnected = { connected: false, deviceName: 'No controller selected', input: {}, output: {}, outputActive: false,
  scriptsEnabled: false, outputStatus: 'Virtual controller is off', recording: { isRecording: false, hasInput: false, elapsedMs: 0, pending: null },
  meanPollMs: null, p95PollMs: null, p99PollMs: null };
export const previewState: StudioState = {
  version: 'Desktop design preview',
  controller: {
    profiles: [{ id: profileId, name: 'Everyday setup' }],
    activeProfile: { id: profileId, name: 'Everyday setup',
      bindings: controls.map(control => ({ control: control.id, label: control.label, script: control.id === 'largeRight' ? 'rhythm' : 'passthrough', enabled: true, consume: false })),
      timings: ['rhythm', 'missEarly', 'missLate', 'moving', 'stepback'].map(variant => ({ variant, label: variant === 'rhythm' ? 'Timed sequence' : variant, timing: { ...timing } })),
      recordings: [], luaScripts: []
    },
    devices: [], selectedDeviceId: '', deadzone: { leftPercent: 5, rightPercent: 5 }, controls, scripts,
    recordingChannels: [['rightStick', 'Right stick'], ['leftStick', 'Left stick'], ['buttons', 'Buttons'], ['leftTrigger', 'Left trigger'], ['rightTrigger', 'Right trigger']].map(([id, label]) => ({ id, label })),
    driverAvailable: true, profileDirectory: 'Local profile storage', status: disconnected
  },
  vision: { sources: [], profiles: [], shotProfiles: [], models: [], selectedProfileId: null,
    selectedShotProfileId: null, selectedSource: null, settings: {}, status: { running: false, message: 'Choose your Remote Play window to begin.' } },
  voice: { devices: { microphones: [], outputs: [] }, models: [], profiles: [], selectedProfileId: null, selectedModelId: null,
    microphoneId: '', outputId: '', destination: 'virtual', settings: { preset: 'clean', pitchSemitones: 0, gainDb: 0, muted: false },
    musicRemovalAvailable: false, profileDirectory: 'Local voice profile storage', clip: null,
    status: { isRunning: false, isStarting: false, busy: false, message: 'Voice processing is off.', inputPeak: 0, outputPeak: 0, bufferedMilliseconds: 0 } },
  settings: { driverAvailable: true, profileDirectory: 'Local profile storage', appDirectory: 'Installed application folder', platform: 'Windows x64' }
};
async function previewCommand(method: string, args: any): Promise<any> {
  await new Promise(resolve => setTimeout(resolve, 80));
  const controller = previewState.controller;
  const action = method.split('.')[1];
  if (method === 'app.bootstrap') return structuredClone(previewState);
  if (method === 'app.page') return null;
  if (method === 'app.draftState') return null;
  if (method.startsWith('controller.')) {
    if (action === 'profileCreate' || action === 'profileDuplicate') {
      const id = crypto.randomUUID(), name = args.name || 'New setup';
      controller.profiles.push({ id, name }); controller.activeProfile = { ...structuredClone(controller.activeProfile), id, name };
    } else if (action === 'profileSelect') {
      const profile = controller.profiles.find((item: any) => item.id === args.id);
      if (!profile) throw new Error('That setup is unavailable.');
      controller.activeProfile = { ...structuredClone(controller.activeProfile), ...profile };
    } else if (action === 'profileRename') {
      controller.activeProfile.name = args.name; controller.profiles.find((profile: any) => profile.id === controller.activeProfile.id).name = args.name;
    } else if (action === 'bindingSave') {
      const index = controller.activeProfile.bindings.findIndex((binding: any) => binding.control === args.control);
      controller.activeProfile.bindings[index] = { ...controller.activeProfile.bindings[index], ...args };
    } else if (action === 'timingSave') {
      controller.activeProfile.timings.find((item: any) => item.variant === args.variant).timing = args.timing;
    } else if (action === 'navigationSet') {
      if (args.enabled) throw new Error('Controller switches need the installed app. Choose keyboard switches for the design preview.');
    } else if (action === 'stickySave') controller.activeProfile.stickyControls = args.controls;
    else if (action === 'smoothingSave') controller.activeProfile.smoothing = args.settings;
    else if (action === 'feedbackSave') controller.feedback = args.settings;
    else if (action === 'feedbackTest') return { queued: false, message: 'Design preview only. Run the installed app to hear or feel feedback.' };
    else if (action === 'deadzoneSave') controller.deadzone = args;
    else if (action === 'luaValidate') return { valid: true, message: 'Design preview only. Run the desktop app to validate Lua.' };
    else if (action === 'luaGet') return controller.activeProfile.luaScripts.find((script: any) => script.id === args.id);
    else if (action === 'luaSave') {
      const script = { ...args.script, id: args.script.id || crypto.randomUUID() };
      controller.activeProfile.luaScripts = [...controller.activeProfile.luaScripts.filter((item: any) => item.id !== script.id), script];
      return script;
    } else if (action === 'luaDelete') controller.activeProfile.luaScripts = controller.activeProfile.luaScripts.filter((item: any) => item.id !== args.id);
    else if (action === 'trajectory') return { points: Array.from({ length: 101 }, (_, index) => ({ timeMs: index * 8, x: 0, y: index < 64 ? -1 : index < 74 ? -1 + (index - 64) / 5 : index < 82 ? 1 : 0 })), durationMs: 800 };
    else if (['outputStart', 'scriptsSet', 'recordStart', 'deviceSelect'].includes(action)) throw new Error('This is a design preview. Device actions are available in the installed desktop app.');
    return null;
  }
  if (method.startsWith('app.')) throw new Error('This is a design preview. System actions are available in the installed desktop app.');
  throw new Error('This is a design preview. Capture and audio actions are available in the installed desktop app.');
}
