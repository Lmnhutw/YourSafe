import assert from 'node:assert/strict';
import { build } from 'esbuild';
function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), emit: (...args) => listeners.forEach(listener => listener(...args)) };
}
const messages = event();
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
    id: 'official', getURL: path => 'chrome-extension://official/' + path, onMessage: messages,
    connectNative: () => {
      const onMessage = event(), onDisconnect = event();
      return {
        onMessage, onDisconnect, disconnect() {},
        postMessage(request) {
          nativeActions.push(request.action);
          queueMicrotask(() => {
            if (request.action === 'getCredentialSecret' && changeDuringSecret) {
              currentUrl += '#changed'; nav.emit({ tabId: 1, frameId: 0 });
            }
            nativeHook?.(request.action);
            const result = request.action === 'findCredentials' ? { credentials: [{ id, title: 'Synthetic', username: 'test' }], truncated: false }
              : request.action === 'getCredentialSecret' ? { username: 'test', password: 'synthetic' } : { shown: true };
            onMessage.emit(nativeError
              ? { version: 1, requestId: request.requestId, ok: false, error: nativeError }
              : { version: 1, requestId: request.requestId, ok: true, result });
          });
        }
      };
    }
  },
  tabs: {
    query: async () => [{ id: 1, windowId: 1, url: currentUrl }],
    sendMessage: async (tabId, message, options) => {
      deliveries.push({ tabId, message, options }); return { ok: true };
    },
    onActivated: event(), onRemoved: event(), onUpdated: event()
  },
  webNavigation: {
    getFrame: async () => {
      const snapshot = { url: currentUrl, documentId, documentLifecycle: 'active' };
      frameHook?.(++frameReads);
      return snapshot;
    },
    onBeforeNavigate: nav, onCommitted: event(), onHistoryStateUpdated: event(), onReferenceFragmentUpdated: event()
  },
  windows: { onFocusChanged: event() },
  action: { openPopup: async () => {} }
};
await build({ entryPoints: ['src/worker.ts'], bundle: true, outfile: 'dist/test/worker.js', format: 'esm', platform: 'node', define: { NATIVE_HOST: '"test.host"' } });
await import('../dist/test/worker.js');
const popup = { id: 'official', url: chrome.runtime.getURL('popup.html') };
const content = { id: 'official', frameId: 0, documentId, tab: { id: 1 }, url: currentUrl };
const send = (message, sender = popup) => new Promise(resolve => messages.emit(message, sender, resolve));
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
console.log('Passed worker authorization, stale-await/expiry races, scoped tab cancellation and one-use consent checks.');
