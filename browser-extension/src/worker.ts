import { canonicalOrigin, isDiscovery, isSecret, isTotp, record, type Credential } from './protocol';
import { nativeSession } from './native';
import { isPopup, sameTarget, type Target } from './context';

type Popup = { port: chrome.runtime.Port; native: ReturnType<typeof nativeSession> | undefined };
type Interaction = {
  token: string; target: Target; accounts: Credential[]; busy: boolean; expiresAt: number; deadline: number;
  selectedId: string | undefined; lastReadAt: number;
};
let popup: Popup | undefined;
let interaction: Interaction | undefined;
let watchedTarget: Pick<Target, 'tabId' | 'windowId'> | undefined;
let generation = 0;
let expiry: ReturnType<typeof setTimeout> | undefined;
function notify(message: unknown): void {
  const session = popup;
  if (!session) return;
  try { session.port.postMessage(message); }
  catch {
    if (popup === session) { invalidate(); popup = undefined; }
    session.port.disconnect();
  }
}
function invalidate(error?: string): void {
  generation++;
  interaction = undefined;
  watchedTarget = undefined;
  clearTimeout(expiry);
  expiry = undefined;
  popup?.native?.close(error ?? 'targetChanged');
  if (popup) popup.native = undefined;
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
chrome.windows.onFocusChanged.addListener(windowId => {
  if (watchedTarget && windowId !== watchedTarget.windowId) invalidate('targetChanged');
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
async function requireCurrent(session: Popup, active: Interaction): Promise<void> {
  if (popup !== session || interaction !== active || expired(active)) throw new Error('targetChanged');
  const target = await currentTarget();
  if (popup !== session || interaction !== active || expired(active) || !sameTarget(active.target, target))
    throw new Error('targetChanged');
}
function deliver(target: Target, message: unknown): Promise<unknown> {
  return chrome.tabs.sendMessage(target.tabId, message, { documentId: target.documentId, frameId: target.frameId });
}
function native(session: Popup): ReturnType<typeof nativeSession> {
  if (popup !== session) throw new Error('targetChanged');
  session.native ??= nativeSession(error => { if (popup === session) invalidate(error); });
  return session.native;
}
function accountFor(message: Record<string, unknown>, capability: 'hasPassword' | 'hasTotp'): Interaction {
  const active = interaction;
  if (!active || active.busy || message.token !== active.token || typeof message.id !== 'string'
    || !active.accounts.some(account => account.id === message.id && account[capability])) throw new Error('targetChanged');
  return active;
}
async function popupAction(session: Popup, message: Record<string, unknown>): Promise<unknown> {
  if (popup !== session) throw new Error('targetChanged');
  switch (message.action) {
    case 'discover': {
      invalidate();
      const epoch = generation;
      // Discovery begins the fixed authorization lifetime; ticks never renew it.
      const expiresAt = Date.now() + 60000;
      const deadline = performance.now() + 60000;
      const target = await currentTarget(true);
      if (popup !== session || epoch !== generation) throw new Error('targetChanged');
      const discovery = await native(session).request('findCredentials', { origin: target.origin });
      const current = await currentTarget();
      if (popup !== session || epoch !== generation || !isDiscovery(discovery) || expiresAt <= Date.now()
        || deadline <= performance.now() || !sameTarget(target, current)) throw new Error('targetChanged');
      interaction = { token: crypto.randomUUID(), target, accounts: discovery.credentials, busy: false, expiresAt, deadline,
        selectedId: undefined, lastReadAt: -Infinity };
      const active = interaction;
      expiry = setTimeout(() => { if (interaction === active) invalidate('targetChanged'); }, Math.max(0, deadline - performance.now()));
      return { token: active.token, origin: target.origin, accounts: active.accounts, truncated: discovery.truncated };
    }
    case 'select': {
      const active = accountFor(message, 'hasPassword');
      active.selectedId = undefined;
      active.busy = true;
      try {
        await requireCurrent(session, active);
        const prepared = await deliver(active.target, { action: 'prepare', token: active.token, url: active.target.url });
        if (!record(prepared) || prepared.ok !== true) throw new Error('ambiguousFields');
        await requireCurrent(session, active);
        const secret = await native(session).request('getCredentialSecret', { origin: active.target.origin, credentialId: String(message.id) });
        if (!isSecret(secret)) throw new Error('invalidRequest');
        await requireCurrent(session, active);
        const filled = await deliver(active.target, { action: 'fill', token: active.token, url: active.target.url, secret });
        if (!record(filled) || filled.ok !== true) throw new Error('targetChanged');
        if (interaction === active) invalidate();
        return { filled: true };
      } finally { if (interaction === active) active.busy = false; }
    }
    case 'viewTotp': case 'refreshTotp': case 'copyTotp': {
      const active = accountFor(message, 'hasTotp');
      if (message.action !== 'viewTotp' && active.selectedId !== message.id) throw new Error('targetChanged');
      const copy = message.action === 'copyTotp';
      if (!copy && performance.now() - active.lastReadAt < 1000) throw new Error('invalidRequest');
      active.selectedId = String(message.id);
      active.busy = true;
      try {
        await requireCurrent(session, active);
        if (!copy) active.lastReadAt = performance.now();
        const result = await native(session).request(copy ? 'copyCredentialTotp' : 'getCredentialTotp',
          { origin: active.target.origin, credentialId: active.selectedId });
        await requireCurrent(session, active);
        if (copy ? !record(result) || result.copied !== true : !isTotp(result)) throw new Error('invalidRequest');
        return result;
      } finally { if (interaction === active) active.busy = false; }
    }
    case 'closeTotp': {
      const active = interaction;
      if (!active || message.token !== active.token || message.id !== active.selectedId) throw new Error('targetChanged');
      // Closing is local cancellation: it must not wait for a pending native read.
      invalidate();
      return { closed: true };
    }
    case 'showApp': {
      invalidate();
      try { return await native(session).request('showApp'); }
      finally { if (popup === session) invalidate(); }
    }
    default: throw new Error('invalidRequest');
  }
}
function safeError(error: unknown): string {
  const allowed = ['locked', 'unavailable', 'desktopUnavailable', 'invalidRequest', 'targetChanged', 'ambiguousFields', 'unsupportedPage'];
  return error instanceof Error && allowed.includes(error.message) ? error.message : 'unavailable';
}

// A live official popup port owns the authorization and native connection.
chrome.runtime.onConnect.addListener(port => {
  if (port.name !== 'popup-session' || !port.sender || !isPopup(port.sender)) { port.disconnect(); return; }
  const previous = popup;
  invalidate('targetChanged');
  popup = undefined;
  previous?.port.disconnect();
  const session: Popup = { port, native: undefined };
  popup = session;
  port.onDisconnect.addListener(() => { if (popup === session) { invalidate(); popup = undefined; } });
  port.onMessage.addListener((message: unknown) => {
    if (!record(message) || typeof message.requestId !== 'string' || !record(message.message)) {
      invalidate('invalidRequest'); return;
    }
    const requestId = message.requestId;
    const action = message.message.action;
    const sensitive = ['viewTotp', 'refreshTotp', 'copyTotp'].includes(String(action));
    const operation = popupAction(session, message.message);
    const epoch = generation;
    void operation.then(result => {
      if (sensitive && epoch !== generation) return;
      if (popup === session) notify({ requestId, ok: true, result });
    }, error => {
      if (popup !== session || epoch !== generation) return;
      const code = safeError(error);
      invalidate();
      notify({ requestId, ok: false, error: code });
    });
  });
});

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  const handle = async () => {
    // Content scripts may request the popup, never metadata, passwords, or OTP values.
    if (!record(message) || sender.id !== chrome.runtime.id || sender.frameId !== 0 || sender.tab?.id === undefined
      || !sender.documentId || message.action !== 'openPopup') throw new Error('invalidRequest');
    const target = await currentTarget();
    if (target.tabId !== sender.tab.id || target.documentId !== sender.documentId || target.url !== sender.url)
      throw new Error('targetChanged');
    invalidate('targetChanged');
    try { await chrome.action.openPopup({ windowId: target.windowId }); return { opened: true }; }
    catch { return { opened: false }; }
  };
  void handle().then(result => sendResponse({ ok: true, result }), error => sendResponse({ ok: false, error: safeError(error) }));
  return true;
});
