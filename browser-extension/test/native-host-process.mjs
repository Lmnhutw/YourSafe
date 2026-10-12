// Runs the real adjacent NativeHost against a marked disposable desktop, never a mock pipe server.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { writeFile, mkdir } from 'node:fs/promises';
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) =>
  index % 2 === 0 ? [...pairs, [value.replace(/^--/, ''), all[index + 1]]] : pairs, []));
const host = resolve(args.host ?? '');
const testDirectory = resolve(args['test-directory'] ?? '');
const appPid = Number(args['app-pid']);
if (!args.host || !args['test-directory'] || !Number.isSafeInteger(appPid) || appPid <= 0)
  throw new Error('Provide --host, --app-pid and --test-directory for the disposable app.');
const root = resolve(import.meta.dirname, '../..');
const pwsh = args.pwsh ?? 'pwsh';
const control = resolve(root, 'scripts/AutofillTestControl.ps1');
const origin = args.origin ?? 'http://127.0.0.1:49173';
const results = [];
function ui(action) {
  execFileSync(pwsh, ['-NoProfile', '-File', control, '-Action', action,
    '-ProcessId', String(appPid), '-TestDirectory', testDirectory], { timeout: action === 'signIn' ? 60000 : 20000, stdio: 'pipe' });
}
function request(action, payload = {}) { return { version: 2, requestId: randomUUID(), action, payload }; }
function frame(body) {
  const bytes = typeof body === 'string' ? Buffer.from(body) : Buffer.isBuffer(body) ? body : Buffer.from(JSON.stringify(body));
  const header = Buffer.alloc(4); header.writeUInt32LE(bytes.length);
  return Buffer.concat([header, bytes]);
}
function start() {
  const child = spawn(host, ['chrome-extension://cmfnnjellknkpnbooenlahijcmalbifg/'],
    { cwd: dirname(host), stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const chunks = []; let size = 0; let stderr = 0;
  child.stdout.on('data', bytes => { size += bytes.length; if (size > 65540) child.kill(); else chunks.push(bytes); });
  child.stderr.on('data', bytes => { stderr += bytes.length; });
  child.stdin.on('error', () => {});
  const ended = new Promise((resolveEnded, reject) => {
    child.once('error', reject);
    child.once('exit', code => resolveEnded(code));
  });
  return { child, chunks, ended, get stderr() { return stderr; } };
}
async function bounded(task, milliseconds, message) {
  let timeout;
  try { return await Promise.race([task, new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error(message)), milliseconds); })]); }
  finally { clearTimeout(timeout); }
}
function parseFrames(chunks) {
  const bytes = Buffer.concat(chunks); const frames = [];
  try {
    let position = 0;
    while (position + 4 <= bytes.length) {
      const length = bytes.readUInt32LE(position); position += 4;
      assert(length > 0 && length <= 65536, 'Invalid response length');
      if (position + length > bytes.length) break;
      frames.push(JSON.parse(bytes.subarray(position, position + length).toString('utf8')));
      position += length;
    }
    return frames;
  } finally { bytes.fill(0); }
}
async function response(running, ordinal = 0) {
  let read;
  try {
    return await bounded(new Promise(resolveResponse => {
      read = () => { const frames = parseFrames(running.chunks); if (frames.length > ordinal) resolveResponse(frames[ordinal]); };
      running.child.stdout.on('data', read); read();
    }), 8000, 'NativeHost response timed out');
  } finally { running.child.stdout.off('data', read); }
}
async function withHost(action) {
  const running = start();
  try { return await action(running); }
  finally {
    running.child.stdin.destroy();
    if (running.child.exitCode === null) running.child.kill();
    await bounded(running.ended.catch(() => {}), 3000, 'Host cleanup timed out');
    for (const bytes of running.chunks) bytes.fill(0);
  }
}
async function check(name, action) {
  try { await action(); results.push({ name, status: 'verified', testLayer: 'Real NativeHost process and disposable WinUI' }); console.log(`PASS: ${name}`); }
  catch (error) { results.push({ name, status: 'failed', error: error.message }); throw error; }
}
ui('validate');
try {
  const uuid = randomUUID();
  const header = Buffer.alloc(4); header.writeUInt32LE(65537);
  const malformed = [
    ['zero length', Buffer.alloc(4)], ['oversized frame', header],
    ['truncated header', Buffer.from([1, 0])], ['truncated body', Buffer.from([4, 0, 0, 0, 123])],
    ['malformed UTF-8', frame(Buffer.from([0xff]))], ['malformed JSON', frame('{')],
    ['duplicate property', frame(`{"version":2,"requestId":"${uuid}","action":"ping","action":"getStatus","payload":{}}`)],
    ['unknown property', frame({ ...request('ping'), unknown: true })],
    ['wrong version', frame({ ...request('ping'), version: 1 })],
    ['unknown action', frame(request('unrecognized'))]
  ];
  for (const [name, bytes] of malformed) await check(name, () => withHost(async running => {
    running.child.stdin.end(bytes);
    const code = await bounded(running.ended, 6000, 'Malformed input did not terminate');
    assert.notEqual(code, 0, 'Malformed input was accepted');
    assert.equal(parseFrames(running.chunks).some(response => response.ok), false, 'Malformed input emitted a success');
    assert.equal(running.stderr, 0, 'NativeHost logged error payloads');
  }));
  ui('signIn');
  ui('lock');
  ui('unlock');
  ui('enable');
  let credentialId;
  await check('fragmented valid discovery through the real desktop', () => withHost(async running => {
    const bytes = frame(request('findCredentials', { origin }));
    for (const byte of bytes) { running.child.stdin.write(Buffer.from([byte])); }
    const discovery = await response(running);
    assert.equal(discovery.ok, true, 'Discovery failed');
    credentialId = discovery.result.credentials.find(item => item.hasPassword)?.id;
    assert.equal(typeof credentialId, 'string', 'Missing synthetic password credential');
    running.child.stdin.end();
    await bounded(running.ended, 6000, 'Discovery host did not exit');
  }));
  for (const invalidation of ['EOF', 'extra frame']) await check(`pending approval cancelled by ${invalidation}`, () => withHost(async running => {
    running.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
    ui('waitApproval');
    if (invalidation === 'EOF') running.child.stdin.end();
    else running.child.stdin.write(frame(request('ping')));
    await bounded(running.ended, 6000, 'Pending request did not cancel');
    assert.equal(parseFrames(running.chunks).some(response => response.ok), false, 'Cancelled request emitted a success');
    assert.equal(running.stderr, 0, 'Cancellation logged sensitive data');
    ui('waitApprovalGone');
  }));
  await check('competing client denied while the real approval remains pending', () => withHost(async first => {
    first.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
    ui('waitApproval');
    await withHost(async second => {
      second.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
      assert.equal((await response(second)).ok, false, 'A second client inherited the pending approval');
    });
    await withHost(async probe => {
      probe.child.stdin.write(frame(request('getStatus')));
      assert.equal((await response(probe)).ok, true, 'Approval blocked the second pipe listener');
    });
    ui('waitApproval');
    ui('cancel');
    assert.equal((await response(first)).ok, false, 'Cancelled first client received a secret');
  }));
  await check('terminated NativeHost cancels the actual desktop prompt', () => withHost(async running => {
    running.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
    ui('waitApproval');
    running.child.kill();
    await bounded(running.ended, 6000, 'Terminated NativeHost did not exit');
    ui('waitApprovalGone');
    assert.equal(parseFrames(running.chunks).some(item => item.ok), false, 'Terminated client received a secret');
  }));
  await check('a second request on the same host needs a fresh desktop approval', () => withHost(async running => {
    running.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
    ui('waitApproval');
    ui('approve');
    assert.equal((await response(running)).ok, true, 'Synthetic first approval failed');
    running.child.stdin.write(frame(request('getCredentialSecret', { origin, credentialId })));
    ui('waitApproval');
    assert.equal(parseFrames(running.chunks).length, 1, 'The second request reused consent');
    ui('cancel');
    assert.equal((await response(running, 1)).ok, false, 'Second denied approval emitted a secret');
    assert.equal(running.stderr, 0, 'Host logged protocol data');
  }));
  await check('discovery admission is bounded across new NativeHost clients', async () => {
    console.log('PACE: starting a fresh real 30/minute admission window.');
    await new Promise(done => setTimeout(done, 61000));
    let allowed = 0;
    for (let index = 0; index < 31; index++) await withHost(async running => {
      running.child.stdin.write(frame(request('findCredentials', { origin })));
      if ((await response(running)).ok) allowed++;
      assert.equal(running.stderr, 0, 'Admission failure logged protocol data');
    });
    assert.equal(allowed, 30, 'New host connections bypassed or unexpectedly exhausted admission');
    ui('assertNoApproval');
  });
} finally {
  await mkdir(resolve(testDirectory, 'ui-results'), { recursive: true });
  await writeFile(resolve(testDirectory, 'ui-results/native-host-process.json'), JSON.stringify({ host, appPid, results }, null, 2));
}
