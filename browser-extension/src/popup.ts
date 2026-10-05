import { isCredential, record } from './protocol';
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
let busy = false;
function setBusy(value: boolean): void {
  busy = value;
  refresh.disabled = showApp.disabled = value;
  accounts.setAttribute('aria-busy', String(value));
}
const errors: Record<string, string> = {
  locked: 'Your vault table is locked. Click Show YourSafe, choose Unlock Vault on the desktop, then refresh here.',
  desktopUnavailable: 'YourSafe is not connected. Start the matching desktop app, then refresh. If it is running, check browser host registration.',
  ambiguousFields: 'Focus the username or password field you want to fill, then reopen YourSafe.',
  targetChanged: 'The page changed. Refresh and choose an account again.',
  unavailable: 'The credential is unavailable. Refresh and try again.',
  unsupportedPage: 'Open an HTTPS sign-in page, then reopen YourSafe.'
};
async function request(message: unknown): Promise<unknown> {
  const response: unknown = await chrome.runtime.sendMessage(message);
  if (!record(response) || response.ok !== true) {
    const code = record(response) && typeof response.error === 'string' ? response.error : 'unavailable';
    throw new Error(errors[code] ?? 'YourSafe could not complete this request.');
  }
  return response.result;
}
function failure(error: unknown): void {
  if (!origin.textContent) origin.textContent = 'Website unavailable';
  status.dataset.state = 'error';
  status.textContent = error instanceof Error ? error.message : 'YourSafe is unavailable.';
}
async function discover(): Promise<void> {
  if (busy) return;
  setBusy(true);
  status.dataset.state = 'loading';
  accounts.replaceChildren();
  origin.textContent = '';
  status.textContent = 'Looking for accounts…';
  try {
    const result = await request({ action: 'discover' });
    if (!record(result) || typeof result.token !== 'string' || typeof result.origin !== 'string'
      || !Array.isArray(result.accounts) || !result.accounts.every(isCredential)
      || typeof result.truncated !== 'boolean') throw new Error('Invalid account response.');
    const token = result.token;
    origin.textContent = result.origin;
    status.textContent = result.truncated ? 'Some accounts could not be listed. Open YourSafe on the desktop to see all accounts.'
      : result.accounts.length ? 'Choose an account to fill this site.' : 'No accounts for this website. Add one in YourSafe with this exact website address, then refresh.';
    status.dataset.state = result.accounts.length ? 'ready' : 'empty';
    for (const account of result.accounts) {
      const button = document.createElement('button');
      button.type = 'button';
      const label = document.createElement('span');
      label.className = 'account-label';
      const title = document.createElement('strong');
      title.textContent = account.title;
      const username = document.createElement('span');
      username.textContent = account.username || 'Password only';
      label.append(title, username);
      button.append(label);
      button.setAttribute('aria-label', `Fill ${account.title}, ${account.username || 'password only'}`);
      button.addEventListener('click', () => {
        if (busy) return;
        setBusy(true);
        accounts.querySelectorAll('button').forEach(item => { item.disabled = true; });
        status.textContent = 'Filling…';
        void request({ action: 'select', token, id: account.id }).then(() => {
          accounts.replaceChildren(); status.dataset.state = 'success'; status.textContent = 'Filled. Review the form before signing in. YourSafe does not submit it.';
        }).catch(error => { accounts.replaceChildren(); failure(error); }).finally(() => { setBusy(false); });
      });
      accounts.append(button);
    }
  } catch (error) { failure(error); }
  finally { setBusy(false); }
}
refresh.addEventListener('click', () => { void discover(); });
showApp.addEventListener('click', () => {
  if (busy) return;
  setBusy(true);
  accounts.replaceChildren();
  void request({ action: 'showApp' }).then(result => {
    status.dataset.state = record(result) && result.shown === true ? 'ready' : 'error';
    status.textContent = record(result) && result.shown === true
      ? 'Unlock on the desktop, then refresh and choose an account again.' : 'YourSafe could not open its window.';
  }).catch(failure).finally(() => { setBusy(false); });
});
void discover();
