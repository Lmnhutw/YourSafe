import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { runInNewContext } from 'node:vm';
function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), removeListener: listener => {
    const index = listeners.indexOf(listener); if (index >= 0) listeners.splice(index, 1);
  }, emit: (...args) => [...listeners].forEach(listener => listener(...args)) };
}
const compiled = await build({ entryPoints: ['src/native.ts'], bundle: true, write: false, format: 'iife',
  globalName: 'transport', define: { NATIVE_HOST: '"test.host"' } });
const ports = [], timeouts = new Map(), failures = [];
let sequence = 0;
const timeoutDurations = [];
const context = {
  TextEncoder, crypto: { randomUUID: () => '12345678-1234-1234-1234-' + String(++sequence).padStart(12, '0') },
  setTimeout: (callback, milliseconds) => { timeoutDurations.push(milliseconds); const id = ++sequence; timeouts.set(id, callback); return id; }, clearTimeout: id => timeouts.delete(id),
  chrome: { runtime: { connectNative: () => {
    const port = { onMessage: event(), onDisconnect: event(), messages: [], disconnected: false,
      postMessage(request) { this.messages.push(request); }, disconnect() { this.disconnected = true; } };
    ports.push(port);
    return port;
  } } }
};
runInNewContext(compiled.outputFiles[0].text, context);
const session = context.transport.nativeSession(error => failures.push(error));
const first = session.request('ping'), second = session.request('getStatus');
await Promise.resolve();
assert.equal(ports.length, 1);
assert.equal(ports[0].messages.length, 1, 'native requests serialize on the session connection');
const request = ports[0].messages[0];
ports[0].onMessage.emit({ version: 2, requestId: request.requestId, ok: true, result: { host: 'YourSafe' } });
await first;
await Promise.resolve();
assert.equal(ports[0].messages.length, 2);
assert.notEqual(ports[0].messages[0].requestId, ports[0].messages[1].requestId);
ports[0].onMessage.emit({ version: 2, requestId: ports[0].messages[1].requestId, ok: true, result: { unlocked: true } });
await second;
assert.equal(timeouts.size, 0);
session.close();
assert(ports[0].disconnected);
for (const kind of ['disconnect', 'timeout', 'malformed', 'oversized']) {
  const active = context.transport.nativeSession(error => failures.push(error));
  const result = active.request('ping');
  const rejected = assert.rejects(result, /desktopUnavailable|invalidRequest/);
  await Promise.resolve();
  const port = ports.at(-1);
  if (kind === 'disconnect') port.onDisconnect.emit();
  if (kind === 'timeout') [...timeouts.values()][0]();
  if (kind === 'malformed') port.onMessage.emit({ version: 1 });
  if (kind === 'oversized') port.onMessage.emit({ payload: 'x'.repeat(65536) });
  await rejected;
  assert(port.disconnected);
  assert.equal(port.messages.length, 1, 'failed native requests are never retried');
  assert.equal(timeouts.size, 0);
  await assert.rejects(active.request('ping'), /desktopUnavailable/);
}
assert.equal(failures.length, 4);
assert(timeoutDurations.every(milliseconds => milliseconds === 70000), 'native timeout leaves sixty seconds for desktop approval');
const closed = context.transport.nativeSession(() => assert.fail('explicit close is not a failure notification'));
const awaiting = closed.request('ping');
const discarded = assert.rejects(awaiting, /targetChanged/);
await Promise.resolve();
const oldPort = ports.at(-1), oldRequest = oldPort.messages[0];
closed.close('targetChanged');
oldPort.onMessage.emit({ version: 2, requestId: oldRequest.requestId, ok: true, result: { host: 'YourSafe' } });
await discarded;
assert.equal(timeouts.size, 0);
console.log('Passed native session serialization, request IDs, timeout, disconnect, strict frames, late responses and no retries.');
