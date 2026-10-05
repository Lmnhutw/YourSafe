// Reuse an installed Playwright package; this repository does not ship or download a browser runtime.
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
const packagePath = process.env.YOURSAFE_PLAYWRIGHT_PACKAGE;
if (!packagePath) throw new Error('Set YOURSAFE_PLAYWRIGHT_PACKAGE to an installed playwright package directory.');
const { chromium } = createRequire(import.meta.url)(packagePath);
const browser = await chromium.launch({ headless: true, ...(process.env.YOURSAFE_BROWSER_EXECUTABLE ? { executablePath: process.env.YOURSAFE_BROWSER_EXECUTABLE } : {}) });
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(pathToFileURL(resolve('test/browser-smoke.html')).href);
  const result = await page.locator('#results').innerText();
  if (!result.startsWith('PASS:') || errors.length) throw new Error(result + ': ' + errors.join('; '));
  console.log(result);
} finally { await browser.close(); }
