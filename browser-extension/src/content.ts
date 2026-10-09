import { findFields, sameFields, visibleFields, fillFields, type Fields } from './fields';
import { canonicalOrigin, isSecret, record } from './protocol';

let focused = document.activeElement instanceof HTMLInputElement ? document.activeElement : undefined;
let generation = 0;
let authorizedToken: string | undefined;
let prepared: { token: string; url: string; fields: Fields; expiresAt: number; deadline: number; generation: number } | undefined;
document.addEventListener('focusin', event => {
  if (event.target instanceof HTMLInputElement) focused = event.target;
});
window.addEventListener('pagehide', () => { generation++; authorizedToken = undefined; prepared = undefined; });
chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  const handle = async (): Promise<boolean> => {
    if (sender.id !== chrome.runtime.id || !record(message) || typeof message.token !== 'string'
      || message.url !== location.href || !canonicalOrigin(location.href)) return false;
    if (message.action === 'cancel') {
      if (message.token !== authorizedToken) return false;
      generation++;
      authorizedToken = undefined;
      prepared = undefined;
      return true;
    }
    if (document.visibilityState !== 'visible') return false;
    if (message.action === 'prepare') {
      generation++;
      authorizedToken = undefined;
      prepared = undefined;
      const fields = findFields(focused);
      if (!fields) return false;
      const target = { token: message.token, url: location.href, fields, expiresAt: Date.now() + 70000,
        deadline: performance.now() + 70000, generation };
      authorizedToken = target.token;
      prepared = target;
      if (!await visibleFields(fields) || prepared !== target || target.url !== location.href || document.visibilityState !== 'visible'
        || !sameFields(fields, findFields(focused))) { if (prepared === target) prepared = undefined; return false; }
      return true;
    }
    if (message.action === 'fill') {
      const target = prepared;
      prepared = undefined;
      if (!target || target.token !== message.token || target.url !== location.href || target.expiresAt <= Date.now() || target.deadline <= performance.now()
        || !isSecret(message.secret) || !sameFields(target.fields, findFields(focused))) return false;
      try {
        await fillFields(target.fields, message.secret.username, message.secret.password,
          () => authorizedToken === target.token && generation === target.generation
            && target.expiresAt > Date.now() && target.deadline > performance.now() && target.url === location.href);
        return true;
      } finally { if (generation === target.generation) authorizedToken = undefined; }
    }
    return false;
  };
  void handle().then(ok => sendResponse({ ok }), () => sendResponse({ ok: false }));
  return true;
});
