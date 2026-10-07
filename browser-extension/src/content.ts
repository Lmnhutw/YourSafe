import { findFields, sameFields, fillFields, passwordField, type Fields } from './fields';
import { isSecret, record } from './protocol';

let focused: HTMLInputElement | undefined;
let prepared: { token: string; url: string; fields: Fields; expiresAt: number } | undefined;
document.addEventListener('focusin', event => {
  if (event.target instanceof HTMLInputElement) focused = event.target;
});
const hint = document.createElement('button');
hint.type = 'button';
hint.textContent = 'Fill with YourSafe';
hint.setAttribute('aria-label', 'Choose an account in YourSafe');
hint.style.cssText = 'position:fixed;right:16px;bottom:16px;z-index:2147483647;padding:10px 14px;border:1px solid #64748b;border-radius:8px;background:#122238;color:#fff;font:14px system-ui;cursor:pointer';
hint.addEventListener('click', () => {
  void chrome.runtime.sendMessage({ action: 'openPopup' }).then((value: unknown) => {
    if (!record(value) || value.ok !== true || !record(value.result) || value.result.opened !== true)
      hint.textContent = 'Open YourSafe from the browser toolbar';
  }).catch(() => { hint.textContent = 'Open YourSafe from the browser toolbar'; });
});
function updateHint(): void {
  const visiblePassword = [...document.querySelectorAll<HTMLInputElement>('input[type=password]')]
    .some(passwordField);
  if (visiblePassword && !hint.isConnected) document.documentElement.append(hint);
  else if (!visiblePassword && hint.isConnected) hint.remove();
}
let scheduled = false;
new MutationObserver(() => {
  if (!scheduled) {
    scheduled = true;
    requestAnimationFrame(() => { scheduled = false; updateHint(); });
  }
}).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['type', 'style', 'class', 'hidden', 'disabled', 'readonly', 'autocomplete'] });
updateHint();
window.addEventListener('pagehide', () => { prepared = undefined; });
chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !record(message) || typeof message.token !== 'string'
    || message.url !== location.href || location.protocol !== 'https:' && !(location.protocol === 'http:' && ['localhost', '127.0.0.1', '::1'].includes(location.hostname))
    || document.visibilityState !== 'visible') { sendResponse({ ok: false }); return; }
  if (message.action === 'prepare') {
    prepared = undefined;
    const fields = findFields(focused);
    if (fields) prepared = { token: message.token, url: location.href, fields, expiresAt: Date.now() + 10000 };
    sendResponse({ ok: !!fields });
    return;
  }
  if (message.action === 'fill') {
    const target = prepared;
    prepared = undefined;
    if (!target || target.token !== message.token || target.url !== location.href || target.expiresAt <= Date.now()
      || !isSecret(message.secret) || !sameFields(target.fields, findFields(focused))) { sendResponse({ ok: false }); return; }
    try { fillFields(target.fields, message.secret.username, message.secret.password); sendResponse({ ok: true }); }
    catch { sendResponse({ ok: false }); }
  }
});
