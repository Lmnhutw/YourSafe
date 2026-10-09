import { canonicalOrigin } from './protocol';

export type Fields = { password: HTMLInputElement; username?: HTMLInputElement };
export function editable(input: HTMLInputElement): boolean {
  if (!input.isConnected || input.matches(':disabled') || input.readOnly || input.closest('[inert]')) return false;
  let opacity = 1;
  for (let element: Element | null = input; element; element = element.parentElement) {
    const style = getComputedStyle(element);
    opacity *= Number(style.opacity);
    for (const filter of style.filter.matchAll(/opacity\(\s*([\d.]+)(%)?\s*\)/g)) opacity *= Number(filter[1]) / (filter[2] ? 100 : 1);
    if (style.visibility !== 'visible' || style.display === 'none' || opacity < 0.1 || style.contentVisibility === 'hidden') return false;
  }
  const bounds = input.getBoundingClientRect();
  const left = Math.max(0, bounds.left), right = Math.min(innerWidth, bounds.right);
  const top = Math.max(0, bounds.top), bottom = Math.min(innerHeight, bounds.bottom);
  return right - left >= 2 && bottom - top >= 2 && document.elementFromPoint((left + right) / 2, (top + bottom) / 2) === input;
}
export function passwordField(input: HTMLInputElement): boolean {
  return input.type === 'password' && !input.autocomplete.split(/\s+/).includes('new-password') && editable(input);
}
function usernameField(input: HTMLInputElement): boolean {
  return (input.type === 'text' || input.type === 'email') && editable(input);
}
function safeForm(input: HTMLInputElement): boolean {
  const origin = canonicalOrigin(location.href);
  if (!origin) return false;
  const form = input.form;
  if (!form) return true;
  if (canonicalOrigin(form.action) !== origin) return false;
  return [...form.elements].every(element => !(element instanceof HTMLButtonElement || element instanceof HTMLInputElement)
    || !element.hasAttribute('formaction') || canonicalOrigin(element.formAction) === origin);
}
export function findFields(focused?: HTMLInputElement, selectedUsername?: HTMLInputElement): Fields | undefined {
  const passwords = [...document.querySelectorAll<HTMLInputElement>('input[type=password]')].filter(passwordField);
  const focusedPassword = focused && passwords.includes(focused) ? focused : undefined;
  const scopePassword = focused?.form ? passwords.filter(input => input.form === focused.form) : passwords;
  const password = focusedPassword ?? (scopePassword.length === 1 ? scopePassword[0] : undefined);
  if (!password || !safeForm(password)) return;
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
export function visibleFields(fields: Fields): Promise<boolean> {
  const targets = fields.username ? [fields.password, fields.username] : [fields.password];
  if (typeof IntersectionObserver === 'undefined' || targets.some(input => !editable(input))) return Promise.resolve(false);
  return new Promise<boolean>(resolve => {
    let observer: IntersectionObserver | undefined;
    const finish = (result: boolean) => { clearTimeout(timeout); observer?.disconnect(); resolve(result); };
    const timeout = setTimeout(() => finish(false), 1000);
    try {
      // Hit testing skips pointer-events:none covers; the browser's visibility tracker checks paint occlusion.
      const options = { threshold: 1, trackVisibility: true, delay: 100 };
      observer = new IntersectionObserver(entries => {
        const visible = new Set<Element>();
        for (const entry of entries) {
          if (!entry.isIntersecting || entry.intersectionRatio !== 1 || !('isVisible' in entry) || entry.isVisible !== true) {
            finish(false); return;
          }
          visible.add(entry.target);
        }
        if (targets.every(input => visible.has(input))) finish(true);
      }, options);
      if (!('trackVisibility' in observer) || observer.trackVisibility !== true) { finish(false); return; }
      for (const target of targets) observer.observe(target);
    } catch { finish(false); }
  });
}
export async function fillFields(fields: Fields, username: string, password: string, canFill: () => boolean = () => true): Promise<void> {
  const url = location.href;
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  if (!setter) throw new Error('unavailable');
  for (const [input, value] of [[fields.username, username], [fields.password, password]] as const) {
    if (!input) continue;
    // Username events can replace or reclassify the password field; check again before disclosing it.
    if (!await visibleFields(fields) || !canFill() || document.visibilityState !== 'visible'
      || location.href !== url || !sameFields(fields, findFields(fields.password, fields.username))) throw new Error('targetChanged');
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }
}
