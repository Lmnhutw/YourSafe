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
const integrationOff = element('integration-off', HTMLElement);
const integrationGuide = element('integration-off-guide', HTMLParagraphElement);
const integrationOffOpen = element('integration-off-open', HTMLButtonElement);
const openAppFeedback = element('open-app-feedback', HTMLParagraphElement);
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
  locked: 'Your vault is locked. Open YourSafe, unlock it, then reopen this popup. If it stays open, use Refresh.',
  desktopUnavailable: 'Could not connect to YourSafe. Make sure the Windows app is running. If it is running, update both YourSafe and this extension, then try again.',
  integrationDisabled: 'Browser integration is off. Enable it in YourSafe Settings, then click Refresh.',
  integrationLocked: 'Unlock your vault before enabling browser integration.',
  protocolMismatch: 'YourSafe and this extension use different protocol versions. Update both the Windows app and extension, then try again.',
  ambiguousFields: 'YourSafe could not identify a field to fill. Click the username or password field, then reopen this popup and choose the account again.',
  targetChanged: 'The page changed or this request expired. Return to the sign-in page, then reopen this popup or refresh and choose an account again.',
  tabChanged: 'Request canceled because you switched browser tabs or windows. Return to the sign-in page and refresh to choose an account again.',
  approvalDeclined: 'Request canceled in YourSafe on Windows. Refresh to choose an account again.',
  unavailable: "This request wasn't completed. Refresh to try again.",
  unsupportedPage: 'Open an HTTPS sign-in page, then reopen YourSafe.'
};
const port = chrome.runtime.connect({ name: 'popup-session' });
const pending = new Map<string, { resolve(value: unknown): void; reject(error: Error): void; timeout: ReturnType<typeof setTimeout> }>();
let connected = true;
let busy = false;
let openingApp = false;
let epoch = 0;
let selected: { account: Credential; token: string } | undefined;
let displayed: Totp | undefined;
let codeExpired = false;
let timer: ReturnType<typeof setInterval> | undefined;
let authorizationToken: string | undefined;
function setBusy(value: boolean): void {
  busy = value;
  refresh.disabled = value;
  showApp.disabled = integrationOffOpen.disabled = openingApp;
  copy.disabled = value || !selected || panel.hidden;
  cancel.hidden = !value || !authorizationToken;
  close.disabled = false;
  accounts.querySelectorAll('button').forEach(button => { button.disabled = value; });
  accounts.setAttribute('aria-busy', String(value));
  if (value && authorizationToken) status.dataset.state = 'pending';
}
function approvalStatus(action: 'select' | 'viewTotp' | 'copyTotp' | undefined, account?: Credential): string {
  if (!account || !action) return 'Approve this request in YourSafe on Windows.';
  const label = account.username ? `${account.title} (${account.username})` : account.title;
  const task = action === 'select' ? 'Fill password' : action === 'viewTotp' ? 'View verification code' : 'Copy verification code';
  return `${task} for ${label}. Approve in YourSafe on Windows.`;
}
function clearPanel(): void {
  epoch++;
  clearInterval(timer);
  timer = undefined;
  displayed = undefined;
  codeExpired = false;
  selected = undefined;
  accounts.hidden = false;
  panel.hidden = true;
  panelTitle.textContent = panelUsername.textContent = code.textContent = countdown.textContent = '';
  code.removeAttribute('aria-label');
  progress.value = 0;
  copy.disabled = true;
  integrationOff.hidden = true;
  showApp.hidden = false;
  openAppFeedback.hidden = true;
  openAppFeedback.textContent = '';
}
function rejectPending(error: Error): void {
  for (const operation of pending.values()) { clearTimeout(operation.timeout); operation.reject(error); }
  pending.clear();
}
function errorFor(value: unknown): Error {
  return new Error(typeof value === 'string' ? errors[value] ?? 'YourSafe could not complete this request. Refresh to try again.'
    : 'YourSafe is unavailable. Refresh to try again.', { cause: value });
}
function failure(error: unknown): void {
  authorizationToken = undefined;
  clearPanel();
  accounts.replaceChildren();
  const reason = error instanceof Error ? error.cause : undefined;
  if (!origin.textContent || reason === 'tabChanged') origin.textContent = 'Website unavailable';
  const needsUnlock = reason === 'integrationLocked';
  const off = needsUnlock || reason === 'integrationDisabled';
  integrationOff.hidden = !off;
  showApp.hidden = off;
  if (off) integrationGuide.textContent = needsUnlock
    ? 'Open YourSafe and unlock your vault first. Then enable Browser integration in Settings and reopen this popup.'
    : 'Open YourSafe on Windows, then enable Browser integration in Settings. Refresh here when it is on.';
  status.dataset.state = needsUnlock ? 'off-locked' : off ? 'off' : reason === 'locked' ? 'locked'
    : reason === 'desktopUnavailable' ? 'unavailable' : reason === 'protocolMismatch' ? 'incompatible' : 'error';
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
      if (!record(message) || message.action !== 'showApp') { connected = false; port.disconnect(); }
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
    if (value) codeExpired = now >= value.expiresAtUnixMs;
    displayed = undefined;
    code.textContent = '—';
    code.removeAttribute('aria-label');
    countdown.textContent = busy ? 'Waiting for approval…' : codeExpired ? 'Expired' : '';
    if (codeExpired && !busy && status.dataset.state !== 'success') {
      const instruction = 'Code expired. Go back to accounts and select View code again. Copy code requests the current code with a separate approval.';
      if (status.textContent !== instruction) status.textContent = instruction;
    }
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
  status.textContent = approvalStatus('viewTotp', active.account);
  try {
    const result = await request({ action: 'viewTotp', token: active.token, id: active.account.id });
    if (currentEpoch !== epoch || selected !== active) return;
    if (!isTotp(result)) throw errorFor('unavailable');
    displayed = result;
    paintTotp();
    status.dataset.state = 'ready';
    status.textContent = 'This code expires when the timer reaches 0. Copy code requests the current code with a separate approval in YourSafe on Windows.';
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
      status.textContent = approvalStatus('select', account);
      void request({ action: 'select', token, id: account.id }).then(() => {
        if (currentEpoch !== epoch) return;
        accounts.replaceChildren();
        status.dataset.state = 'success';
        status.textContent = 'Login details filled. Check them, then click the website\'s sign-in button.';
      }).catch(error => { if (currentEpoch === epoch) failure(error); }).finally(() => { if (currentEpoch === epoch) setBusy(false); });
    });
    actions.append(fill);
  }
  if (account.hasTotp) {
    const view = document.createElement('button');
    view.type = 'button';
    view.textContent = 'View code';
    view.dataset.credentialId = account.id;
    view.setAttribute('aria-label', 'View code for ' + account.title);
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
  authorizationToken = undefined;
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
    status.textContent = result.truncated ? 'Only part of your account list is shown. View all accounts in YourSafe on Windows.'
      : result.accounts.length ? 'Choose an action for an account below.'
        : 'No accounts match this website. In YourSafe on Windows, edit the account and set its URL to this website’s sign-in address. Then refresh or reopen this popup.';
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
    || (value.action !== undefined && value.action !== 'select' && value.action !== 'viewTotp' && value.action !== 'copyTotp')
    || (value.result !== undefined && !isTotp(value.result)
      && (!record(value.result) || (value.result.filled !== true && value.result.copied !== true)))) throw errorFor('unavailable');
  clearPanel();
  authorizationToken = value.token;
  origin.textContent = value.origin;
  accounts.replaceChildren();
  for (const account of value.accounts) accounts.append(accountRow(account, value.token));
  const account = value.accounts.find(item => item.id === value.selectedId);
  if (account && (value.action === 'viewTotp' || value.action === 'copyTotp')) {
    selected = { account, token: value.token };
    panelTitle.textContent = account.title;
    panelUsername.textContent = account.username || 'No username';
    accounts.hidden = true;
    panel.hidden = false;
    displayed = isTotp(value.result) ? value.result : undefined;
    timer = setInterval(paintTotp, 100);
  }
  status.dataset.state = value.busy ? 'loading' : 'ready';
  status.textContent = value.busy ? approvalStatus(value.action, account)
    : record(value.result) && 'filled' in value.result && value.result.filled === true ? 'Login details filled. Check them, then click the website\'s sign-in button.'
    : record(value.result) && 'copied' in value.result && value.result.copied === true ? 'Copied the current code.'
    : isTotp(value.result) ? 'This code expires when the timer reaches 0. Copy code requests the current code with a separate approval in YourSafe on Windows.'
    : 'Choose an action for an account below.';
  setBusy(value.busy);
  if (!panel.hidden) paintTotp();
}
copy.addEventListener('click', () => {
  const active = selected;
  if (busy || !active) return;
  const currentEpoch = epoch;
  setBusy(true);
  status.textContent = approvalStatus('copyTotp', active.account);
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
  status.textContent = 'Choose an action for an account below.';
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
async function openYourSafe(): Promise<void> {
  if (openingApp) return;
  const currentEpoch = epoch;
  openingApp = true;
  showApp.disabled = integrationOffOpen.disabled = true;
  openAppFeedback.hidden = false;
  openAppFeedback.textContent = 'Opening YourSafe…';
  try {
    const result = await request({ action: 'showApp' });
    if (currentEpoch !== epoch) return;
    openAppFeedback.textContent = record(result) && result.shown === true
      ? busy ? 'YourSafe is open. Review the pending request on Windows.'
        : status.dataset.state === 'off-locked' ? 'Unlock your vault first, then enable Browser integration in Settings.'
          : !integrationOff.hidden ? 'Enable Browser integration in Settings, then click Refresh here.'
          : status.dataset.state === 'locked' ? 'Unlock your vault in YourSafe, then reopen this popup or use Refresh.'
            : 'YourSafe is open.'
      : 'YourSafe could not open its window. Open it from the taskbar.';
  } catch (error) {
    if (currentEpoch !== epoch) return;
    openAppFeedback.textContent = error instanceof Error && error.cause === 'desktopUnavailable'
      ? 'Could not connect to YourSafe. Open it from the Windows Start menu, then reopen this popup.'
      : error instanceof Error ? error.message : 'YourSafe could not open its window.';
  } finally {
    openingApp = false;
    showApp.disabled = integrationOffOpen.disabled = false;
  }
}
showApp.addEventListener('click', () => { void openYourSafe(); });
integrationOffOpen.addEventListener('click', () => { void openYourSafe(); });
window.addEventListener('pagehide', () => {
  clearPanel();
  accounts.replaceChildren();
  rejectPending(errorFor('targetChanged'));
  connected = false;
  port.disconnect();
});
async function open(): Promise<void> {
  const currentEpoch = epoch;
  setBusy(true);
  status.textContent = 'Checking the current request…';
  try {
    const result = await request({ action: 'resume' });
    if (epoch !== currentEpoch) return;
    if (!record(result) || typeof result.resumed !== 'boolean') throw errorFor('unavailable');
    if (result.resumed) renderSnapshot(result.interaction);
    else if (result.cancellation === 'approvalDeclined' || result.cancellation === 'tabChanged') failure(errorFor(result.cancellation));
    else { setBusy(false); await discover(); }
  } catch (error) { if (epoch === currentEpoch) failure(error); }
}
void open();
