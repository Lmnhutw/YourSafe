// Uses a disposable vault, an isolated browser profile, the installed extension and real desktop approval.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash, randomUUID } from 'node:crypto';
const execFileAsync = promisify(execFile);
const extensionId = 'cmfnnjellknkpnbooenlahijcmalbifg';
const passwordTitle = 'TOTP password account';
const totpTitle = 'TOTP only account';
const syntheticPassword = 'synthetic-test-password';
const syntheticUsername = 'totp-password@example.test';
function freshCodeDelay(now) {
  const remaining = 30000 - now % 30000;
  return remaining >= 20000 ? 0 : remaining + 100;
}
function sectionSelected(section, prefixes) {
  return !prefixes.length || prefixes.some(prefix => prefix.startsWith(section) || section.startsWith(prefix));
}
function evidenceStatus(results, complete, error) {
  return error || results.some(result => result.status === 'failed') ? 'failed'
    : results.some(result => result.status === 'blocked') ? 'blocked' : complete ? 'verified' : 'running';
}
function fixture(mode) {
  const password = '<input id="password" type="password" autocomplete="current-password">';
  const username = '<input id="username" autocomplete="username">';
  const unsafeImage = '<input type="image" form="login" formaction="https://untrusted.example/collect">';
  const form = `<form id="login">${mode === 'password-only' ? '' : username}${password}${mode === 'unsafe-image' ? unsafeImage : ''}<button>Sign in</button></form>${mode === 'external-image' ? unsafeImage : ''}`;
  return `<!doctype html><meta charset="utf-8"><title>YourSafe disposable E2E</title><style>body{font:18px sans-serif;margin:48px}input{display:block;margin:12px;width:320px;height:36px}</style><h1>Synthetic login</h1><main id="fixture">${mode === 'dynamic' ? '' : form}</main><script>
    window.events = { input: 0, change: 0, submit: 0 };
    document.addEventListener('input', () => window.events.input++);
    document.addEventListener('change', () => window.events.change++);
    document.addEventListener('submit', event => { event.preventDefault(); window.events.submit++; });
    ${mode === 'dynamic' ? `setTimeout(() => document.getElementById('fixture').innerHTML = ${JSON.stringify(form)}, 150);` : ''}
    ${mode === 'mutate-image' ? "document.getElementById('username').addEventListener('input', () => document.getElementById('fixture').insertAdjacentHTML('beforeend', '<input type=\"image\" form=\"login\" formaction=\"https://untrusted.example/collect\">'), { once: true });" : ''}
  </script>`;
}
if (process.argv.includes('--self-check')) {
  for (const mode of ['normal', 'password-only', 'dynamic', 'unsafe-image', 'external-image', 'mutate-image']) {
    const html = fixture(mode);
    assert(html.includes('current-password') && html.includes('event.preventDefault()'));
    assert(!html.includes(syntheticPassword));
  }
  assert(fixture('external-image').includes('</form><input type="image" form="login"'));
  assert.equal(freshCodeDelay(0), 0);
  assert.equal(freshCodeDelay(10000), 0);
  assert.equal(freshCodeDelay(29999), 101);
  assert.equal(freshCodeDelay(30000), 0);
  assert(sectionSelected('E08', ['E08-pending-editPassword']));
  assert(sectionSelected('E08', ['E0']));
  assert(!sectionSelected('E08', ['E11']));
  assert.equal(evidenceStatus([{ status: 'verified' }], false), 'running');
  assert.equal(evidenceStatus([{ status: 'verified' }], true, 'setup failed'), 'failed');
  assert.equal(evidenceStatus([{ status: 'failed' }], true), 'failed');
  assert.equal(evidenceStatus([{ status: 'blocked' }], true), 'blocked');
  assert.equal(evidenceStatus([{ status: 'verified' }], true), 'verified');
  assert.equal(safeFailure(new Error(`${syntheticPassword} ${syntheticUsername} 123456`)), '[synthetic password] [synthetic username] [digits]');
  console.log('PASS: six secret-free E2E fixtures and controlled OTP-window timing.');
  process.exit(0);
}
const testDirectory = process.env.YOURSAFE_E2E_DIRECTORY;
const executablePath = process.env.YOURSAFE_BROWSER_EXECUTABLE;
const secondExecutablePath = process.env.YOURSAFE_SECOND_BROWSER_EXECUTABLE;
const packagePath = process.env.YOURSAFE_PLAYWRIGHT_PACKAGE;
const appPid = Number(process.env.YOURSAFE_APP_PID);
assert(testDirectory && executablePath && packagePath && Number.isSafeInteger(appPid) && appPid > 0,
  'Set YOURSAFE_E2E_DIRECTORY, YOURSAFE_BROWSER_EXECUTABLE, YOURSAFE_PLAYWRIGHT_PACKAGE and YOURSAFE_APP_PID.');
const controlScript = resolve(process.env.YOURSAFE_E2E_CONTROL_SCRIPT ?? '../scripts/AutofillTestControl.ps1');
const port = Number(process.env.YOURSAFE_E2E_PORT ?? '49173');
assert(Number.isInteger(port) && port >= 1024 && port <= 65535, 'Invalid fixture port');
const root = resolve(testDirectory);
const evidence = join(root, `extension-e2e-results-${randomUUID()}`);
const selectedCases = (process.env.YOURSAFE_E2E_CASE_PREFIX ?? '').split(',').filter(Boolean);
const expiryOnly = selectedCases.length > 0 && selectedCases.every(prefix => prefix.startsWith('E11'));
const profile = join(root, `extension-e2e-profile-${randomUUID()}`);
await mkdir(evidence, { recursive: true });
await mkdir(profile, { recursive: true });
assert.equal((await readdir(profile)).length, 0, 'Use a fresh disposable test directory; refusing to reuse a populated browser profile.');
// Action popups use CDP's "other" target type; this attaches the test driver to the real popup.
process.env.PW_CHROMIUM_ATTACH_TO_OTHER = '1';
const { chromium } = createRequire(import.meta.url)(packagePath);
const results = [];
let context;
let browserCdp;
let page;
let caseId = 'setup';
let version;
let secondaryBrowserVersion;
let clipboardDenied;
let sourceHashes;
let campaignComplete = false;
let campaignError;
const clipboardCases = new Set(['E05-copy-denied-no-clipboard-change', 'E05-approved-copy-and-E12-OTP-clipboard-clear',
  'E05-approved-TOTP-only-copy-eight-digit', 'E05-copy-near-expiry-uses-current-code', 'E12-OTP-same-text-replacement-survives-timer', 'E12-desktop-password-clipboard-timer-and-lock']);
for (const kind of ['OTP', 'password']) for (const replacement of ['same-text', 'changed-text']) for (const trigger of ['timer', 'lock']) {
  clipboardCases.add(`E12-${kind}-${replacement}-replacement-survives-${trigger}`);
}
async function desktop(action, timeout = 20000) {
  console.log(`CONTROL ${caseId}: ${action}`);
  try {
    await execFileAsync('pwsh', ['-NoProfile', '-File', controlScript, '-Action', action,
      '-ProcessId', String(appPid), '-TestDirectory', root], { timeout, windowsHide: true });
  } catch (error) {
    // The guarded helper returns a bounded JSON reason instead of printing clipboard contents.
    let reason;
    try { reason = JSON.parse(String(error.stdout ?? '').trim()).reason; } catch { }
    throw new Error(`Desktop ${action} failed: ${typeof reason === 'string' ? reason.slice(0, 1000) : 'helper failed or timed out'}`);
  }
}
function safeFailure(error) {
  return (error instanceof Error ? error.message : 'Unknown error').replaceAll(syntheticPassword, '[synthetic password]')
    .replaceAll(syntheticUsername, '[synthetic username]').replace(/\b\d{6,8}\b/g, '[digits]').slice(0, 2000);
}
async function writeEvidence() {
  const status = evidenceStatus(results, campaignComplete, campaignError);
  await writeFile(join(evidence, 'results.json'), JSON.stringify({ status, browser: version, secondaryBrowserVersion, sourceHashes, campaignError, results }, null, 2));
}
async function record(id, operation) {
  if (selectedCases.length && !selectedCases.some(prefix => id.startsWith(prefix))) return;
  caseId = id;
  const started = Date.now();
  try {
    if (clipboardCases.has(id)) {
      if (clipboardDenied) throw new Error(clipboardDenied);
      await desktop('clipboardSetSentinel');
    }
    await operation();
    results.push({ id, status: 'verified', layer: 'installed-extension-nativehost-winui', durationMs: Date.now() - started });
    console.log(`PASS ${id}`);
  } catch (error) {
    const message = safeFailure(error);
    if (message.includes('Clipboard unavailable (Windows error 5, open holder PID 0)')) {
      clipboardDenied = message;
      results.push({ id, status: 'blocked', layer: 'installed-extension-nativehost-winui', error: message });
      console.log(`BLOCKED ${id}: the test process cannot access the Windows clipboard.`);
      return;
    }
    results.push({ id, status: 'failed', layer: 'installed-extension-nativehost-winui', error: message });
    throw error;
  } finally { await writeEvidence(); }
}
async function load(mode = 'normal') {
  await page.goto(`http://127.0.0.1:${port}/${mode}`);
  await page.locator('#password').waitFor();
  await page.bringToFront();
}
async function openPopup(targetContext = context, cdp = browserCdp, fixturePage = page) {
  const existing = targetContext.pages().find(candidate => !candidate.isClosed() && candidate.url() === `chrome-extension://${extensionId}/popup.html`);
  if (existing) await existing.close();
  const { targetInfos } = await cdp.send('Target.getTargets', { filter: [{ type: 'tab', exclude: false }] });
  const tabs = targetInfos.filter(target => target.type === 'tab' && target.url === fixturePage.url());
  assert.equal(tabs.length, 1, 'The controlled fixture must identify exactly one tab target');
  const ready = targetContext.waitForEvent('page', { predicate: candidate => candidate.url() === `chrome-extension://${extensionId}/popup.html`, timeout: 10000 });
  void ready.catch(() => {}); // Surface action-command failures without an abandoned event wait masking them.
  await cdp.send('Extensions.triggerAction', { id: extensionId, targetId: tabs[0].targetId });
  const popup = await ready;
  await popup.locator('#status').waitFor();
  await popup.waitForFunction(() => ['ready', 'empty', 'success', 'error'].includes(document.getElementById('status')?.dataset.state ?? '')
    && document.getElementById('accounts')?.getAttribute('aria-busy') === 'false', undefined, { timeout: 12000 });
  return popup;
}
async function copyApprovedCode(nearExpiry = false, title = passwordTitle) {
  const delay = freshCodeDelay(Date.now());
  if (!nearExpiry && delay) await new Promise(done => setTimeout(done, delay));
  await choose('View code', title);
  await desktop('approve');
  await page.bringToFront();
  const popup = await openPopup();
  assert.match(await popup.locator('#totp-code').innerText(), title === totpTitle ? /^\d{4} \d{4}$/ : /^\d{3} \d{3}$/, 'Approved View must be visible before Copy');
  if (nearExpiry) {
    await popup.waitForFunction(() => document.getElementById('totp-countdown')?.textContent === '1s', undefined, { timeout: 32000 });
  }
  await popup.locator('#totp-copy').click();
  await desktop('waitApproval');
  if (nearExpiry) await new Promise(done => setTimeout(done, 1500)); // Approve after the displayed code's window ended.
  await desktop('approve');
  await page.bringToFront();
  const copied = await openPopup();
  assert.match(await copied.locator('#status').innerText(), /Copied the current code/, 'Copy must complete after its separate approval');
  await desktop(title === totpTitle ? 'clipboardAssertTotpOnly' : nearExpiry ? 'clipboardAssertCurrentOtp' : 'clipboardAssertNumeric');
  await copied.close();
  await emptyPassword();
}
async function stopExtensionWorker() {
  const { targetInfos } = await browserCdp.send('Target.getTargets');
  const workers = targetInfos.filter(target => target.type === 'service_worker' && target.url.startsWith(`chrome-extension://${extensionId}/`));
  assert.equal(workers.length, 1, 'The isolated profile must have exactly one YourSafe worker');
  const cdp = await context.newCDPSession(page);
  try {
    await cdp.send('ServiceWorker.enable');
    await cdp.send('ServiceWorker.stopAllWorkers'); // Only the disposable browser profile is controlled.
  } finally { await cdp.detach(); }
  const { targetInfos: after } = await browserCdp.send('Target.getTargets');
  assert(!after.some(target => target.targetId === workers[0].targetId), 'The original extension worker did not stop');
}
async function competingProfile(otherExecutable) {
  const otherProfile = join(root, `extension-e2e-profile-${randomUUID()}`);
  await mkdir(otherProfile, { recursive: true });
  const otherContext = await chromium.launchPersistentContext(otherProfile, {
    executablePath: otherExecutable, headless: false, viewport: null,
    args: ['--enable-unsafe-extension-debugging'], ignoreDefaultArgs: ['--disable-extensions']
  });
  try {
    secondaryBrowserVersion = otherContext.browser().version();
    console.log(`SECOND BROWSER ${caseId}: ${secondaryBrowserVersion}`);
    const otherCdp = await otherContext.browser().newBrowserCDPSession();
    assert.equal((await otherCdp.send('Extensions.loadUnpacked', { path: resolve('dist/development') })).id, extensionId);
    const otherPage = otherContext.pages()[0] ?? await otherContext.newPage();
    await otherPage.goto(`http://127.0.0.1:${port}/normal`);
    await load();
    await choose();
    await otherPage.bringToFront();
    const second = await openPopup(otherContext, otherCdp, otherPage);
    await account(second, passwordTitle).getByRole('button', { name: /^Fill / }).click();
    await second.waitForFunction(() => document.getElementById('status')?.dataset.state === 'error'
      && document.getElementById('accounts')?.getAttribute('aria-busy') === 'false');
    assert.equal(await second.locator('.account').count(), 0, 'A competing client retained authority after its Fill was denied');
    assert.equal(await otherPage.locator('#password').inputValue(), '');
    assert.equal(await otherPage.evaluate(() => window.events.submit), 0);
    await second.close();
    await desktop('waitApproval'); // The first request remains the sole approval after the second was rejected.
    await desktop('cancel');
    await page.bringToFront();
    await emptyPassword();
  } finally { await otherContext.close(); }
}
function account(popup, title) { return popup.locator('.account').filter({ has: popup.locator('strong', { hasText: title }) }); }
async function choose(action = 'Fill', title = passwordTitle, approvalExpected = true) {
  const popup = await openPopup();
  await account(popup, title).getByRole('button', { name: action === 'Fill' ? /^Fill / : /^View code for / }).click();
  if (approvalExpected) await desktop('waitApproval');
  return popup;
}
async function emptyPassword() {
  assert.equal(await page.locator('#password').inputValue(), '', 'Denied operation wrote a password');
  assert.equal(await page.evaluate(() => window.events.submit), 0, 'Autofill submitted the form');
}
async function filled(expectedEvents = 4) {
  await page.waitForFunction(value => document.getElementById('password')?.value === value, syntheticPassword, { timeout: 10000 });
  const state = await page.evaluate(() => ({ username: document.getElementById('username')?.value, events: window.events }));
  if (state.username !== undefined) assert.equal(state.username, syntheticUsername);
  assert.equal(state.events.input + state.events.change, expectedEvents, 'Must fill each input exactly once');
  assert.equal(state.events.submit, 0);
  await page.screenshot({ path: join(evidence, `${caseId}.png`) });
}
const server = createServer((request, response) => {
  const mode = request.url?.slice(1).split(/[?#]/)[0] ?? 'normal';
  if (!['normal', 'password-only', 'dynamic', 'unsafe-image', 'external-image', 'mutate-image'].includes(mode)) { response.writeHead(404).end(); return; }
  response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' }).end(fixture(mode));
});
try {
  console.log(`EVIDENCE: ${evidence}`);
  sourceHashes = Object.fromEntries(await Promise.all([
    ['runner', fileURLToPath(import.meta.url)], ['control', controlScript], ['worker', resolve('dist/development/worker.js')]
  ].map(async ([name, path]) => [name, createHash('sha256').update(await readFile(path)).digest('hex')])));
  await desktop('signIn', 60000); // A fresh sign-in includes the real six-box Authenticator dialog.
  await desktop('lock');
  await desktop('unlock'); // Start each campaign with a fresh vault deadline, without renewing the login.
  await desktop('disable');
  if (!expiryOnly) {
    console.log('PACE: waiting for any prior campaign\'s protected-request window.');
    await new Promise(done => setTimeout(done, 61000));
  }
  await new Promise((ready, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', ready); });
  context = await chromium.launchPersistentContext(profile, {
    executablePath, headless: false, viewport: null,
    args: ['--enable-unsafe-extension-debugging'],
    ignoreDefaultArgs: ['--disable-extensions']
  });
  context.setDefaultTimeout(10000);
  version = context.browser().version();
  browserCdp = await context.browser().newBrowserCDPSession();
  const installed = await browserCdp.send('Extensions.loadUnpacked', { path: resolve('dist/development') });
  assert.equal(installed.id, extensionId, 'Wrong development extension identity');
  page = context.pages()[0] ?? await context.newPage();
  await load();
  await record('E01-integration-off', async () => {
    const popup = await openPopup();
    assert.equal(await popup.locator('.account').count(), 0);
    assert.equal(await popup.locator('#status').getAttribute('data-state'), 'error');
    await popup.close();
    await emptyPassword();
  });
  await desktop('enable');
  await record('E02-desktop-locked', async () => {
    await desktop('lock');
    await page.bringToFront();
    const popup = await openPopup();
    assert.match(await popup.locator('#status').innerText(), /locked|Unlock Vault/i);
    assert.equal(await popup.locator('input[type=password]').count(), 0);
    await popup.close();
    await emptyPassword();
    await desktop('unlock');
  });
  for (const mode of ['normal', 'password-only', 'dynamic']) {
    await record(`E03-fill-${mode}`, async () => {
      await load(mode);
      await choose();
      await desktop('approve');
      await page.bringToFront();
      await filled(mode === 'password-only' ? 2 : 4);
    });
  }
  await record('E03-password-only-account', async () => {
    await load('password-only');
    await choose('Fill', 'Autofill password only');
    await desktop('approve');
    await page.bringToFront();
    await filled(2);
  });
  await record('E04-cancel-desktop-approval', async () => {
    await load();
    await choose();
    await desktop('cancel');
    await page.bringToFront();
    const popup = await openPopup();
    await emptyPassword();
    assert(!/[0-9]{6}/.test(await popup.locator('#totp-code').innerText()));
    await popup.close();
  });
  await record('E04-desktop-approval-deadline', async () => {
    await load();
    const popup = await choose();
    await desktop('waitApprovalDeadline', 85000);
    if (!popup.isClosed()) await popup.waitForFunction(() => document.getElementById('status')?.dataset.state === 'error', undefined, { timeout: 10000 });
    await page.bringToFront();
    await emptyPassword();
    const reopened = await openPopup();
    assert.doesNotMatch(await reopened.locator('#status').innerText(), /^Filled/);
    await reopened.close();
  });
  await record('E05-TOTP-only-separate-view', async () => {
    await load();
    const discovery = await openPopup();
    assert.equal(await account(discovery, totpTitle).getByRole('button', { name: /^Fill / }).count(), 0);
    assert.equal(await discovery.locator('#totp-code').innerText(), '');
    await account(discovery, totpTitle).getByRole('button', { name: /^View code for / }).click();
    await desktop('waitApproval');
    await desktop('approve');
    await page.bringToFront();
    const view = await openPopup();
    assert.match(await view.locator('#totp-code').innerText(), /^\d{4} \d{4}$/);
    await view.screenshot({ path: join(evidence, `${caseId}.png`) });
    await view.close();
    await emptyPassword();
  });
  await record('E05-copy-denied-no-clipboard-change', async () => {
    await load();
    await choose('View code', totpTitle);
    await desktop('approve');
    await page.bringToFront();
    const view = await openPopup();
    await view.locator('#totp-copy').click();
    await desktop('cancel'); // A denied independent Copy must not be reported as copied.
    await page.bringToFront();
    const afterCopy = await openPopup();
    assert.doesNotMatch(await afterCopy.locator('#status').innerText(), /Copied the current code/);
    await afterCopy.close();
    await desktop('clipboardAssertSentinel');
    await emptyPassword();
  });
  await record('E05-approved-TOTP-only-copy-eight-digit', async () => {
    await load();
    await copyApprovedCode(false, totpTitle);
    await desktop('lock');
    await desktop('clipboardAssertEmpty');
    await desktop('unlock');
  });
  await record('E05-approved-copy-and-E12-OTP-clipboard-clear', async () => {
    await load();
    await copyApprovedCode();
    await new Promise(done => setTimeout(done, 31000)); // Exercise the actual sensitive-clipboard timer.
    await desktop('clipboardAssertEmpty');
  });
  await record('E05-copy-near-expiry-uses-current-code', async () => {
    await load();
    await copyApprovedCode(true);
    await desktop('lock');
    await desktop('clipboardAssertEmpty');
    await desktop('unlock');
  });
  await record('E05-password-account-view-does-not-fill', async () => {
    await load();
    await choose('View code');
    await desktop('approve');
    await page.bringToFront();
    const popup = await openPopup();
    assert.match(await popup.locator('#totp-code').innerText(), /^\d{3} \d{3}$/);
    await popup.close();
    await emptyPassword();
  });
  await record('E05-code-expiry-does-not-request-new-approval', async () => {
    await load();
    await choose('View code');
    await desktop('approve');
    await page.bringToFront();
    const popup = await openPopup();
    await popup.waitForFunction(() => document.getElementById('totp-code')?.textContent === '—', undefined, { timeout: 32000 });
    assert.match(await popup.locator('#totp-countdown').innerText(), /Expired/);
    await desktop('assertNoApproval');
    await popup.close();
    await emptyPassword();
  });
  await record('E06-popup-close-focus-reopen-once', async () => {
    await load();
    const popup = await choose();
    if (!popup.isClosed()) await popup.close();
    await desktop('approve');
    await page.bringToFront();
    await filled();
    const resumed = await openPopup();
    assert.match(await resumed.locator('#status').innerText(), /Filled/);
    assert.equal(await resumed.locator('.account').count(), 0);
    await resumed.close();
    await filled();
  });
  // The following automated burst must respect the desktop's real 30/minute admission window.
  if (['E07', 'E08', 'E09'].some(section => sectionSelected(section, selectedCases))) {
    console.log('PACE: waiting for the protected-request window before target and form cases.');
    await new Promise(done => setTimeout(done, 61000));
  }
  for (const mutation of ['hash', 'reload', 'navigate', 'history']) {
    await record(`E07-pending-${mutation}`, async () => {
      await load();
      await choose();
      if (mutation === 'hash') await page.evaluate(() => { location.hash = 'changed'; });
      if (mutation === 'history') await page.evaluate(() => history.pushState({}, '', '/normal?changed'));
      if (mutation === 'reload') await page.reload();
      if (mutation === 'navigate') await page.goto(`http://127.0.0.1:${port}/dynamic`);
      await page.locator('#password').waitFor();
      // Cancellation closes the actual pending dialog; approve is intentionally never retried.
      await page.bringToFront();
      const popup = await openPopup();
      await emptyPassword();
      assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
      await popup.close();
    });
  }
  await record('E07-pending-active-tab-change', async () => {
    await load();
    await choose();
    const other = await context.newPage();
    await other.goto(`http://127.0.0.1:${port}/normal`);
    await other.bringToFront();
    await other.close();
    await page.bringToFront();
    const popup = await openPopup();
    await emptyPassword();
    assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
    await popup.close();
  });
  await record('E07-unrelated-background-tab-does-not-cancel', async () => {
    const background = await context.newPage();
    await background.goto(`http://127.0.0.1:${port}/dynamic`);
    await load();
    await choose();
    await background.goto(`http://127.0.0.1:${port}/password-only`);
    await background.reload();
    await background.close();
    await desktop('approve');
    await page.bringToFront();
    await filled();
  });
  await record('E07-pending-document-replacement', async () => {
    await load();
    await choose();
    await page.evaluate(() => { document.getElementById('fixture').innerHTML = '<form><input id="username" autocomplete="username"><input id="password" type="password" autocomplete="current-password"><button>Sign in</button></form>'; });
    await desktop('approve');
    await page.bringToFront();
    await emptyPassword();
    const popup = await openPopup();
    assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
    await popup.close();
  });
  await record('E07-pending-browser-window-change', async () => {
    await load();
    await choose();
    const target = await browserCdp.send('Target.createTarget', { url: `http://127.0.0.1:${port}/dynamic`, newWindow: true });
    try { await desktop('waitApprovalGone'); }
    finally { await browserCdp.send('Target.closeTarget', { targetId: target.targetId }); }
    await page.bringToFront();
    const popup = await openPopup();
    await emptyPassword();
    assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
    await popup.close();
  });
  for (const action of ['lock', 'disable']) {
    await record(`E08-pending-${action}`, async () => {
      await load();
      await choose();
      await desktop(action);
      await page.bringToFront();
      const popup = await openPopup();
      await emptyPassword();
      assert.equal(await popup.locator('.account').count(), 0);
      await popup.close();
      await desktop(action === 'lock' ? 'unlock' : 'enable');
    });
  }
  if (sectionSelected('E08', selectedCases)) {
    console.log('PACE: waiting for the protected-request window before credential edits.');
    await new Promise(done => setTimeout(done, 61000));
  }
  for (const action of ['editPassword', 'editUrl', 'removePassword']) {
    await record(`E08-pending-${action}`, async () => {
      await load();
      await choose();
      await desktop(action, 45000);
      await page.bringToFront();
      await emptyPassword();
      const popup = await openPopup();
      assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
      await popup.close();
      await desktop(action === 'editUrl' ? 'resetUrl' : 'resetPassword', 45000);
    });
  }
  await record('E08-delete-cancels-queued-desktop-approval', async () => {
    await load('password-only');
    await desktop('beginDelete');
    await page.bringToFront();
    const pending = await choose('Fill', 'Autofill password only', false);
    await pending.waitForFunction(() => document.getElementById('cancel')?.hidden === false);
    await desktop('confirmDelete');
    await desktop('assertNoApproval');
    await page.bringToFront();
    const popup = await openPopup();
    await emptyPassword();
    assert.doesNotMatch(await popup.locator('#status').innerText(), /^Filled/);
    await popup.close();
    await desktop('restoreDeleted');
  });
  for (const mode of ['unsafe-image', 'external-image', 'mutate-image']) {
    await record(`E09-${mode}`, async () => {
      await load(mode);
      const popup = await choose('Fill', passwordTitle, mode === 'mutate-image');
      if (mode === 'mutate-image') await desktop('approve');
      else await popup.waitForFunction(() => document.getElementById('status')?.dataset.state === 'error');
      await page.bringToFront();
      await emptyPassword();
    });
  }
  if (sectionSelected('E10', selectedCases)) {
    await desktop('signIn', 60000);
    await desktop('lock');
    await desktop('unlock'); // Refresh the vault deadline before this independently paced section.
  }
  await record('E10-worker-restart-cancels-pending-approval', async () => {
    await load();
    const popup = await choose();
    if (!popup.isClosed()) await popup.close();
    await stopExtensionWorker();
    await desktop('waitApprovalGone');
    await page.bringToFront();
    await emptyPassword();
    const reopened = await openPopup();
    assert.doesNotMatch(await reopened.locator('#status').innerText(), /^Filled/);
    assert.equal(await reopened.locator('#totp-code').innerText(), '');
    await desktop('assertNoApproval');
    await reopened.close();
    await choose();
    await desktop('approve');
    await page.bringToFront();
    await filled(); // The restarted worker needs a new explicit action and desktop approval.
  });
  await record('E10-worker-restart-discards-displayed-code', async () => {
    await load();
    await choose('View code', totpTitle);
    await desktop('approve');
    await page.bringToFront();
    const view = await openPopup();
    assert.match(await view.locator('#totp-code').innerText(), /^\d{4} \d{4}$/);
    await view.close();
    await stopExtensionWorker();
    const reopened = await openPopup();
    assert.equal(await reopened.locator('#totp-code').innerText(), '');
    await desktop('assertNoApproval');
    await reopened.close();
    await emptyPassword();
  });
  await record('E10-isolated-profiles-compete-for-one-approval', async () => {
    await competingProfile(executablePath);
  });
  if (secondExecutablePath) {
    await record('E10-distinct-browsers-compete-for-one-approval', async () => {
      assert.notEqual(resolve(secondExecutablePath).toLowerCase(), resolve(executablePath).toLowerCase(), 'Provide a distinct second browser executable');
      await competingProfile(secondExecutablePath);
    });
  }
  await record('E11-natural-vault-expiry-cancels-approval', async () => {
    // Requires a separate disposable app whose persisted vault timeout was one minute before sign-in.
    await desktop('signIn', 60000);
    await desktop('lock');
    await desktop('unlock');
    await load();
    await choose();
    await desktop('waitExpired', 85000);
    await desktop('assertNoApproval');
    await page.bringToFront();
    await emptyPassword();
    const popup = await openPopup();
    assert.match(await popup.locator('#status').innerText(), /locked|Unlock Vault/i);
    assert.equal(await popup.locator('#totp-code').innerText(), '');
    await popup.close();
    await desktop('unlock');
  });
  if (sectionSelected('E12', selectedCases)) {
    await desktop('signIn', 60000);
    await desktop('lock');
    await desktop('unlock');
  }
  await record('E12-desktop-password-clipboard-timer-and-lock', async () => {
    await desktop('copyPassword');
    await desktop('clipboardAssertPassword');
    await new Promise(done => setTimeout(done, 31000));
    await desktop('clipboardAssertEmpty');
    await desktop('copyPassword');
    await desktop('clipboardAssertPassword');
    await desktop('lock');
    await desktop('clipboardAssertEmpty');
    await desktop('unlock');
  });
  for (const kind of ['OTP', 'password']) for (const replacement of ['same-text', 'changed-text']) for (const trigger of ['timer', 'lock']) {
    await record(`E12-${kind}-${replacement}-replacement-survives-${trigger}`, async () => {
      if (kind === 'OTP') {
        await load();
        await copyApprovedCode();
      } else {
        await desktop('copyPassword');
        await desktop('clipboardAssertPassword');
      }
      await desktop(replacement === 'same-text' ? 'clipboardReplaceSame' : 'clipboardSetSentinel');
      if (trigger === 'timer') await new Promise(done => setTimeout(done, 31000));
      else await desktop('lock');
      await desktop('clipboardAssertSentinel'); // Sequence and hash prove the external replacement survived cleanup.
      await desktop('clipboardClearSentinel');
      if (trigger === 'lock') await desktop('unlock');
    });
  }
  await record('E14-desktop-close-cancels-pending-approval', async () => {
    await load();
    await choose();
    await desktop('closeDesktop');
    await page.bringToFront();
    await emptyPassword();
    const popup = await openPopup();
    assert.equal(await popup.locator('#status').getAttribute('data-state'), 'error');
    assert.equal(await popup.locator('.account').count(), 0);
    assert.equal(await popup.locator('#totp-code').innerText(), '');
    await popup.close();
  });
  assert(results.length > 0, 'No E2E case matched the requested prefix');
  campaignComplete = true;
  if (results.some(result => result.status === 'blocked')) process.exitCode = 2;
} catch (error) {
  campaignError = safeFailure(error);
  if (!results.some(result => result.status === 'failed')) {
    results.push({ id: `campaign-after-${caseId}`, status: 'failed', layer: 'campaign-control', error: campaignError });
  }
  console.error(`FAIL CAMPAIGN: ${campaignError}`);
  process.exitCode = 1;
} finally {
  const cleanup = await Promise.allSettled([context?.close(), server.listening ? new Promise(closed => server.close(closed)) : undefined]);
  for (const outcome of cleanup) if (outcome.status === 'rejected') { campaignError ??= safeFailure(outcome.reason); process.exitCode = 1; }
  await writeEvidence();
}
