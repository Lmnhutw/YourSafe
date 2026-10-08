import { isCredential, isTotp, record, type Credential, type Totp } from './protocol';
function element<T extends HTMLElement>(id: string, kind: { new(): T }): T {
  const found = document.getElementById(id);
  if (!(found instanceof kind)) throw new Error('Invalid popup layout');
  return found;
}
const status = element('status', HTMLParagraphElement);
const origin = element('origin', HTMLParagraphElement);
const accounts = element('accounts', HTMLDivElement);
const showApp = element('show-app', HTMLButtonElement);
const refresh = element('refresh', HTMLButtonElement);
const panel = element('totp-panel', HTMLDivElement);
const panelTitle = element('totp-title', HTMLElement);
const panelUsername = element('totp-username', HTMLParagraphElement);
const code = element('totp-code', HTMLElement);
const countdown = element('totp-countdown', HTMLElement);
const progress = element('totp-progress', HTMLProgressElement);
const copy = element('totp-copy', HTMLButtonElement);
const close = element('totp-close', HTMLButtonElement);
const errors: Record<string, string> = {
  locked: 'Your vault table is locked. Click Show YourSafe, choose Unlock Vault on the desktop, then refresh here.',
  desktopUnavailable: 'YourSafe is not connected. Start the matching desktop app, then refresh. If it is running, check browser host registration.',
  ambiguousFields: 'Focus the username or password field you want to fill, then reopen YourSafe.',
  targetChanged: 'The page changed or access expired. Refresh and choose an account again.',
  unavailable: 'The credential is unavailable. Refresh and try again.',
  unsupportedPage: 'Open an HTTPS sign-in page, then reopen YourSafe.'
};
const port = chrome.runtime.connect({ name: 'popup-session' });
const pending = new Map<string, { resolve(value: unknown): void; reject(error: Error): void; timeout: ReturnType<typeof setTimeout> }>();
let connected = true;
let busy = false;
let epoch = 0;
let selected: { account: Credential; token: string } | undefined;
let displayed: Totp | undefined;
let timer: ReturnType<typeof setInterval> | undefined;
let lastReadAt = -Infinity;
function setBusy(value: boolean): void {
  busy = value;
  refresh.disabled = showApp.disabled = value;
  copy.disabled = value || !displayed || panel.hidden;
  close.disabled = false;
  accounts.querySelectorAll('button').forEach(button => { button.disabled = value; });
  accounts.setAttribute('aria-busy', String(value));
}
function clearPanel(): void {
  epoch++;
  clearInterval(timer);
  timer = undefined;
  displayed = undefined;
  selected = undefined;
  accounts.hidden = false;
  panel.hidden = true;
  panelTitle.textContent = panelUsername.textContent = code.textContent = countdown.textContent = '';
  code.removeAttribute('aria-label');
  progress.value = 0;
  copy.disabled = true;
}
function rejectPending(error: Error): void {
  for (const operation of pending.values()) { clearTimeout(operation.timeout); operation.reject(error); }
  pending.clear();
}
function errorFor(value: unknown): Error {
  return new Error(typeof value === 'string' ? errors[value] ?? 'YourSafe could not complete this request. Refresh to try again.'
    : 'YourSafe is unavailable. Refresh to try again.');
}
function failure(error: unknown): void {
  clearPanel();
  accounts.replaceChildren();
  if (!origin.textContent) origin.textContent = 'Website unavailable';
  status.dataset.state = 'error';
  status.textContent = error instanceof Error ? error.message : 'YourSafe is unavailable.';
  setBusy(false);
}
port.onMessage.addListener((message: unknown) => {
  if (!record(message)) { rejectPending(errorFor('unavailable')); failure(errorFor('unavailable')); return; }
  if (message.event === 'invalidated') {
    rejectPending(errorFor(message.error));
    failure(errorFor(message.error));
    return;
  }
  if (typeof message.requestId !== 'string') return;
  const operation = pending.get(message.requestId);
  if (!operation) return; // Late responses from cleared sessions never restore a code.
  pending.delete(message.requestId);
  clearTimeout(operation.timeout);
  if (message.ok === true) operation.resolve(message.result);
  else operation.reject(errorFor(message.error));
});
port.onDisconnect.addListener(() => {
  void chrome.runtime.lastError;
  connected = false;
  rejectPending(errorFor('desktopUnavailable'));
  failure(errorFor('desktopUnavailable'));
});
function request(message: unknown): Promise<unknown> {
  if (!connected) return Promise.reject(errorFor('desktopUnavailable'));
  const requestId = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      pending.delete(requestId);
      reject(errorFor('desktopUnavailable'));
      connected = false;
      port.disconnect();
    }, 12000);
    pending.set(requestId, { resolve, reject, timeout });
    try { port.postMessage({ requestId, message }); }
    catch { pending.delete(requestId); clearTimeout(timeout); reject(errorFor('desktopUnavailable')); }
  });
}
function paintTotp(): void {
  const value = displayed;
  const now = Date.now();
  if (!value || now >= value.expiresAtUnixMs || now < value.expiresAtUnixMs - value.periodSeconds * 1000) {
    displayed = undefined;
    code.textContent = '—';
    code.removeAttribute('aria-label');
    countdown.textContent = 'Updating…';
    progress.value = 0;
    copy.disabled = true;
    return;
  }
  const split = value.code.length / 2;
  code.textContent = value.code.slice(0, split) + ' ' + value.code.slice(split);
  code.setAttribute('aria-label', 'Verification code ' + value.code.split('').join(' '));
  const remaining = value.expiresAtUnixMs - now;
  countdown.textContent = Math.ceil(remaining / 1000) + 's';
  progress.max = value.periodSeconds * 1000;
  progress.value = remaining;
  copy.disabled = busy;
}
async function readTotp(action: 'viewTotp' | 'refreshTotp', currentEpoch: number): Promise<void> {
  const active = selected;
  if (!active || currentEpoch !== epoch || busy) return;
  setBusy(true);
  lastReadAt = performance.now();
  try {
    const result = await request({ action, token: active.token, id: active.account.id });
    if (currentEpoch !== epoch || selected !== active) return;
    if (!isTotp(result)) throw errorFor('unavailable');
    displayed = result;
    paintTotp();
    if (action === 'viewTotp') {
      status.dataset.state = 'ready';
      status.textContent = 'Code access ends after one minute. Copy uses the current desktop code.';
    }
  } catch (error) { if (currentEpoch === epoch) failure(error); }
  finally {
    if (currentEpoch === epoch) {
      lastReadAt = performance.now(); setBusy(false); paintTotp();
      if (action === 'viewTotp' && !panel.hidden && !copy.disabled) copy.focus();
    }
  }
}
function viewTotp(account: Credential, token: string): void {
  if (busy) return;
  clearPanel();
  selected = { account, token };
  panelTitle.textContent = account.title;
  panelUsername.textContent = account.username || 'No username';
  accounts.hidden = true;
  panel.hidden = false;
  close.focus();
  const currentEpoch = epoch;
  const begin = () => {
    if (currentEpoch !== epoch) return;
    void readTotp('viewTotp', currentEpoch);
    timer = setInterval(() => {
      if (currentEpoch !== epoch || panel.hidden) return;
      paintTotp();
      if (!busy && performance.now() - lastReadAt >= 1000) void readTotp('refreshTotp', currentEpoch);
    }, 100);
  };
  // Switching accounts still respects the one-read-per-second bound.
  const delay = Math.max(0, 1000 - (performance.now() - lastReadAt));
  if (delay) {
    setBusy(true);
    timer = setInterval(() => {
      if (currentEpoch !== epoch || performance.now() - lastReadAt < 1000) return;
      clearInterval(timer);
      timer = undefined;
      setBusy(false);
      begin();
    }, 100);
    paintTotp();
  } else begin();
}
function accountRow(account: Credential, token: string): HTMLElement {
  const row = document.createElement('div');
  row.className = 'account';
  const label = document.createElement('div');
  label.className = 'account-label';
  const title = document.createElement('strong');
  title.textContent = account.title;
  const username = document.createElement('span');
  username.textContent = account.username || 'No username';
  label.append(title, username);
  const actions = document.createElement('div');
  actions.className = 'account-actions';
  if (account.hasPassword) {
    const fill = document.createElement('button');
    fill.type = 'button';
    fill.textContent = 'Fill';
    fill.setAttribute('aria-label', 'Fill ' + account.title + ', ' + (account.username || 'password only'));
    fill.addEventListener('click', () => {
      if (busy) return;
      clearPanel();
      setBusy(true);
      status.textContent = 'Filling…';
      void request({ action: 'select', token, id: account.id }).then(() => {
        accounts.replaceChildren();
        status.dataset.state = 'success';
        status.textContent = 'Filled. Review the form before signing in. YourSafe does not submit it.';
      }).catch(failure).finally(() => { setBusy(false); });
    });
    actions.append(fill);
  }
  if (account.hasTotp) {
    const view = document.createElement('button');
    view.type = 'button';
    view.textContent = 'View TOTP';
    view.dataset.credentialId = account.id;
    view.setAttribute('aria-label', 'View TOTP for ' + account.title);
    view.addEventListener('click', () => { viewTotp(account, token); });
    actions.append(view);
  }
  row.append(label, actions);
  return row;
}
async function discover(focusAccountId?: string): Promise<void> {
  if (busy) return;
  clearPanel();
  const currentEpoch = epoch;
  setBusy(true);
  status.dataset.state = 'loading';
  accounts.replaceChildren();
  origin.textContent = '';
  status.textContent = 'Looking for accounts…';
  try {
    const result = await request({ action: 'discover' });
    if (currentEpoch !== epoch) return;
    if (!record(result) || typeof result.token !== 'string' || typeof result.origin !== 'string'
      || !Array.isArray(result.accounts) || !result.accounts.every(isCredential)
      || typeof result.truncated !== 'boolean') throw errorFor('unavailable');
    origin.textContent = result.origin;
    status.textContent = result.truncated ? 'Some accounts could not be listed. Open YourSafe on the desktop to see all accounts.'
      : result.accounts.length ? 'Choose Fill or View TOTP for an account.' : 'No accounts available. Add one in YourSafe with this website address or choose No URL, then refresh.';
    status.dataset.state = result.accounts.length ? 'ready' : 'empty';
    for (const account of result.accounts) accounts.append(accountRow(account, result.token));
  } catch (error) { if (currentEpoch === epoch) failure(error); }
  finally {
    if (currentEpoch === epoch) {
      setBusy(false);
      if (focusAccountId) accounts.querySelectorAll('button').forEach(button => {
        if (button.dataset.credentialId === focusAccountId) button.focus();
      });
    }
  }
}
copy.addEventListener('click', () => {
  const active = selected;
  if (busy || !active) return;
  const currentEpoch = epoch;
  setBusy(true);
  void request({ action: 'copyTotp', token: active.token, id: active.account.id }).then(result => {
    if (currentEpoch !== epoch) return;
    if (!record(result) || result.copied !== true) throw errorFor('unavailable');
    status.dataset.state = 'success';
    status.textContent = 'Copied the current code.';
  }).catch(error => { if (currentEpoch === epoch) failure(error); }).finally(() => {
    if (currentEpoch === epoch) { setBusy(false); paintTotp(); }
  });
});
close.addEventListener('click', () => {
  const active = selected;
  if (!active) return;
  clearPanel();
  rejectPending(errorFor('targetChanged'));
  setBusy(false);
  status.dataset.state = 'ready';
  status.textContent = 'Choose Fill or View TOTP for an account.';
  setBusy(true);
  void request({ action: 'closeTotp', token: active.token, id: active.account.id }).then(async () => {
    setBusy(false);
    await discover(active.account.id);
  }).catch(failure);
});
refresh.addEventListener('click', () => { void discover(); });
showApp.addEventListener('click', () => {
  if (busy) return;
  clearPanel();
  const currentEpoch = epoch;
  setBusy(true);
  accounts.replaceChildren();
  void request({ action: 'showApp' }).then(result => {
    if (currentEpoch !== epoch) return;
    status.dataset.state = record(result) && result.shown === true ? 'ready' : 'error';
    status.textContent = record(result) && result.shown === true
      ? 'Unlock on the desktop, then refresh and choose an account again.' : 'YourSafe could not open its window.';
  }).catch(failure).finally(() => { if (currentEpoch === epoch) setBusy(false); });
});
window.addEventListener('pagehide', () => {
  clearPanel();
  accounts.replaceChildren();
  rejectPending(errorFor('targetChanged'));
  connected = false;
  port.disconnect();
});
void discover();
