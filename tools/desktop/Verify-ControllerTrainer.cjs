// Native UI/bridge checks. Only isolated, labeled synthetic verification profiles are mutated.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const frontendRequire = createRequire(path.resolve(__dirname, '../../src/AccessRelayStudio/DesktopUi/package.json'));
const { chromium, expect } = frontendRequire('@playwright/test');
const argument = (name, fallback = '') => { const index = process.argv.indexOf(name); return index >= 0 ? process.argv[index + 1] : fallback; };
const dataRoot = path.resolve(argument('--data-root'));
const endpoint = argument('--endpoint', 'http://127.0.0.1:9341');
assert(argument('--data-root'), 'Supply the isolated verification root.');
assert(['127.0.0.1', 'localhost'].includes(new URL(endpoint).hostname));

(async () => {
  const browser = await chromium.connectOverCDP(endpoint);
  let page;
  const startupDeadline = Date.now() + 20000;
  while (!page && Date.now() < startupDeadline) {
    page = browser.contexts().flatMap(context => context.pages()).find(item => item.url() === 'https://studio.local/index.html');
    if (!page) await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!page) { await browser.close(); throw new Error('The verification desktop did not open.'); }
  const command = (method, params = {}) => page.evaluate(({ method, params }) => new Promise((resolve, reject) => {
    const bridge = window.chrome?.webview;
    if (!bridge) return reject(new Error('Native desktop bridge unavailable.'));
    const id = 'trainer-verification-' + crypto.randomUUID();
    const timeout = setTimeout(() => { bridge.removeEventListener('message', receive); reject(new Error(method + ' timed out')); }, 20000);
    function receive(event) {
      if (event.data?.type !== 'response' || event.data.id !== id) return;
      clearTimeout(timeout); bridge.removeEventListener('message', receive);
      event.data.ok ? resolve(event.data.result) : reject(new Error(event.data.error));
    }
    bridge.addEventListener('message', receive); bridge.postMessage({ protocol: 1, id, method, params });
  }), { method, params });
  try {
    const initial = await command('app.bootstrap');
    assert.equal(initial.settings.isVerification, true);
    const relative = path.relative(dataRoot, initial.controller.profileDirectory);
    assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
    assert.equal(initial.controller.status.outputActive, false);
    assert.equal(initial.controller.status.scriptsEnabled, false);
    const pack = initial.controller.gamePacks.find(item => item.manifest.controllerTrainer);
    assert(pack);
    const scripts = initial.controller.activeProfile.luaScripts;
    assert.equal(scripts.length, 3);
    assert(scripts.every(script => script.training.sourceMatches));
    const current = scripts.find(script => script.training.shotId === 'standing-still');
    const other = scripts.find(script => script.training.shotId === 'fade-away');
    const otherSource = (await command('controller.luaGet', { id: other.id })).source;
    await assert.rejects(command('controller.trainerConvert', { key: pack.key, shotId: 'standing-still', timingFeedback: 'late', tempoFeedback: 'fast' }), /Record and stop/);

    await page.getByRole('button', { name: 'Controller', exact: true }).click();
    await page.getByRole('tab', { name: 'Plugins', exact: true }).click();
    for (const name of ['Standing Still', 'Off-Dribble', 'Fade-Away']) await expect(page.getByRole('tab', { name, exact: true })).toBeVisible();
    const choose = async (label, value) => {
      await page.getByLabel(label, { exact: true }).click();
      await page.locator(`[role="option"][data-value="${value}"]`).click();
    };
    await choose('Shot Timing', 'late'); await choose('Shot Tempo', 'fast');
    await page.getByRole('button', { name: 'Apply shot feedback', exact: true }).click();
    await expect(page.getByLabel('Hold before release (ms)', { exact: true })).toHaveValue('384');
    await expect(page.getByLabel('Stick stroke duration (ms)', { exact: true })).toHaveValue('48');
    await expect(page.getByRole('button', { name: 'Apply shot feedback', exact: true })).toBeDisabled();
    assert.equal((await command('controller.luaGet', { id: other.id })).source, otherSource);

    await page.getByLabel('Hold before release (ms)', { exact: true }).fill('450');
    await page.getByLabel('Stick stroke duration (ms)', { exact: true }).fill('60');
    await page.getByRole('button', { name: 'Save manual durations', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save manual durations', exact: true })).toBeDisabled();
    const saved = await command('controller.luaGet', { id: current.id });
    assert.equal(saved.training.holdMs, 450); assert.equal(saved.training.tempoMs, 60);
    assert.equal(saved.training.baseline.frames[1].timeMs, 400);
    assert.equal(saved.training.baselineTimingFeedback, 'late');
    await assert.rejects(command('controller.trainerTune', { key: pack.key, id: current.id, holdMs: 0, tempoMs: 60 }));
    assert.equal((await command('controller.luaGet', { id: current.id })).source, saved.source);

    await choose('Controller key, input or paddle', 'a');
    await page.getByRole('button', { name: 'Assign shot', exact: true }).click();
    await expect(page.getByText('Assigned to A.', { exact: false })).toBeVisible();
    await assert.rejects(command('controller.trainerAssign', { key: pack.key, id: other.id, control: 'a' }), /already has a mapping/);
    let snapshot = await command('app.bootstrap');
    assert.equal(snapshot.controller.activeProfile.bindings.find(binding => binding.control === 'a').luaScriptId, current.id);
    assert.equal(snapshot.controller.activeProfile.bindings.find(binding => binding.control === 'x').script, 'passthrough');
    assert.equal(snapshot.controller.status.scriptsEnabled, false);
    assert.equal(snapshot.controller.status.outputActive, false);

    const screenshots = path.join(dataRoot, 'screenshots'); fs.mkdirSync(screenshots, { recursive: true });
    for (const theme of ['light', 'dark']) {
      if (await page.locator('html').getAttribute('data-theme') !== theme)
        await page.getByRole('button', { name: theme === 'dark' ? 'Switch to dark mode' : 'Switch to light mode', exact: true }).click();
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      const practice = page.locator('section.card').filter({ has: page.getByRole('heading', { name: '4. Practice and adjust', exact: true }) });
      await practice.screenshot({ path: path.join(screenshots, `practice-${theme}.png`) });
    }
    await page.getByRole('tab', { name: 'Fade-Away', exact: true }).click();
    await expect(page.getByLabel('Hold before release (ms)', { exact: true })).toHaveValue('400');
    const custom = await command('controller.luaGet', { id: other.id });
    await command('controller.luaSave', { script: { ...custom, source: custom.source + '\n-- user custom edit\n' } });
    await expect(page.getByText(/This Lua script has custom edits/)).toBeVisible();
    await assert.rejects(command('controller.trainerTune', { key: pack.key, id: other.id, holdMs: 500, tempoMs: 60 }), /edited outside the trainer/);
    snapshot = await command('app.bootstrap');
    assert.equal(snapshot.controller.status.outputActive, false);
    assert.equal(snapshot.controller.status.scriptsEnabled, false);
    console.log('SUCCESS Native trainer UI and bridge: actual package, independent shots, feedback/manual Lua updates, immutable baseline, invalid-edit rejection, assignment conflicts, custom-source protection, stopped output, light/dark screenshots.');
    console.log('Evidence: ' + dataRoot);
    await command('app.close', { discard: true });
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
