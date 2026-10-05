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
const errors: Record<string, string> = {
  locked: 'Unlock YourSafe on the desktop, then refresh and choose your account again.',
  desktopUnavailable: 'Start YourSafe on the desktop and check browser integration registration.',
  ambiguousFields: 'Focus the username or password field you want to fill, then reopen YourSafe.',
  targetChanged: 'The page changed. Refresh and choose an account again.',
  unavailable: 'The credential is unavailable. Refresh and try again.'
};
async function request(message: unknown): Promise<unknown> {
  const response: unknown = await chrome.runtime.sendMessage(message);
  if (!record(response) || response.ok !== true) {
    const code = record(response) && typeof response.error === 'string' ? response.error : 'unavailable';
    throw new Error(errors[code] ?? 'YourSafe could not complete this request.');
  }
  return response.result;
}
function failure(error: unknown): void { status.textContent = error instanceof Error ? error.message : 'YourSafe is unavailable.'; }
async function discover(): Promise<void> {
  if (busy) return;
  busy = true;
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
      : result.accounts.length ? 'Choose an account to fill this site.' : 'No saved accounts match this origin.';
    for (const account of result.accounts) {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = `${account.title} — ${account.username || 'Password only'}`;
      button.addEventListener('click', () => {
        if (busy) return;
        busy = true;
        accounts.querySelectorAll('button').forEach(item => { item.disabled = true; });
        status.textContent = 'Filling…';
        void request({ action: 'select', token, id: account.id }).then(() => {
          accounts.replaceChildren(); status.textContent = 'Filled. Review the form before signing in.';
        }).catch(error => { accounts.replaceChildren(); failure(error); }).finally(() => { busy = false; });
      });
      accounts.append(button);
    }
  } catch (error) { failure(error); }
  finally { busy = false; }
}
refresh.addEventListener('click', () => { void discover(); });
showApp.addEventListener('click', () => {
  if (busy) return;
  busy = true;
  accounts.replaceChildren();
  void request({ action: 'showApp' }).then(result => {
    status.textContent = record(result) && result.shown === true
      ? 'Unlock on the desktop, then refresh and choose an account again.' : 'YourSafe could not open its window.';
  }).catch(failure).finally(() => { busy = false; });
});
void discover();
