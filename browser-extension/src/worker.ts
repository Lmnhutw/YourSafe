import { canonicalOrigin, isDiscovery, isSecret, record, type Credential } from './protocol';
import { nativeRequest } from './native';
import { isPopup, sameTarget, type Target } from './context';

type Interaction = { token: string; target: Target; accounts: Credential[]; busy: boolean; expiresAt: number };
let interaction: Interaction | undefined;
let watchedTarget: Pick<Target, 'tabId' | 'windowId'> | undefined;
let generation = 0;
function invalidate(): void { generation++; interaction = undefined; watchedTarget = undefined; }
function invalidateTab(tabId: number): void { if (tabId === watchedTarget?.tabId) invalidate(); }

chrome.webNavigation.onBeforeNavigate.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onCommitted.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onHistoryStateUpdated.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.webNavigation.onReferenceFragmentUpdated.addListener(event => { if (event.frameId === 0) invalidateTab(event.tabId); });
chrome.tabs.onActivated.addListener(event => {
  if (!watchedTarget || (event.windowId === watchedTarget.windowId && event.tabId !== watchedTarget.tabId)) invalidate();
});
chrome.tabs.onRemoved.addListener(invalidateTab);
chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.url || change.status === 'loading') invalidateTab(tabId); });
chrome.windows.onFocusChanged.addListener(windowId => {
  if (!watchedTarget || windowId !== watchedTarget.windowId) invalidate();
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
async function requireCurrent(active: Interaction): Promise<void> {
  if (interaction !== active || active.expiresAt <= Date.now()) throw new Error('targetChanged');
  const target = await currentTarget();
  if (interaction !== active || active.expiresAt <= Date.now() || !sameTarget(active.target, target))
    throw new Error('targetChanged');
}
function deliver(target: Target, message: unknown): Promise<unknown> {
  return chrome.tabs.sendMessage(target.tabId, message, { documentId: target.documentId, frameId: target.frameId });
}
async function popupAction(message: Record<string, unknown>): Promise<unknown> {
  switch (message.action) {
    case 'discover': {
      invalidate();
      const epoch = generation;
      const target = await currentTarget(true);
      if (epoch !== generation) throw new Error('targetChanged');
      const discovery = await nativeRequest('findCredentials', { origin: target.origin });
      const current = await currentTarget();
      if (epoch !== generation || !isDiscovery(discovery)
        || !sameTarget(target, current)) throw new Error('targetChanged');
      const accounts = discovery.credentials;
      interaction = { token: crypto.randomUUID(), target, accounts, busy: false, expiresAt: Date.now() + 60000 };
      return { token: interaction.token, origin: target.origin, accounts, truncated: discovery.truncated };
    }
    case 'select': {
      const active = interaction;
      if (!active || active.busy || message.token !== active.token || typeof message.id !== 'string'
        || !active.accounts.some(account => account.id === message.id)) throw new Error('targetChanged');
      active.busy = true;
      try {
        await requireCurrent(active);
        const prepared = await deliver(active.target, { action: 'prepare', token: active.token, url: active.target.url });
        if (!record(prepared) || prepared.ok !== true) throw new Error('ambiguousFields');
        // Final consent comes only from a validated message from our own trusted popup.
        await requireCurrent(active);
        const secret = await nativeRequest('getCredentialSecret', { origin: active.target.origin, credentialId: message.id });
        if (!isSecret(secret)) throw new Error('invalidRequest');
        await requireCurrent(active);
        const filled = await deliver(active.target, { action: 'fill', token: active.token, url: active.target.url, secret });
        if (!record(filled) || filled.ok !== true) throw new Error('targetChanged');
        return { filled: true };
      } finally { invalidate(); }
    }
    case 'showApp': invalidate(); return nativeRequest('showApp');
    default: throw new Error('invalidRequest');
  }
}

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  const handle = async () => {
    if (!record(message)) throw new Error('invalidRequest');
    if (isPopup(sender)) return popupAction(message);
    // Content scripts may request the popup, but never metadata or secret retrieval.
    if (sender.id !== chrome.runtime.id || sender.frameId !== 0 || sender.tab?.id === undefined
      || !sender.documentId || message.action !== 'openPopup') throw new Error('invalidRequest');
    const target = await currentTarget();
    if (target.tabId !== sender.tab.id || target.documentId !== sender.documentId || target.url !== sender.url)
      throw new Error('targetChanged');
    invalidate();
    try { await chrome.action.openPopup({ windowId: target.windowId }); return { opened: true }; }
    catch { return { opened: false }; }
  };
  void handle().then(result => sendResponse({ ok: true, result }), error => {
    invalidate();
    const allowed = ['locked', 'unavailable', 'desktopUnavailable', 'invalidRequest', 'targetChanged', 'ambiguousFields', 'unsupportedPage'];
    const code = error instanceof Error && allowed.includes(error.message) ? error.message : 'unavailable';
    sendResponse({ ok: false, error: code });
  });
  return true;
});
