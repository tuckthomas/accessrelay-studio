// Native integration verification. Refuses normal user-data sessions before any mutation.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { createRequire } = require('node:module');
const { spawn } = require('node:child_process');
const frontendRequire = createRequire(path.resolve(__dirname, '../../src/AccessRelayStudio/DesktopUi/package.json'));
const { chromium } = frontendRequire('@playwright/test');
const argument = (name, fallback = '') => { const index = process.argv.indexOf(name); return index >= 0 ? process.argv[index + 1] : fallback; };
const endpoint = argument('--endpoint', 'http://127.0.0.1:9339');
const dataRoot = path.resolve(argument('--data-root'));
const sourceHelper = argument('--source-helper');
assert(argument('--data-root'), 'Provide the isolated verification data root explicitly.');
assert(['127.0.0.1', 'localhost'].includes(new URL(endpoint).hostname), 'Only a local verification host is supported.');

(async () => {
  const browser = await chromium.connectOverCDP(endpoint);
  const page = browser.contexts().flatMap(context => context.pages()).find(page => page.url() === 'https://studio.local/index.html');
  assert(page, 'The local native desktop interface is not open.');
  let safeSession = false, sourceProcess;
  const passed = [];
  const command = (method, params = {}) => page.evaluate(({ method, params }) => new Promise((resolve, reject) => {
    const bridge = window.chrome?.webview;
    if (!bridge) { reject(new Error('The native bridge is unavailable.')); return; }
    const id = 'verification-' + crypto.randomUUID();
    const timeout = setTimeout(() => { bridge.removeEventListener('message', receive); reject(new Error(method + ' timed out.')); }, 20000);
    function receive(event) {
      const value = event.data;
      if (value?.type !== 'response' || value.id !== id) return;
      clearTimeout(timeout); bridge.removeEventListener('message', receive);
      value.ok ? resolve(value.result) : reject(new Error(value.error));
    }
    bridge.addEventListener('message', receive);
    bridge.postMessage({ protocol: 1, id, method, params });
  }), { method, params });
  const snapshot = () => command('app.bootstrap');
  const group = label => { passed.push(label); console.log('SUCCESS: ' + label); };
  const until = async predicate => {
    const began = Date.now();
    while (Date.now() - began < 12000) { const value = await predicate(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
    throw new Error('The expected native state did not arrive.');
  };
  try {
    const initial = await snapshot();
    assert(initial.settings.isVerification === true, 'Refusing to mutate a normal user-data session.');
    const relative = path.relative(dataRoot, path.resolve(initial.controller.profileDirectory));
    assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'The native data folder is outside the supplied isolated root.');
    safeSession = true;
    assert.equal(initial.controller.status.outputActive, false);
    assert.equal(initial.controller.status.scriptsEnabled, false);
    assert.equal(initial.voice.status.isRunning, false);
    assert(!initial.vision.status.session?.running);
    group('Native startup preserves stopped controller, microphone and capture state.');

    await command('controller.profileCreate', { name: 'Synthetic bridge verification' });
    let state = await snapshot();
    const setupId = state.controller.activeProfile.id;
    await command('controller.profileRename', { name: 'Synthetic controller setup' });
    const script = { id: randomUUID(), name: 'Synthetic Lua check', source: 'output.button("A", true)\nwait(20)\noutput.release("A")' };
    await command('controller.luaValidate', { source: script.source });
    await assert.rejects(command('controller.luaValidate', { source: 'function broken(' }));
    await command('controller.luaSave', { script });
    await command('controller.bindingSave', { control: 'a', script: 'lua', enabled: true, consume: false, luaScriptId: script.id });
    assert.equal((await command('controller.luaGet', { id: script.id })).source, script.source);
    state = await snapshot();
    const timing = state.controller.activeProfile.timings.find(item => item.variant === 'rhythm').timing;
    await command('controller.timingSave', { variant: 'rhythm', timing: { ...timing, downHold: 526 } });
    const trajectory = await command('controller.trajectory', { variant: 'rhythm' });
    assert.equal(trajectory.normalized, true);
    assert(trajectory.frames.some(frame => frame.rightY === -1) && trajectory.frames.some(frame => frame.rightY === 1));
    const stored = JSON.parse(fs.readFileSync(path.join(initial.controller.profileDirectory, setupId.replaceAll('-', '') + '.json'), 'utf8'));
    assert.equal(stored.profile.name, 'Synthetic controller setup');
    assert.equal(stored.profile.luaScripts[script.id].source, script.source);
    group('Native controller setup, Lua validation/persistence, mappings and full-amplitude trajectory.');

    // Seed only the verified isolated folder, using the application's actual persisted contract.
    const recordingId = randomUUID(), fixtureId = randomUUID();
    const empty = { buttons: 0, leftTrigger: 0, rightTrigger: 0, leftX: 0, leftY: 0, rightX: 0, rightY: 0 };
    const fixture = { id: fixtureId, name: 'Synthetic recording fixture', scriptsEnabled: false, bindings: {},
      timings: Object.fromEntries(state.controller.activeProfile.timings.map(item => [item.variant, item.timing])),
      recordings: { [recordingId]: { id: recordingId, name: 'Synthetic sweep', channels: 'rightStick', durationMs: 400,
        frames: [0, 100, 200, 300].map((timeMs, index) => ({ timeMs, state: { ...empty, rightY: [-32767, 0, 32767, 0][index] } })) } }, luaScripts: {} };
    fs.writeFileSync(path.join(initial.controller.profileDirectory, fixtureId.replaceAll('-', '') + '.json'), JSON.stringify({ version: 3, profile: fixture }), { flag: 'wx' });
    await command('controller.profileSelect', { id: fixtureId });
    const original = await command('controller.recordGet', { id: recordingId });
    await command('controller.nbaFeedback', { id: recordingId, timingFeedback: 'early', tempoFeedback: 'none' });
    const retimed = await command('controller.recordGet', { id: recordingId });
    assert(retimed.durationMs > original.durationMs);
    retimed.frames[0].state.rightX = 1000;
    await command('controller.recordUpdate', { recording: retimed });
    const saved = await command('controller.recordGet', { id: recordingId });
    assert.equal(saved.durationMs, retimed.durationMs); assert.equal(saved.frames[0].state.rightX, 1000);
    const invalidRetime = structuredClone(saved); invalidRetime.frames[0].state.rightX = 2222;
    await assert.rejects(command('controller.recordRetime', { id: recordingId, recording: invalidRetime, startFrame: 0, endFrame: 999, durationMs: 10 }));
    assert.equal((await command('controller.recordGet', { id: recordingId })).frames[0].state.rightX, 1000);
    const copy = await command('controller.recordDuplicate', { id: recordingId, name: 'Synthetic copy' });
    await command('controller.recordDelete', { id: copy.id });
    group('Recorded sequence loading, feedback retiming, sample edits and duplicate/delete persistence.');

    const shot = { id: randomUUID(), name: 'Synthetic UI shot', heightInches: 78, threePointRating: 84,
      base: 'Synthetic base', upperRelease1: 'Synthetic upper one', upperRelease2: 'Synthetic upper two',
      usesShotMeter: true, meterReleaseType: 'Synthetic cue', shotSpeed: 4, release1Percent: 35, cameraView: '2K' };
    await command('vision.profileSave', { profile: shot });
    state = await snapshot();
    assert.equal(state.vision.selectedProfile.release2Percent, 65);
    await command('vision.settingsSave', { settings: { ...state.vision.settings, executionProvider: 'cpu' } });
    await assert.rejects(command('vision.armedSet', { armed: true }), /compatible model/);
    group('Shot attributes, complementary blend, native CPU setting and unvalidated-assistance rejection.');

    if (sourceHelper) {
      assert.equal(path.basename(sourceHelper).toLowerCase(), 'windowsverification.exe');
      // WGC requires a visible source; windowsHide hides the helper's first native window.
      sourceProcess = spawn(sourceHelper, ['--preview-source'], { windowsHide: false, stdio: 'ignore' });
      const source = await until(async () => {
        assert(sourceProcess.exitCode === null, 'The synthetic source helper exited before a window appeared.');
        await command('vision.sourceRefresh');
        return (await snapshot()).vision.sources.find(item => item.title === 'AccessRelay synthetic preview source');
      });
      await command('vision.sourceSelect', { id: source.id });
      await command('app.page', { page: 'vision', visible: true });
      await command('vision.start', { loadModel: false });
      const preview = await until(async () => (await snapshot()).vision.status.preview);
      const media = await page.evaluate(async url => {
        const response = await fetch(url), bytes = new Uint8Array(await response.arrayBuffer());
        const image = await createImageBitmap(new Blob([bytes], { type: response.headers.get('content-type') }));
        return { status: response.status, first: [...bytes.slice(0, 2)], width: image.width, height: image.height };
      }, preview.url);
      assert.equal(media.status, 200); assert.deepEqual(media.first, [66, 77]); assert(media.width > 100 && media.width <= 960);
      const frozen = await command('vision.freeze');
      await command('vision.layoutSave', { token: frozen.token, player: { left: .25, top: .2, width: .4, height: .6 },
        layout: { ...frozen.layout, hudName: 'Synthetic HUD', gameBuild: 'UI verification only', shotCategory: 'Mixed / unclassified',
          purpose: 'meterSupervisedTraining', gameplayRegion: { left: 0, top: 0, width: 1, height: 1 },
          meterRegion: { left: .7, top: .2, width: .15, height: .5 }, feedbackRegion: { left: .1, top: .05, width: .6, height: .12 } } });
      state = await snapshot(); assert.equal(state.vision.settings.trainingCapture.configured, true);
      assert.equal(state.vision.settings.trainingCapture.sourceWidth, frozen.sourceWidth);
      group('Actual synthetic-window WGC, intercepted binary preview decoded by WebView2, frozen layout and saved source geometry.');
      sourceProcess.kill(); sourceProcess = null;
      await until(async () => { await command('vision.sourceRefresh'); return (await snapshot()).vision.selectedSourceId === ''; });
      assert.equal((await snapshot()).vision.settings.trainingCapture.configured, false);
      group('Closed-source removal and capture-layout invalidation.');
    }
    await command('controller.profileSelect', { id: setupId });
    await command('controller.profileDelete', { id: fixtureId });
    console.log(JSON.stringify({ version: initial.version, passed, scope: 'Isolated native bridge and synthetic images. No live microphone, virtual output, trained model or gameplay accuracy was exercised.' }, null, 2));
  } finally {
    if (safeSession) { try { await command('app.stopAll'); } catch {} }
    sourceProcess?.kill();
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
