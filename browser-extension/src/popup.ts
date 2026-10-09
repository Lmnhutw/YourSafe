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
const cancel = element('cancel', HTMLButtonElement);
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
  unavailable: 'Enable browser integration in YourSafe desktop Settings and approve the request there. Then refresh and try again.',
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
let authorizationToken: string | undefined;
function setBusy(value: boolean): void {
  busy = value;
  refresh.disabled = showApp.disabled = value;
  copy.disabled = value || !selected || panel.hidden;
  cancel.hidden = !value || !authorizationToken;
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
  authorizationToken = undefined;
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
  if (message.event === 'updated') {
    try { renderSnapshot(message.interaction); } catch (error) { failure(error); }
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
    }, 72000);
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
    countdown.textContent = 'Expired. Choose account to request a new code.';
    progress.value = 0;
    copy.disabled = busy || !selected;
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
async function readTotp(currentEpoch: number): Promise<void> {
  const active = selected;
  if (!active || currentEpoch !== epoch || busy) return;
  setBusy(true);
  status.textContent = 'Approve this code request in YourSafe on the desktop. Reopen this popup afterwards if it closes.';
  try {
    const result = await request({ action: 'viewTotp', token: active.token, id: active.account.id });
    if (currentEpoch !== epoch || selected !== active) return;
    if (!isTotp(result)) throw errorFor('unavailable');
    displayed = result;
    paintTotp();
    status.dataset.state = 'ready';
    status.textContent = 'This approved code expires at the countdown. Copy requires a new desktop approval.';
  } catch (error) { if (currentEpoch === epoch) failure(error); }
  finally {
    if (currentEpoch === epoch) {
      setBusy(false); paintTotp();
      if (!panel.hidden && !copy.disabled) copy.focus();
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
  void readTotp(currentEpoch);
  timer = setInterval(() => { if (currentEpoch === epoch && !panel.hidden) paintTotp(); }, 100);
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
      const currentEpoch = epoch;
      setBusy(true);
      status.textContent = 'Approve this fill in YourSafe on the desktop. Reopen this popup afterwards if it closes.';
      void request({ action: 'select', token, id: account.id }).then(() => {
        if (currentEpoch !== epoch) return;
        accounts.replaceChildren();
        status.dataset.state = 'success';
        status.textContent = 'Filled. Review the form before signing in. YourSafe does not submit it.';
      }).catch(error => { if (currentEpoch === epoch) failure(error); }).finally(() => { if (currentEpoch === epoch) setBusy(false); });
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
    authorizationToken = result.token;
    origin.textContent = result.origin;
    status.textContent = result.truncated ? 'Some accounts could not be listed. Open YourSafe on the desktop to see all accounts.'
      : result.accounts.length ? 'Choose Fill or View TOTP for an account.' : 'No accounts available. Add a supported website address to the account in YourSafe, then refresh.';
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
function renderSnapshot(value: unknown): void {
  if (!record(value) || typeof value.token !== 'string' || typeof value.origin !== 'string'
    || !Array.isArray(value.accounts) || !value.accounts.every(isCredential) || typeof value.truncated !== 'boolean'
    || typeof value.busy !== 'boolean' || (value.selectedId !== undefined && typeof value.selectedId !== 'string')
    || ![undefined, 'select', 'viewTotp', 'copyTotp'].some(action => value.action === action)
    || (value.result !== undefined && !isTotp(value.result)
      && (!record(value.result) || (value.result.filled !== true && value.result.copied !== true)))) throw errorFor('unavailable');
  clearPanel();
  authorizationToken = value.token;
  origin.textContent = value.origin;
  accounts.replaceChildren();
  for (const account of value.accounts) accounts.append(accountRow(account, value.token));
  const account = value.accounts.find(item => item.id === value.selectedId);
  if (account && ['viewTotp', 'copyTotp'].includes(String(value.action))) {
    selected = { account, token: value.token };
    panelTitle.textContent = account.title;
    panelUsername.textContent = account.username || 'No username';
    accounts.hidden = true;
    panel.hidden = false;
    displayed = isTotp(value.result) ? value.result : undefined;
    timer = setInterval(paintTotp, 100);
    paintTotp();
  }
  status.dataset.state = value.busy ? 'loading' : 'ready';
  status.textContent = value.busy ? 'Waiting for desktop approval. Cancel here or in YourSafe to stop this request.'
    : record(value.result) && 'filled' in value.result && value.result.filled === true ? 'Filled. Review the form before signing in. YourSafe does not submit it.'
    : record(value.result) && 'copied' in value.result && value.result.copied === true ? 'Copied the current code.'
    : isTotp(value.result) ? 'This approved code expires at the countdown. Copy requires a new desktop approval.'
    : 'Choose Fill or View TOTP for an account.';
  setBusy(value.busy);
}
copy.addEventListener('click', () => {
  const active = selected;
  if (busy || !active) return;
  const currentEpoch = epoch;
  setBusy(true);
  status.textContent = 'Approve copying the current code in YourSafe on the desktop.';
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
cancel.addEventListener('click', () => {
  const token = authorizationToken;
  if (!token) return;
  clearPanel();
  rejectPending(errorFor('targetChanged'));
  void request({ action: 'cancel', token }).then(async () => { setBusy(false); await discover(); }).catch(failure);
});
showApp.addEventListener('click', () => {
  if (busy) return;
  clearPanel();
  authorizationToken = undefined;
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
async function open(): Promise<void> {
  setBusy(true);
  status.textContent = 'Checking the current request…';
  try {
    const result = await request({ action: 'resume' });
    if (!record(result) || typeof result.resumed !== 'boolean') throw errorFor('unavailable');
    if (result.resumed) renderSnapshot(result.interaction);
    else { setBusy(false); await discover(); }
  } catch (error) { failure(error); }
}
void open();
