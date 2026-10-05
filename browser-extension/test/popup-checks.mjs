import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { runInNewContext } from 'node:vm';

// Minimal DOM exercises the real popup script without browser profiles or secrets.
class Element {
  children = []; dataset = {}; listeners = {}; textContent = ''; disabled = false;
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items; }
  setAttribute() {}
  addEventListener(name, fn) { this.listeners[name] = fn; }
  querySelectorAll() { return this.children; }
  click() { this.listeners.click?.(); }
}
const compiled = await build({ entryPoints: ['src/popup.ts'], bundle: true, write: false, format: 'iife' });
const settle = () => new Promise(resolve => setImmediate(resolve));
async function scenario(result) {
  const elements = Object.fromEntries(['status', 'origin', 'accounts', 'show-app', 'refresh'].map(id => [id, new Element()]));
  const requests = [];
  runInNewContext(compiled.outputFiles[0].text, {
    HTMLElement: Element, HTMLParagraphElement: Element, HTMLDivElement: Element, HTMLButtonElement: Element,
    document: { getElementById: id => elements[id], createElement: () => new Element() },
    chrome: { runtime: { sendMessage: async message => {
      requests.push(message);
      return message.action === 'discover' ? result : { ok: true, result: { shown: true } };
    } } }
  });
  await settle();
  return { elements, requests };
}
const ready = await scenario({ ok: true, result: { token: 'consent', origin: 'https://example.com', accounts: [{ id: '11111111-1111-1111-1111-111111111111', title: '<Account>', username: 'synthetic' }], truncated: false } });
assert.equal(ready.elements.status.dataset.state, 'ready');
assert.equal(ready.elements.accounts.children.length, 1);
ready.elements.accounts.children[0].click();
await settle();
assert.equal(ready.elements.status.dataset.state, 'success');
assert.equal(ready.elements.accounts.children.length, 0);
assert.equal(ready.requests[1].action, 'select');
assert.equal(ready.elements.refresh.disabled, false);
const locked = await scenario({ ok: false, error: 'locked' });
assert.equal(locked.elements.status.dataset.state, 'error');
assert.match(locked.elements.status.textContent, /Unlock Vault/);
locked.elements['show-app'].click();
await settle();
assert.equal(locked.elements.status.dataset.state, 'ready');
const empty = await scenario({ ok: true, result: { token: 'consent', origin: 'https://example.com', accounts: [], truncated: false } });
assert.equal(empty.elements.status.dataset.state, 'empty');
const unavailable = await scenario({ ok: false, error: 'desktopUnavailable' });
assert.match(unavailable.elements.status.textContent, /Start the matching desktop app/);
assert.equal(unavailable.elements['show-app'].disabled, false);
console.log('Passed popup ready, fill, locked, empty and disconnected recovery flows.');
