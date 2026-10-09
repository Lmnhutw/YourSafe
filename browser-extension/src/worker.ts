import { canonicalOrigin, isDiscovery, isSecret, isTotp, record, type Credential, type Totp } from './protocol';
import { nativeSession } from './native';
import { isPopup, sameTarget, type Target } from './context';

type Popup = { port: chrome.runtime.Port };
type Interaction = {
  token: string; target: Target; accounts: Credential[]; truncated: boolean; busy: boolean; expiresAt: number; deadline: number;
  selectedId: string | undefined; action: 'select' | 'viewTotp' | 'copyTotp' | undefined;
  result: { filled: true } | { copied: true } | Totp | undefined;
};
let popup: Popup | undefined;
let interaction: Interaction | undefined;
let connection: ReturnType<typeof nativeSession> | undefined;
let watchedTarget: Pick<Target, 'tabId' | 'windowId'> | undefined;
let generation = 0;
let inFlight: Promise<unknown> | undefined;
let expiry: ReturnType<typeof setTimeout> | undefined;
let codeExpiry: ReturnType<typeof setTimeout> | undefined;
function notify(message: unknown): void {
  const session = popup;
  if (!session) return;
  try { session.port.postMessage(message); }
  catch { if (popup === session) popup = undefined; session.port.disconnect(); }
}
function invalidate(error?: string): void {
  const active = interaction;
  if (active) void deliver(active.target, { action: 'cancel', token: active.token, url: active.target.url }).catch(() => {});
  generation++;
  inFlight = undefined;
  interaction = undefined;
  watchedTarget = undefined;
  clearTimeout(expiry);
  clearTimeout(codeExpiry);
  expiry = undefined;
  codeExpiry = undefined;
  connection?.close(error ?? 'targetChanged');
  connection = undefined;
  if (error) notify({ event: 'invalidated', error });
}
function invalidateTab(tabId: number): void { if (tabId === watchedTarget?.tabId) invalidate('targetChanged'); }

chrome.webNavigation.onBeforeNavigate.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onCommitted.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onHistoryStateUpdated.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onReferenceFragmentUpdated.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.tabs.onActivated.addListener(event => {
  if (watchedTarget && event.windowId === watchedTarget.windowId && event.tabId !== watchedTarget.tabId) invalidate('targetChanged');
});
chrome.tabs.onRemoved.addListener(invalidateTab);
chrome.tabs.onDetached.addListener(invalidateTab);
chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.url || change.status === 'loading') invalidateTab(tabId); });
// Desktop approval reports WINDOW_ID_NONE; switching to another browser window cancels.
chrome.windows.onFocusChanged.addListener(windowId => {
  if (watchedTarget && windowId !== chrome.windows.WINDOW_ID_NONE && windowId !== watchedTarget.windowId) invalidate('targetChanged');
});
chrome.windows.onRemoved.addListener(windowId => {
  if (watchedTarget?.windowId === windowId) invalidate('targetChanged');
});

async function currentTarget(watch = false): Promise<Target> {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (tab?.id === undefined || !tab.url) throw new Error('targetChanged');
  if (!canonicalOrigin(tab.url)) throw new Error('unsupportedPage');
  if (watch) watchedTarget = { tabId: tab.id, windowId: tab.windowId };
  const frame = await chrome.webNavigation.getFrame({ tabId: tab.id, frameId: 0 });
  const origin = canonicalOrigin(tab.url);
  if (!frame?.documentId || frame.url !== tab.url || frame.documentLifecycle !== 'active' || !origin)
    throw new Error('targetChanged');
  return { tabId: tab.id, windowId: tab.windowId, frameId: 0, documentId: frame.documentId, url: frame.url, origin };
}
function expired(active: Interaction): boolean { return active.expiresAt <= Date.now() || active.deadline <= performance.now(); }
async function requireCurrent(active: Interaction): Promise<void> {
  if (interaction !== active || expired(active)) throw new Error('targetChanged');
  const target = await currentTarget();
  if (interaction !== active || expired(active) || !sameTarget(active.target, target)) throw new Error('targetChanged');
}
function deliver(target: Target, message: unknown): Promise<unknown> {
  return chrome.tabs.sendMessage(target.tabId, message, { documentId: target.documentId, frameId: target.frameId });
}
function native(): ReturnType<typeof nativeSession> {
  connection ??= nativeSession(error => invalidate(error));
  return connection;
}
function lifetime(active: Interaction, milliseconds: number): void {
  active.expiresAt = Date.now() + milliseconds;
  active.deadline = performance.now() + milliseconds;
  clearTimeout(expiry);
  expiry = setTimeout(() => { if (interaction === active) invalidate('targetChanged'); }, milliseconds);
}
function snapshot(active: Interaction): unknown {
  if (isTotp(active.result) && (active.result.expiresAtUnixMs <= Date.now()
    || Date.now() < active.result.expiresAtUnixMs - active.result.periodSeconds * 1000)) active.result = undefined;
  return { token: active.token, origin: active.target.origin, accounts: active.accounts, truncated: active.truncated,
    busy: active.busy, selectedId: active.selectedId, action: active.action, result: active.result };
}
function accountFor(message: Record<string, unknown>, capability: 'hasPassword' | 'hasTotp'): Interaction {
  const active = interaction;
  if (!active || active.busy || expired(active) || message.token !== active.token || typeof message.id !== 'string'
    || !active.accounts.some(account => account.id === message.id && account[capability])) throw new Error('targetChanged');
  return active;
}
async function popupAction(session: Popup, message: Record<string, unknown>): Promise<unknown> {
  if (popup !== session) throw new Error('targetChanged');
  switch (message.action) {
    case 'resume': {
      const active = interaction;
      if (!active) return { resumed: false };
      await requireCurrent(active);
      return { resumed: true, interaction: snapshot(active) };
    }
    case 'discover': {
      invalidate();
      const epoch = generation;
      const expiresAt = Date.now() + 60000, deadline = performance.now() + 60000;
      const target = await currentTarget(true);
      if (popup !== session || epoch !== generation) throw new Error('targetChanged');
      const discovery = await native().request('findCredentials', { origin: target.origin });
      const current = await currentTarget();
      if (popup !== session || epoch !== generation || !isDiscovery(discovery) || expiresAt <= Date.now()
        || deadline <= performance.now() || !sameTarget(target, current)) throw new Error('targetChanged');
      interaction = { token: crypto.randomUUID(), target, accounts: discovery.credentials, truncated: discovery.truncated,
        busy: false, expiresAt, deadline, selectedId: undefined, action: undefined, result: undefined };
      const active = interaction;
      expiry = setTimeout(() => { if (interaction === active) invalidate('targetChanged'); }, Math.max(0, deadline - performance.now()));
      return snapshot(active);
    }
    case 'select': case 'viewTotp': case 'copyTotp': {
      const action = message.action;
      const active = accountFor(message, action === 'select' ? 'hasPassword' : 'hasTotp');
      active.busy = true;
      try {
        await requireCurrent(active);
        // Only this explicit action starts an approval lifetime; reconnects and countdowns never renew it.
        active.selectedId = String(message.id);
        active.action = action;
        active.result = undefined;
        clearTimeout(codeExpiry);
        lifetime(active, 70000);
        if (action === 'select') {
          const prepare = { action: 'prepare', token: active.token, url: active.target.url };
          let prepared: unknown;
          try { prepared = await deliver(active.target, prepare); }
          catch {
            await requireCurrent(active);
            await chrome.scripting.executeScript({ target: { tabId: active.target.tabId, documentIds: [active.target.documentId] }, files: ['content.js'] });
            await requireCurrent(active);
            prepared = await deliver(active.target, prepare);
          }
          if (!record(prepared) || prepared.ok !== true) throw new Error('ambiguousFields');
          await requireCurrent(active);
          const secret = await native().request('getCredentialSecret', { origin: active.target.origin, credentialId: active.selectedId });
          if (!isSecret(secret)) throw new Error('invalidRequest');
          await requireCurrent(active);
          const filled = await deliver(active.target, { action: 'fill', token: active.token, url: active.target.url, secret });
          if (!record(filled) || filled.ok !== true) throw new Error('targetChanged');
          active.accounts = [];
          active.result = { filled: true };
        } else {
          const result = await native().request(action === 'copyTotp' ? 'copyCredentialTotp' : 'getCredentialTotp',
            { origin: active.target.origin, credentialId: active.selectedId });
          await requireCurrent(active);
          if (action === 'copyTotp') {
            if (!record(result) || result.copied !== true) throw new Error('invalidRequest');
            active.result = { copied: true };
          } else {
            if (!isTotp(result)) throw new Error('invalidRequest');
            active.result = result;
            codeExpiry = setTimeout(() => { if (interaction === active && active.result === result) active.result = undefined; },
              Math.max(0, Math.min(70000, result.expiresAtUnixMs - Date.now())));
          }
        }
        // Passwords are never retained. Only the bounded one-shot OTP result can survive popup closure.
        active.busy = false;
        if (popup !== session) notify({ event: 'updated', interaction: snapshot(active) });
        return active.result;
      } finally { if (interaction === active) active.busy = false; }
    }
    case 'cancel': case 'closeTotp': {
      const active = interaction;
      if (!active || message.token !== active.token) throw new Error('targetChanged');
      invalidate();
      return { closed: true };
    }
    case 'showApp': {
      invalidate();
      const appConnection = native();
      try { return await appConnection.request('showApp'); }
      finally { appConnection.close(); if (connection === appConnection) connection = undefined; }
    }
    default: throw new Error('invalidRequest');
  }
}
function safeError(error: unknown): string {
  const allowed = ['locked', 'unavailable', 'desktopUnavailable', 'invalidRequest', 'targetChanged', 'ambiguousFields', 'unsupportedPage'];
  return error instanceof Error && allowed.includes(error.message) ? error.message : 'unavailable';
}

// The official popup chooses an action; the worker owns its pending desktop approval.
chrome.runtime.onConnect.addListener(port => {
  if (port.name !== 'popup-session' || !port.sender || !isPopup(port.sender)) { port.disconnect(); return; }
  const previous = popup;
  const session: Popup = { port };
  popup = session;
  previous?.port.disconnect();
  port.onDisconnect.addListener(() => {
    if (popup !== session) return;
    popup = undefined;
    if (!interaction?.busy && !interaction?.result) invalidate();
  });
  port.onMessage.addListener((message: unknown) => {
    if (popup !== session) return;
    if (!record(message) || Object.keys(message).some(key => !['requestId', 'message'].includes(key))
      || typeof message.requestId !== 'string' || !message.requestId.length || message.requestId.length > 128
      || !record(message.message) || Object.keys(message.message).some(key => !['action', 'token', 'id'].includes(key))) {
      invalidate('invalidRequest'); return;
    }
    const requestId = message.requestId;
    const control = ['resume', 'cancel', 'closeTotp'].includes(String(message.message.action));
    if (inFlight && !control) { notify({ requestId, ok: false, error: 'invalidRequest' }); return; }
    const operation = popupAction(session, message.message);
    if (!control) inFlight = operation;
    const epoch = generation;
    void operation.then(result => {
      if (inFlight === operation) inFlight = undefined;
      if (epoch === generation && popup === session) notify({ requestId, ok: true, result });
    }, error => {
      if (inFlight === operation) inFlight = undefined;
      if (epoch !== generation) return;
      const code = safeError(error);
      if (popup === session) { invalidate(); notify({ requestId, ok: false, error: code }); }
      else invalidate(code);
    });
  });
});
