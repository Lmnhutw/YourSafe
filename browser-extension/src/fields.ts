export type Fields = { password: HTMLInputElement; username?: HTMLInputElement };
export function editable(input: HTMLInputElement): boolean {
  const style = getComputedStyle(input);
  return input.isConnected && !input.matches(':disabled') && !input.readOnly && input.getClientRects().length > 0
    && style.visibility === 'visible' && style.display !== 'none' && !input.closest('[inert]');
}
export function passwordField(input: HTMLInputElement): boolean {
  return input.type === 'password' && !input.autocomplete.split(/\s+/).includes('new-password') && editable(input);
}
function usernameField(input: HTMLInputElement): boolean {
  return (input.type === 'text' || input.type === 'email') && editable(input);
}
export function findFields(focused?: HTMLInputElement, selectedUsername?: HTMLInputElement): Fields | undefined {
  const passwords = [...document.querySelectorAll<HTMLInputElement>('input[type=password]')].filter(passwordField);
  const focusedPassword = focused && passwords.includes(focused) ? focused : undefined;
  const scopePassword = focused?.form ? passwords.filter(input => input.form === focused.form) : passwords;
  const password = focusedPassword ?? (scopePassword.length === 1 ? scopePassword[0] : undefined);
  if (!password) return;
  const inputs = password.form ? [...password.form.elements].filter((element): element is HTMLInputElement => element instanceof HTMLInputElement)
    : [...document.querySelectorAll<HTMLInputElement>('input')].filter(input => !input.form);
  const usernames = inputs.filter(usernameField);
  const explicit = usernames.filter(input => input.autocomplete.split(/\s+/).includes('username'));
  const email = usernames.filter(input => input.type === 'email');
  const username = selectedUsername && usernames.includes(selectedUsername) ? selectedUsername
    : focused && usernames.includes(focused) ? focused
    : explicit.length === 1 ? explicit[0] : email.length === 1 ? email[0] : usernames.length === 1 ? usernames[0] : undefined;
  if (usernames.length > 0 && !username) return;
  return username ? { password, username } : { password };
}
export function sameFields(first: Fields, second: Fields | undefined): boolean {
  return !!second && first.password === second.password && first.username === second.username;
}
export function fillFields(fields: Fields, username: string, password: string): void {
  const url = location.href;
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  if (!setter) throw new Error('unavailable');
  for (const [input, value] of [[fields.username, username], [fields.password, password]] as const) {
    if (!input) continue;
    // Username events can replace or reclassify the password field; check again before disclosing it.
    if (location.href !== url || !sameFields(fields, findFields(fields.password, fields.username))) throw new Error('targetChanged');
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }
}
