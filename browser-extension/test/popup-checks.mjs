import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { runInNewContext } from 'node:vm';

// The real popup script runs against a minimal DOM and deterministic clock.
class Element {
  children = []; dataset = {}; listeners = {}; attributes = {}; textContent = ''; disabled = false; hidden = true; value = 0; max = 1;
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items; }
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
  querySelectorAll() { return this.children.flatMap(child => child.tag === 'button' ? [child] : child.querySelectorAll()); }
  click() { if (!this.disabled) this.listeners.click?.(); }
  focus() { this.focused = true; }
}
function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), emit: (...args) => listeners.forEach(listener => listener(...args)) };
}
const compiled = await build({ entryPoints: ['src/popup.ts'], bundle: true, write: false, format: 'iife' });
const settle = () => new Promise(resolve => setImmediate(resolve));
const account = { id: '11111111-1111-1111-1111-111111111111', title: '<Account>', username: 'synthetic', hasPassword: true, hasTotp: true };
const discovery = accounts => ({ ok: true, result: { token: 'consent', origin: 'https://example.com', accounts, truncated: false } });
async function scenario(result, resumed = { resumed: false }) {
  const elements = Object.fromEntries(['status', 'origin', 'accounts', 'show-app', 'refresh', 'totp-panel', 'totp-title', 'totp-username',
    'totp-code', 'totp-countdown', 'totp-progress', 'totp-copy', 'totp-close', 'cancel'].map(id => [id, new Element()]));
  const requests = [], callbacks = new Map(), timeouts = new Map(), windowEvents = {};
  let timerId = 0, now = 1900000010000, monotonic = 1000, replyHook, disconnected = false;
  const onMessage = event(), onDisconnect = event();
  const port = {
    onMessage, onDisconnect,
    postMessage(envelope) {
      requests.push(envelope.message);
      const reply = replyHook?.(envelope.message) ?? (envelope.message.action === 'resume' ? { ok: true, result: resumed }
        : envelope.message.action === 'discover' ? result
        : envelope.message.action === 'copyTotp' ? { ok: true, result: { copied: true } }
        : envelope.message.action === 'viewTotp' ? { ok: true, result: { code: '001234', periodSeconds: 30, expiresAtUnixMs: Math.floor(now / 30000) * 30000 + 30000 } }
        : { ok: true, result: { shown: true } });
      if (reply instanceof Promise) void reply.then(value => onMessage.emit({ requestId: envelope.requestId, ...value }));
      else queueMicrotask(() => onMessage.emit({ requestId: envelope.requestId, ...reply }));
    },
    disconnect() { disconnected = true; onDisconnect.emit(); }
  };
  runInNewContext(compiled.outputFiles[0].text, {
    HTMLElement: Element, HTMLParagraphElement: Element, HTMLDivElement: Element, HTMLButtonElement: Element, HTMLProgressElement: Element,
    document: { getElementById: id => elements[id], createElement: tag => { const element = new Element(); element.tag = tag; return element; } },
    window: { addEventListener: (name, callback) => { windowEvents[name] = callback; } },
    Date: { now: () => now }, performance: { now: () => monotonic }, crypto: { randomUUID: () => String(++timerId) },
    setTimeout: callback => { const id = ++timerId; timeouts.set(id, callback); return id; }, clearTimeout: id => timeouts.delete(id),
    setInterval: callback => { const id = ++timerId; callbacks.set(id, callback); return id; }, clearInterval: id => callbacks.delete(id),
    chrome: { runtime: { connect: () => port } }
  });
  await settle();
  return { elements, requests, callbacks, windowEvents, port,
    setReply: value => { replyHook = value; },
    tick: async (wallDelta, monoDelta = wallDelta) => { now += wallDelta; monotonic += monoDelta; [...callbacks.values()].forEach(callback => callback()); await settle(); },
    get disconnected() { return disconnected; }
  };
}
const ready = await scenario(discovery([account]));
assert.equal(ready.elements.status.dataset.state, 'ready');
assert.equal(ready.elements.accounts.children.length, 1);
assert.deepEqual(ready.requests.map(request => request.action), ['resume', 'discover'], 'opening recovers or discovers metadata only');
const buttons = ready.elements.accounts.querySelectorAll();
assert.equal(buttons[0].textContent, 'Fill');
assert.equal(buttons[1].textContent, 'View TOTP');
buttons[0].click();
await settle();
assert.equal(ready.elements.status.dataset.state, 'success');
assert.equal(ready.elements.accounts.children.length, 0);
assert.equal(ready.requests[2].action, 'select');
assert.equal(ready.elements.refresh.disabled, false);
const locked = await scenario({ ok: false, error: 'locked' });
assert.equal(locked.elements.status.dataset.state, 'error');
assert.match(locked.elements.status.textContent, /Unlock Vault/);
locked.elements['show-app'].click();
await settle();
assert.equal(locked.elements.status.dataset.state, 'ready');
const empty = await scenario(discovery([]));
assert.equal(empty.elements.status.dataset.state, 'empty');
const unavailable = await scenario({ ok: false, error: 'desktopUnavailable' });
assert.match(unavailable.elements.status.textContent, /Start the matching desktop app/);
assert.equal(unavailable.elements['show-app'].disabled, false);

const otp = await scenario(discovery([{ ...account, hasPassword: false }]));
assert.equal(otp.elements.accounts.querySelectorAll().length, 1, 'TOTP-only accounts have no Fill action');
otp.elements.accounts.querySelectorAll()[0].click();
await settle();
assert.equal(otp.elements['totp-panel'].hidden, false);
assert.equal(otp.elements.accounts.hidden, true);
assert.equal(otp.elements['totp-code'].textContent, '001 234', 'leading zeros survive grouping');
assert.equal(otp.elements['totp-countdown'].textContent, '10s');
assert.equal(otp.callbacks.size, 1);
await otp.tick(900);
assert.equal(otp.requests.filter(request => request.action === 'refreshTotp').length, 0);
await otp.tick(100);
assert.equal(otp.requests.filter(request => request.action === 'refreshTotp').length, 0, 'countdown never requests another desktop approval');
otp.elements['totp-copy'].click();
await settle();
assert.equal(otp.requests.at(-1).action, 'copyTotp', 'copy requests a fresh desktop code');
assert.equal('code' in otp.requests.at(-1), false);
assert.equal(otp.elements.status.textContent, 'Copied the current code.');
await otp.tick(1000);
assert.equal(otp.elements.status.textContent, 'Copied the current code.', 'countdown preserves copy confirmation');

// Expired codes and accessible text disappear without starting a new read.
const beforeExpiration = otp.requests.length;
await otp.tick(8000);
assert.equal(otp.elements['totp-code'].textContent, '—');
assert.equal(otp.elements['totp-code'].attributes['aria-label'], undefined);
assert.equal(otp.requests.length, beforeExpiration, 'expiry never refreshes or replays a code request');
await otp.tick(-70000, 100);
assert.equal(otp.elements['totp-code'].textContent, '—', 'backward clock changes clear the previous time-window code');
otp.port.onMessage.emit({ event: 'invalidated', error: 'targetChanged' });
assert.equal(otp.elements['totp-panel'].hidden, true);
assert.equal(otp.elements['totp-code'].textContent, '');
assert.equal(otp.callbacks.size, 0);
const requestsBefore = otp.requests.length;
await otp.tick(5000);
assert.equal(otp.requests.length, requestsBefore, 'invalidated sessions never retry');

const failed = await scenario(discovery([account]));
failed.setReply(message => message.action === 'viewTotp' ? { ok: false, error: 'locked' } : undefined);
failed.elements.accounts.querySelectorAll()[1].click();
await settle();
assert.equal(failed.elements['totp-panel'].hidden, true);
assert.equal(failed.callbacks.size, 0);
await failed.tick(5000);
assert.equal(failed.requests.length, 3, 'failed initial read does not retry');
const closed = await scenario(discovery([account]));
let afterClose;
closed.setReply(message => message.action === 'viewTotp' ? new Promise(resolve => { afterClose = resolve; }) : undefined);
closed.elements.accounts.querySelectorAll()[1].click();
await settle();
closed.windowEvents.pagehide();
afterClose({ ok: true, result: { code: '654321', periodSeconds: 30, expiresAtUnixMs: 1900000020000 } });
await settle();
assert.equal(closed.disconnected, true);
assert.equal(closed.elements['totp-code'].textContent, '');
assert.equal(closed.callbacks.size, 0, 'popup closure clears the timer and late reads');
const canceled = await scenario(discovery([account]));
let afterCancel;
canceled.setReply(message => message.action === 'viewTotp' ? new Promise(resolve => { afterCancel = resolve; }) : undefined);
canceled.elements.accounts.querySelectorAll()[1].click();
await settle();
assert.equal(canceled.elements['totp-close'].disabled, false, 'local close remains available during a pending read');
canceled.elements['totp-close'].click();
assert.equal(canceled.elements['totp-code'].textContent, '');
assert.equal(canceled.callbacks.size, 0);
await settle();
afterCancel({ ok: true, result: { code: '654321', periodSeconds: 30, expiresAtUnixMs: 1900000020000 } });
await settle();
assert.equal(canceled.elements['totp-panel'].hidden, true, 'late result cannot reopen a locally closed panel');
assert.equal(canceled.elements.accounts.hidden, false);
assert.equal(canceled.elements.accounts.querySelectorAll()[1].focused, true, 'focus returns to the selected account');
const state = { token: 'consent', origin: 'https://example.com', accounts: [account], truncated: false,
  busy: true, selectedId: account.id, action: 'viewTotp' };
const recovered = await scenario(discovery([account]), { resumed: true, interaction: state });
assert.deepEqual(recovered.requests.map(request => request.action), ['resume'], 'reopening does not trigger discovery or native replay');
assert.equal(recovered.elements.cancel.hidden, false);
assert.equal(recovered.elements.refresh.disabled, true);
recovered.port.onMessage.emit({ event: 'updated', interaction: { ...state, busy: false,
  result: { code: '00001234', periodSeconds: 60, expiresAtUnixMs: 1900000070000 } } });
assert.equal(recovered.elements['totp-code'].textContent, '0000 1234');
assert.equal(recovered.elements.refresh.disabled, false);
assert.equal(recovered.elements.cancel.hidden, true);
await recovered.tick(70000);
assert.equal(recovered.elements['totp-code'].textContent, '—');
assert.equal(recovered.requests.length, 1);
const recoverFill = await scenario(discovery([account]), { resumed: true,
  interaction: { ...state, action: 'select', accounts: [], busy: false, result: { filled: true } } });
assert.match(recoverFill.elements.status.textContent, /Filled/);
assert.equal(recoverFill.requests.length, 1);
const cancelFill = await scenario(discovery([account]), { resumed: true, interaction: { ...state, action: 'select' } });
cancelFill.elements.cancel.click();
await settle();
assert.equal(cancelFill.requests[1].action, 'cancel', 'reopened password approval can be explicitly cancelled');
console.log('Passed popup one-shot codes, grouping, countdown/clock expiry, explicit copy, pending recovery and cancellation checks.');
