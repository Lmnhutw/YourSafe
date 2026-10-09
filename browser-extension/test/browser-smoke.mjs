// Reuse an installed Playwright package; this repository does not ship or download a browser runtime.
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const packagePath = process.env.YOURSAFE_PLAYWRIGHT_PACKAGE;
if (!packagePath) throw new Error('Set YOURSAFE_PLAYWRIGHT_PACKAGE to an installed playwright package directory.');
const { chromium } = createRequire(import.meta.url)(packagePath);
const files = new Map([['/test/browser-smoke.html', ['test/browser-smoke.html', 'text/html']],
  ['/dist/test/browser-smoke.js', ['dist/test/browser-smoke.js', 'text/javascript']]]);
const server = createServer((request, response) => {
  const file = files.get(request.url);
  if (!file) { response.writeHead(404).end(); return; }
  void readFile(resolve(file[0])).then(data => { response.writeHead(200, { 'Content-Type': file[1] }).end(data); },
    () => { response.writeHead(500).end(); });
});
await new Promise((resolveReady, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolveReady); });
let browser;
try {
  browser = await chromium.launch({ headless: true, ...(process.env.YOURSAFE_BROWSER_EXECUTABLE ? { executablePath: process.env.YOURSAFE_BROWSER_EXECUTABLE } : {}) });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Missing smoke-test server port');
  await page.goto(`http://127.0.0.1:${address.port}/test/browser-smoke.html`);
  await page.waitForFunction(() => /^(PASS:|FAIL:)/.test(document.getElementById('results')?.textContent ?? ''), undefined, { timeout: 15000 });
  const result = await page.locator('#results').innerText();
  if (!result.startsWith('PASS:') || errors.length) throw new Error(result + ': ' + errors.join('; '));
  console.log(result);
} finally { await browser?.close(); await new Promise(resolveClosed => server.close(resolveClosed)); }
