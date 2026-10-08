import assert from 'node:assert/strict';
import { build } from 'esbuild';
function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), removeListener: listener => { const index = listeners.indexOf(listener); if (index >= 0) listeners.splice(index, 1); }, emit: (...args) => listeners.forEach(listener => listener(...args)) };
}
const messages = event();
const connections = event();
let nativeConnections = 0, nativeDisconnects = 0;
let monotonic = 1000;
const realPerformance = globalThis.performance;
globalThis.performance = { now: () => monotonic };
let delayedAction, delayedReply;
let malformed = false;
let hasPassword = true;
const nav = event();
let currentUrl = 'https://example.com/login';
let documentId = 'document-one';
let changeDuringSecret = false;
let nativeError;
let frameHook;
let nativeHook;
let frameReads = 0;
const nativeActions = [], deliveries = [];
const id = '11111111-1111-1111-1111-111111111111';
globalThis.chrome = {
  runtime: {
    id: 'official', getURL: path => 'chrome-extension://official/' + path, onMessage: messages, onConnect: connections,
    connectNative: () => {
      nativeConnections++;
      const onMessage = event(), onDisconnect = event();
      return {
        onMessage, onDisconnect, disconnect() { nativeDisconnects++; },
        postMessage(request) {
          nativeActions.push(request.action);
          const reply = () => {
            if (request.action === 'getCredentialSecret' && changeDuringSecret) {
              currentUrl += '#changed'; nav.emit({ tabId: 1, frameId: 0 });
            }
            nativeHook?.(request.action);
            const result = request.action === 'findCredentials' ? { credentials: [{ id, title: 'Synthetic', username: 'test', hasPassword, hasTotp: true }], truncated: false }
              : request.action === 'getCredentialSecret' ? { username: 'test', password: 'synthetic' }
              : request.action === 'getCredentialTotp' ? { code: '001234', periodSeconds: 30, expiresAtUnixMs: Math.floor(Date.now() / 30000) * 30000 + 30000 }
              : request.action === 'copyCredentialTotp' ? { copied: true } : { shown: true };
            onMessage.emit(malformed ? { invalid: true } : nativeError
              ? { version: 2, requestId: request.requestId, ok: false, error: nativeError }
              : { version: 2, requestId: request.requestId, ok: true, result });
          };
          if (request.action === delayedAction) delayedReply = reply;
          else queueMicrotask(reply);
        }
      };
    }
  },
  tabs: {
    query: async () => [{ id: 1, windowId: 1, url: currentUrl }],
    sendMessage: async (tabId, message, options) => {
      deliveries.push({ tabId, message, options }); return { ok: true };
    },
    onActivated: event(), onRemoved: event(), onDetached: event(), onUpdated: event()
  },
  webNavigation: {
    getFrame: async () => {
      const snapshot = { url: currentUrl, documentId, documentLifecycle: 'active' };
      frameHook?.(++frameReads);
      return snapshot;
    },
    onBeforeNavigate: nav, onCommitted: event(), onHistoryStateUpdated: event(), onReferenceFragmentUpdated: event()
  },
  windows: { onFocusChanged: event(), onRemoved: event() },
  action: { openPopup: async () => {} }
};
await build({ entryPoints: ['src/worker.ts'], bundle: true, outfile: 'dist/test/worker.js', format: 'esm', platform: 'node', define: { NATIVE_HOST: '"test.host"' } });
await import('../dist/test/worker.js');
const popup = { id: 'official', url: chrome.runtime.getURL('popup.html') };
const content = { id: 'official', frameId: 0, documentId, tab: { id: 1 }, url: currentUrl };
let session;
let requestNumber = 0;
function connect(sender = popup) {
  const onMessage = event(), onDisconnect = event();
  const pending = new Map();
  const port = { name: 'popup-session', sender, onMessage, onDisconnect, disconnected: false,
    postMessage(value) {
      if (value.event === 'invalidated') { for (const done of pending.values()) done({ ok: false, error: value.error }); pending.clear(); }
      if (value.requestId) { const done = pending.get(value.requestId); pending.delete(value.requestId); done?.(value); }
    },
    disconnect() { this.disconnected = true; onDisconnect.emit(); },
    send(message) { return new Promise(resolve => { const requestId = String(++requestNumber); pending.set(requestId, resolve); onMessage.emit({ requestId, message }); }); }
  };
  connections.emit(port);
  return port;
}
session = connect();
const send = (message, sender = popup) => sender === popup ? session.send(message) : new Promise(resolve => messages.emit(message, sender, resolve));
assert.equal((await send({ action: 'discover' }, content)).ok, false);
assert.equal((await send({ action: 'select', id }, content)).ok, false);
assert.equal(nativeActions.length, 0, 'content cannot trigger discovery/retrieval');
let discovery = await send({ action: 'discover' });
assert.equal(discovery.ok, true);
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).ok, true);
assert.deepEqual(nativeActions, ['findCredentials', 'getCredentialSecret']);
assert.equal(deliveries.at(-1).options.documentId, documentId);
assert.equal(deliveries.at(-1).options.frameId, 0);
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).ok, false, 'consent cannot be replayed');
discovery = await send({ action: 'discover' });
changeDuringSecret = true;
const fillsBefore = deliveries.filter(item => item.message.action === 'fill').length;
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'targetChanged');
assert.equal(deliveries.filter(item => item.message.action === 'fill').length, fillsBefore, 'navigation discards the secret');
changeDuringSecret = false;
discovery = await send({ action: 'discover' });
documentId = 'document-two';
const secretsBefore = nativeActions.filter(action => action === 'getCredentialSecret').length;
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'targetChanged');
assert.equal(nativeActions.filter(action => action === 'getCredentialSecret').length, secretsBefore);
discovery = await send({ action: 'discover' });
nativeError = 'locked';
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'locked');
assert.equal(nativeActions.filter(action => action === 'getCredentialSecret').length, secretsBefore + 1, 'secret retrieval has no automatic retry');
nativeError = undefined;
for (const checkAt of [1, 2, 3]) {
  discovery = await send({ action: 'discover' });
  const beforeSecrets = nativeActions.filter(action => action === 'getCredentialSecret').length;
  const beforeFills = deliveries.filter(item => item.message.action === 'fill').length;
  frameReads = 0;
  frameHook = read => { if (read === checkAt) nav.emit({ tabId: 1, frameId: 0 }); };
  assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'targetChanged');
  assert.equal(nativeActions.filter(action => action === 'getCredentialSecret').length, beforeSecrets + (checkAt === 3 ? 1 : 0));
  assert.equal(deliveries.filter(item => item.message.action === 'fill').length, beforeFills, 'stale target API cannot bypass invalidation');
  frameHook = undefined;
}
for (const checkAt of [1, 2]) {
  const before = nativeActions.length;
  frameReads = 0;
  frameHook = read => { if (read === checkAt) nav.emit({ tabId: 1, frameId: 0 }); };
  assert.equal((await send({ action: 'discover' })).error, 'targetChanged');
  assert.equal(nativeActions.length, before + (checkAt === 2 ? 1 : 0));
  frameHook = undefined;
}
const realNow = Date.now;
let now = realNow();
Date.now = () => now;
try {
  discovery = await send({ action: 'discover' });
  const before = nativeActions.length;
  frameHook = () => { now += 60001; };
  assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'targetChanged');
  assert.equal(nativeActions.length, before, 'expiry during await prevents secret retrieval');
} finally { Date.now = realNow; frameHook = undefined; }
const backgroundChanges = () => {
  for (const navigation of [nav, chrome.webNavigation.onCommitted, chrome.webNavigation.onHistoryStateUpdated, chrome.webNavigation.onReferenceFragmentUpdated])
    navigation.emit({ tabId: 2, frameId: 0 });
  chrome.tabs.onUpdated.emit(2, { status: 'loading', url: 'https://other.example' });
  chrome.tabs.onRemoved.emit(2);
  chrome.tabs.onActivated.emit({ tabId: 2, windowId: 2 });
};
nativeHook = backgroundChanges;
discovery = await send({ action: 'discover' });
assert.equal(discovery.ok, true, 'background tabs do not cancel pending discovery');
backgroundChanges();
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).ok, true, 'background tabs do not cancel consent');
nativeHook = undefined;
for (const cancel of [() => chrome.tabs.onUpdated.emit(1, { status: 'loading' }), () => chrome.tabs.onRemoved.emit(1),
  () => chrome.tabs.onActivated.emit({ tabId: 2, windowId: 1 }), () => chrome.windows.onFocusChanged.emit(2)]) {
  discovery = await send({ action: 'discover' });
  cancel();
  assert.equal((await send({ action: 'select', token: discovery.result.token, id })).error, 'targetChanged');
}
// TOTP consent is popup-only, capability-gated and bound to discovered accounts.
for (const action of ['viewTotp', 'refreshTotp', 'copyTotp']) assert.equal((await send({ action, id }, content)).ok, false);
const unofficial = connect({ id: 'official', url: chrome.runtime.getURL('other.html') });
assert(unofficial.disconnected);
discovery = await send({ action: 'discover' });
assert.equal(nativeActions.at(-1), 'findCredentials', 'discovery never retrieves OTP');
assert.equal((await send({ action: 'viewTotp', token: discovery.result.token, id: '22222222-2222-2222-2222-222222222222' })).ok, false);
discovery = await send({ action: 'discover' });
hasPassword = false;
discovery = await send({ action: 'discover' });
const beforePasswordOnly = nativeActions.length;
assert.equal((await send({ action: 'select', token: discovery.result.token, id })).ok, false);
assert.equal(nativeActions.length, beforePasswordOnly, 'TOTP-only account cannot fill a password');
discovery = await send({ action: 'discover' });
const connectionBeforeTicks = nativeConnections;
let value = await send({ action: 'viewTotp', token: discovery.result.token, id });
assert.equal(value.result.code, '001234');
assert.equal(deliveries.filter(item => item.message.action === 'fill').length >= 1, true);
monotonic += 1000;
assert.equal((await send({ action: 'refreshTotp', token: discovery.result.token, id })).ok, true);
assert.equal((await send({ action: 'copyTotp', token: discovery.result.token, id })).result.copied, true);
assert.equal(nativeConnections, connectionBeforeTicks, 'discovery, ticks and copy reuse one native connection');
const beforeFastTick = nativeActions.length;
assert.equal((await send({ action: 'refreshTotp', token: discovery.result.token, id })).error, 'invalidRequest');
assert.equal(nativeActions.length, beforeFastTick, 'read rate is enforced in the trusted worker');

// Navigation during a get discards its result; failures never trigger retries.
discovery = await send({ action: 'discover' });
delayedAction = 'getCredentialTotp';
const navigationRead = send({ action: 'viewTotp', token: discovery.result.token, id });
await new Promise(resolve => setImmediate(resolve));
nav.emit({ tabId: 1, frameId: 0 });
delayedReply();
assert.equal((await navigationRead).ok, false);
delayedAction = undefined;
for (const failure of ['locked', 'desktopUnavailable', 'malformed']) {
  discovery = await send({ action: 'discover' });
  const before = nativeActions.length;
  nativeError = failure === 'malformed' ? undefined : failure;
  malformed = failure === 'malformed';
  assert.equal((await send({ action: 'viewTotp', token: discovery.result.token, id })).ok, false);
  assert.equal((await send({ action: 'refreshTotp', token: discovery.result.token, id })).ok, false);
  assert.equal(nativeActions.length, before + 1, 'failure clears access without automatic retries');
  nativeError = undefined; malformed = false;
}
// A closed popup invalidates an outstanding native operation and discards its late response.
discovery = await send({ action: 'discover' });
delayedAction = 'getCredentialTotp';
void send({ action: 'viewTotp', token: discovery.result.token, id });
await new Promise(resolve => setImmediate(resolve));
assert.equal((await send({ action: 'closeTotp', token: discovery.result.token, id })).result.closed, true,
  'local close cancels a pending native read immediately');
const cancelledReply = delayedReply;
delayedAction = undefined;
discovery = await send({ action: 'discover' });
cancelledReply();
await new Promise(resolve => setImmediate(resolve));
assert.equal((await send({ action: 'viewTotp', token: discovery.result.token, id })).ok, true,
  'late cancellation cannot revoke a new explicit discovery');
discovery = await send({ action: 'discover' });
delayedAction = 'getCredentialTotp';
void send({ action: 'viewTotp', token: discovery.result.token, id });
await new Promise(resolve => setImmediate(resolve));
const beforeClose = nativeDisconnects;
session.disconnect();
delayedReply();
await new Promise(resolve => setImmediate(resolve));
assert.equal(nativeDisconnects, beforeClose + 1);
delayedAction = undefined;
session = connect();
assert.equal((await send({ action: 'copyTotp', token: discovery.result.token, id })).ok, false);
session.disconnect();
globalThis.performance = realPerformance;
console.log('Passed worker password/TOTP authorization, session cleanup, connection reuse, rate limit, navigation/expiry races and no-retry checks.');
