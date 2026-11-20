// Reuse an installed Playwright package; this repository does not ship or download a browser runtime.
import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const packagePath = process.env.YOURSAFE_PLAYWRIGHT_PACKAGE;
if (!packagePath) throw new Error('Set YOURSAFE_PLAYWRIGHT_PACKAGE to an installed playwright package directory.');
const { chromium } = createRequire(import.meta.url)(packagePath);
const files = new Map([['/test/browser-smoke.html', ['test/browser-smoke.html', 'text/html']],
  ['/dist/test/browser-smoke.js', ['dist/test/browser-smoke.js', 'text/javascript']],
  ['/popup.html', ['static/popup.html', 'text/html']], ['/popup.css', ['static/popup.css', 'text/css']],
  ['/popup.js', ['dist/development/popup.js', 'text/javascript']]]);
const keyPath = process.env.YOURSAFE_TEST_TLS_KEY;
const certPath = process.env.YOURSAFE_TEST_TLS_CERT;
if (!!keyPath !== !!certPath) throw new Error('Set both YOURSAFE_TEST_TLS_KEY and YOURSAFE_TEST_TLS_CERT for the isolated HTTPS fixture.');
const serve = (request, response) => {
  const file = files.get(request.url?.split('?')[0]);
  if (!file) { response.writeHead(404).end(); return; }
  void readFile(resolve(file[0])).then(data => { response.writeHead(200, { 'Content-Type': file[1] }).end(data); },
    () => { response.writeHead(500).end(); });
};
const server = keyPath ? createHttpsServer({ key: await readFile(keyPath), cert: await readFile(certPath) }, serve) : createServer(serve);
await new Promise((resolveReady, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolveReady); });
let browser;
try {
  browser = await chromium.launch({ headless: true, ...(process.env.YOURSAFE_BROWSER_EXECUTABLE ? { executablePath: process.env.YOURSAFE_BROWSER_EXECUTABLE } : {}) });
  const page = await browser.newPage({ ignoreHTTPSErrors: !!keyPath });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Missing smoke-test server port');
  await page.goto(`${keyPath ? 'https' : 'http'}://127.0.0.1:${address.port}/test/browser-smoke.html`);
  await page.waitForFunction(() => /^(PASS:|FAIL:)/.test(document.getElementById('results')?.textContent ?? ''), undefined, { timeout: 15000 });
  const result = await page.locator('#results').innerText();
  if (!result.startsWith('PASS:') || errors.length) throw new Error(result + ': ' + errors.join('; '));
  console.log(result);
  // The actual popup markup, bundle and styles run against a synthetic worker; no vault or native host is involved.
  await page.addInitScript(() => {
    const listeners = [];
    window.popupRequests = [];
    window.chrome ??= {};
    window.chrome.runtime = { connect: () => ({
      onMessage: { addListener: listener => listeners.push(listener) }, onDisconnect: { addListener() {} }, disconnect() {},
      postMessage({ requestId, message }) {
        window.popupRequests.push(message);
        const result = message.action === 'resume' ? { ok: true, result: { resumed: location.search === '?pending', interaction: {
          token: 'synthetic-token', origin: 'https://example.test', accounts: [
            { id: '11111111-1111-1111-1111-111111111111', title: 'Example account', username: 'test-user', hasPassword: true, hasTotp: false }
          ], truncated: false, busy: true,
          selectedId: '11111111-1111-1111-1111-111111111111', action: 'select'
        } } } : { ok: false, error: message.action === 'showApp' ? 'desktopUnavailable'
          : location.search === '?locked' ? 'locked' : 'integrationDisabled' };
        queueMicrotask(() => listeners.forEach(listener => listener({ requestId, ...result })));
      }
    }) };
  });
  const base = `${keyPath ? 'https' : 'http'}://127.0.0.1:${address.port}`;
  for (const theme of [{ colorScheme: 'light', forcedColors: 'none' }, { colorScheme: 'dark', forcedColors: 'none' },
    { colorScheme: 'light', forcedColors: 'active' }]) {
    await page.emulateMedia(theme);
    await page.setViewportSize({ width: 360, height: 600 });
    await page.goto(base + '/popup.html');
    await page.getByRole('heading', { name: 'Browser integration is off' }).waitFor();
    assert.equal(await page.locator('#status').getAttribute('data-state'), 'off');
    assert.equal(await page.locator('.account').count(), 0);
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('#integration-off-open').evaluate(button => button === document.activeElement), true,
      'OFF card action is keyboard reachable');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'popup has no horizontal overflow');
    await page.keyboard.press('Enter');
    await page.getByText(/Could not connect to YourSafe/).waitFor();
    assert.equal(await page.locator('#status').getAttribute('data-state'), 'off');
    await page.goto(base + '/popup.html?locked');
    await page.getByText(/unlock it, then reopen this popup/).waitFor();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('#show-app').evaluate(button => button === document.activeElement), true,
      'Open YourSafe is the first keyboard action while locked');
    assert.equal(await page.locator('#refresh').isEnabled(), true);
    await page.goto(base + '/popup.html?pending');
    await page.waitForFunction(() => document.getElementById('status')?.dataset.state === 'pending');
    assert.equal(await page.locator('#status').innerText(), 'Fill password for Example account (test-user). Approve in YourSafe on Windows.');
    assert.equal(await page.locator('#show-app').isEnabled(), true);
    assert.equal(await page.locator('#cancel').isEnabled(), true);
    assert.equal(await page.locator('#cancel').isVisible(), true);
    await page.locator('#show-app').click();
    await page.getByText(/Could not connect to YourSafe/).waitFor();
    assert.equal(await page.locator('#status').getAttribute('data-state'), 'pending');
    assert.equal(await page.locator('#cancel').isVisible(), true);
    assert.deepEqual(await page.evaluate(() => window.popupRequests.map(message => message.action)), ['resume', 'showApp']);
  }
  assert.deepEqual(errors, []);
  console.log('PASS: popup OFF/locked/pending, keyboard, opening failure and layout in Light/Dark/forced-colors (synthetic worker)');
} finally { await browser?.close(); await new Promise(resolveClosed => server.close(resolveClosed)); }
