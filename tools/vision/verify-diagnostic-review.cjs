// Device-free browser acceptance for a recording produced by --diagnostic-recording.
const { chromium } = require('../../src/AccessRelayStudio/DesktopUi/node_modules/@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const parent = path.resolve(process.argv[2] || 'artifacts/DiagnosticRecordingEvidence');
  const directory = fs.readdirSync(parent).map(name => path.join(parent, name)).find(folder => {
    const report = path.join(folder, 'diagnostic.json');
    return fs.existsSync(path.join(folder, 'review.html')) && fs.existsSync(report) && JSON.parse(fs.readFileSync(report)).context.plugin === 'fixture';
  });
  if (!directory) throw Error('Run the native diagnostic recording checks first.');
  const browser = await chromium.launch({ headless: true, channel: 'msedge' });
  try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 1000 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(directory, 'review.html')).href);
    await page.locator('#folder').setInputFiles(directory);
    await page.getByText('Original captured pixels. Outline shows the detector’s meter region.').waitFor();
    if (!await page.locator('#message').textContent().then(text => text.includes('Fill velocity is not yet reliable.')))
      throw Error('Viewer lost the recorded detection decision.');
    if (!await page.locator('#details').textContent().then(text => text.includes('-32768') && text.includes('57.0%')))
      throw Error('Viewer lost physical stick or fill values.');
    const size = await page.locator('#image').evaluate(canvas => [canvas.width, canvas.height]);
    if (size.join(',') !== '16,12') throw Error('Viewer did not decode native image dimensions.');
    await page.screenshot({ path: path.join(parent, 'diagnostic-viewer.png'), fullPage: true });
    await page.setViewportSize({ width: 480, height: 900 });
    if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth))
      throw Error('Diagnostic viewer overflows a narrow window.');
    if (errors.length) throw Error(errors.join('\n'));
    console.log('SUCCESS Offline diagnostic viewer: local folder loading, lossless image decoding, detection/input alignment and narrow layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
