// Focused native check; refuses the normal installed app's user-data session.
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { createRequire } = require('node:module');
const { chromium } = createRequire(path.resolve(__dirname, '../../src/AccessRelayStudio/DesktopUi/package.json'))('@playwright/test');
const arg = name => process.argv[process.argv.indexOf(name) + 1];
const endpoint = arg('--endpoint'), sourceHelper = arg('--source-helper');
assert(process.argv.includes('--endpoint') && ['127.0.0.1', 'localhost'].includes(new URL(endpoint).hostname));
const until = async predicate => {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) { const result = await predicate(); if (result) return result; await new Promise(r => setTimeout(r, 100)); }
  throw new Error('The expected native capture-window state did not arrive.');
};
const command = (page, method, params = {}) => page.evaluate(({ method, params }) => new Promise((resolve, reject) => {
  const bridge = window.chrome.webview, id = crypto.randomUUID();
  const timer = setTimeout(() => { bridge.removeEventListener('message', receive); reject(new Error(method + ' timed out')); }, 15000);
  function receive({ data }) {
    if (data.type !== 'response' || data.id !== id) return;
    clearTimeout(timer); bridge.removeEventListener('message', receive);
    data.ok ? resolve(data.result) : reject(new Error(data.error));
  }
  bridge.addEventListener('message', receive); bridge.postMessage({ protocol: 1, id, method, params });
}), { method, params });

(async () => {
  await until(async () => { try { return (await fetch(endpoint + '/json/version')).ok; } catch { return false; } });
  const browser = await chromium.connectOverCDP(endpoint);
  const pages = () => browser.contexts().flatMap(c => c.pages());
  const main = await until(() => pages().find(p => p.url() === 'https://studio.local/index.html'));
  let source, safe = false;
  try {
    const initial = await command(main, 'app.bootstrap');
    assert.equal(initial.settings.isVerification, true, 'Refusing to mutate the normal installed app.'); safe = true;
    await main.getByRole('button', { name: 'Live Gameplay', exact: true }).click();
    assert.equal(await main.getByRole('button', { name: 'Record training clip' }).count(), 0);
    await main.getByRole('button', { name: 'Detach', exact: true }).click();
    let viewer = await until(() => pages().find(p => p.url().endsWith('?surface=gameplay')));
    await viewer.getByRole('button', { name: 'Bring back', exact: true }).waitFor();
    assert.equal(await viewer.locator('.sidebar').count(), 0);
    assert.equal((await command(main, 'app.bootstrap')).gameplayWindow.detached, true);
    await command(main, 'app.gameplayDetach');
    assert.equal(pages().filter(p => p.url().endsWith('?surface=gameplay')).length, 1);
    await assert.rejects(command(viewer, 'controller.outputStart'), /unavailable/);
    await assert.rejects(command(viewer, 'vision.start'), /unavailable/);
    await command(main, 'app.themeSet', { theme: 'dark' });
    await until(async () => await viewer.locator('html').getAttribute('data-theme') === 'dark');
    assert.equal((await command(main, 'app.bootstrap')).controller.status.outputActive, false);

    if (process.argv.includes('--source-helper')) {
      assert.equal(path.basename(sourceHelper).toLowerCase(), 'windowsverification.exe');
      // WGC needs the synthetic source's own visible window.
      source = spawn(sourceHelper, ['--preview-source'], { windowsHide: false, stdio: 'ignore' });
      const captureSource = await until(async () => {
        assert(source.exitCode === null); await command(main, 'vision.sourceRefresh');
        return (await command(main, 'app.bootstrap')).vision.sources.find(s => s.title === 'AccessRelay synthetic preview source');
      });
      await command(main, 'vision.sourceSelect', { id: captureSource.id });
      await command(main, 'vision.start', { loadModel: false });
      await until(async () => await viewer.getByRole('img', { name: 'Live gameplay capture' }).count());
      const seq = (await command(viewer, 'app.bootstrap')).vision.preview.sequence;
      await main.getByRole('button', { name: 'Settings', exact: true }).click();
      await until(async () => (await command(viewer, 'app.bootstrap')).vision.preview.sequence > seq);
      const image = viewer.getByRole('img', { name: 'Live gameplay capture' });
      assert(await image.evaluate(img => img.complete && img.naturalWidth > 100), 'Detached native preview must decode real captured frames.');
      await viewer.getByRole('button', { name: 'Bring back', exact: true }).click();
      await until(() => viewer.isClosed());
      assert.equal((await command(main, 'app.bootstrap')).vision.status.session.running, true, 'Reattachment must preserve the session.');
      await main.getByRole('button', { name: 'Live Gameplay', exact: true }).click();
      await main.getByRole('img', { name: 'Live gameplay capture' }).waitFor();
      await main.getByRole('button', { name: 'Detach', exact: true }).click();
      viewer = await until(() => pages().find(p => p.url().endsWith('?surface=gameplay')));
      await viewer.getByRole('button', { name: 'Stop capture', exact: true }).click();
      await until(async () => !(await command(main, 'app.bootstrap')).vision.status.session.running);
    }
    // A normal application shutdown closes the detached viewer too.
    const mainClosed = main.waitForEvent('close'), viewerClosed = viewer.waitForEvent('close');
    await command(main, 'app.close', { discard: true });
    await Promise.all([mainClosed, viewerClosed]); safe = false;
    console.log('SUCCESS: separate pages, one detached native viewer, restricted bridge, theme sync, shared live capture, reattachment, stop and shutdown.');
  } finally {
    if (safe && !main.isClosed()) { try { await command(main, 'app.stopAll'); await command(main, 'app.gameplayAttach'); } catch {} }
    source?.kill(); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
